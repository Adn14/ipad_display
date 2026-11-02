import AVFoundation
import CoreMedia
import UIKit

/// Feeds H.264 access units from the host into an AVSampleBufferDisplayLayer (hardware decode,
/// shown immediately - no buffering, lowest latency).
final class VideoRenderer {
    let displayLayer: AVSampleBufferDisplayLayer

    /// Main thread. New video dimensions (pixels).
    var onVideoSize: ((CGSize) -> Void)?
    /// Main thread. First picture of a connection was handed to the decoder.
    var onFirstFrame: (() -> Void)?

    private let lock = NSLock()
    private var sps: Data?
    private var pps: Data?
    private var formatDescription: CMVideoFormatDescription?
    private var needsKeyframe = true
    private var deliveredFirstFrame = false
    private var videoSize = CGSize.zero
    private var statFrames = 0
    private var statBytes = 0

    init(displayLayer: AVSampleBufferDisplayLayer) {
        self.displayLayer = displayLayer
        displayLayer.videoGravity = .resizeAspect
        displayLayer.backgroundColor = UIColor.black.cgColor
    }

    /// New connection: forget stream state and clear the picture.
    func reset() {
        lock.lock()
        sps = nil
        pps = nil
        formatDescription = nil
        needsKeyframe = true
        deliveredFirstFrame = false
        lock.unlock()
        DispatchQueue.main.async { [displayLayer] in
            displayLayer.sampleBufferRenderer.flush(removingDisplayedImage: true, completionHandler: nil)
        }
    }

    /// (frames, bytes) since the last call.
    func takeStats() -> (frames: Int, bytes: Int) {
        lock.lock()
        defer { statFrames = 0; statBytes = 0; lock.unlock() }
        return (statFrames, statBytes)
    }

    /// Called on the network thread with one access unit: repeated [u32 big-endian length][NAL].
    func handleAccessUnit(_ payload: Data) {
        lock.lock()
        defer { lock.unlock() }
        statFrames += 1
        statBytes += payload.count

        var sample = Data()
        var isKeyframe = false
        var parameterSetsChanged = false

        payload.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            let p = raw.bindMemory(to: UInt8.self)
            guard let base = p.baseAddress else { return }
            var o = 0
            while o + 4 <= p.count {
                let n = Int(p[o]) << 24 | Int(p[o + 1]) << 16 | Int(p[o + 2]) << 8 | Int(p[o + 3])
                o += 4
                guard n > 0, o + n <= p.count else { break }
                let type = p[o] & 0x1F
                switch type {
                case 7:
                    let nal = Data(bytes: base + o, count: n)
                    if nal != sps { sps = nal; parameterSetsChanged = true }
                case 8:
                    let nal = Data(bytes: base + o, count: n)
                    if nal != pps { pps = nal; parameterSetsChanged = true }
                case 9:
                    break // access unit delimiter: not needed
                default:
                    if type == 5 { isKeyframe = true }
                    var len = UInt32(n).bigEndian
                    withUnsafeBytes(of: &len) { sample.append(contentsOf: $0) }
                    sample.append(base + o, count: n)
                }
                o += n
            }
        }

        if parameterSetsChanged { rebuildFormatDescription() }
        guard let fd = formatDescription, !sample.isEmpty else { return }
        if needsKeyframe {
            guard isKeyframe else { return } // can't decode until the next keyframe
            needsKeyframe = false
        }
        guard let sampleBuffer = makeSampleBuffer(sample, fd) else { return }

        let first = !deliveredFirstFrame
        deliveredFirstFrame = true
        DispatchQueue.main.async { [weak self] in
            self?.enqueue(sampleBuffer)
            if first { self?.onFirstFrame?() }
        }
    }

    // Must hold `lock`.
    private func rebuildFormatDescription() {
        guard let sps, let pps else { return }
        var newFormat: CMVideoFormatDescription?
        let status: OSStatus = sps.withUnsafeBytes { (s: UnsafeRawBufferPointer) -> OSStatus in
            pps.withUnsafeBytes { (q: UnsafeRawBufferPointer) -> OSStatus in
                let pointers: [UnsafePointer<UInt8>] = [
                    s.bindMemory(to: UInt8.self).baseAddress!,
                    q.bindMemory(to: UInt8.self).baseAddress!,
                ]
                let sizes: [Int] = [sps.count, pps.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: 2,
                    parameterSetPointers: pointers,
                    parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &newFormat)
            }
        }
        guard status == noErr, let newFormat else { return }
        formatDescription = newFormat
        needsKeyframe = true

        let dims = CMVideoFormatDescriptionGetPresentationDimensions(newFormat, usePixelAspectRatio: true, useCleanAperture: true)
        let size = CGSize(width: dims.width, height: dims.height)
        if size != videoSize {
            videoSize = size
            DispatchQueue.main.async { [weak self] in self?.onVideoSize?(size) }
        }
    }

    private func makeSampleBuffer(_ data: Data, _ format: CMVideoFormatDescription) -> CMSampleBuffer? {
        var block: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: data.count,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil,
            offsetToData: 0, dataLength: data.count, flags: 0, blockBufferOut: &block)
        guard status == kCMBlockBufferNoErr, let block else { return nil }

        status = data.withUnsafeBytes { (buf: UnsafeRawBufferPointer) -> OSStatus in
            CMBlockBufferReplaceDataBytes(with: buf.baseAddress!, blockBuffer: block,
                                          offsetIntoDestination: 0, dataLength: data.count)
        }
        guard status == kCMBlockBufferNoErr else { return nil }

        var sampleBuffer: CMSampleBuffer?
        var sampleSize = data.count
        status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format,
            sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil,
            sampleSizeEntryCount: 1, sampleSizeArray: &sampleSize, sampleBufferOut: &sampleBuffer)
        guard status == noErr, let sampleBuffer else { return nil }

        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true),
           CFArrayGetCount(attachments) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict,
                                 Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }
        return sampleBuffer
    }

    // Main thread.
    private func enqueue(_ sampleBuffer: CMSampleBuffer) {
        let renderer = displayLayer.sampleBufferRenderer
        if renderer.status == .failed || renderer.requiresFlushToResumeDecoding {
            // Decoder lost state (e.g. app was in the background): restart at the next keyframe.
            renderer.flush()
            lock.lock()
            needsKeyframe = true
            lock.unlock()
            return
        }
        renderer.enqueue(sampleBuffer)
    }
}
