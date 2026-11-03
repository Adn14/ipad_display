import AVFoundation
import CoreMedia
import UIKit

/// Feeds H.264 access units from the host into an AVSampleBufferDisplayLayer: hardware decode, each
/// picture marked "display immediately" (no presentation queue, no timebase).
///
/// Zero-copy: the message arrives in a CMBlockBuffer the socket reader filled. The picture's NAL units
/// (already AVCC: [u32 BE length][NAL]) are wrapped with a block-buffer *reference* into that same
/// memory - only the parameter sets (SPS/PPS) are copied, and only when they change.
///
/// Threading: everything runs under `lock` on the caller's thread (the socket reader), including the
/// enqueue into the decoder. Submitting from the main thread instead adds a hop, and whatever the UI
/// is doing (touch handling, menus) delays the frame.
final class VideoRenderer {
    let displayLayer: AVSampleBufferDisplayLayer

    /// Main thread. New video dimensions (pixels).
    var onVideoSize: ((CGSize) -> Void)?
    /// Main thread. First picture of a connection was handed to the decoder.
    var onFirstFrame: (() -> Void)?

    private let renderer: AVSampleBufferVideoRenderer
    private let lock = NSLock()
    private var sps: Data?
    private var pps: Data?
    private var formatDescription: CMVideoFormatDescription?
    private var needsKeyframe = true
    private var deliveredFirstFrame = false
    private var videoSize = CGSize.zero
    private var statFrames = 0
    private var statBytes = 0

    /// Main thread.
    init(displayLayer: AVSampleBufferDisplayLayer) {
        self.displayLayer = displayLayer
        renderer = displayLayer.sampleBufferRenderer
        displayLayer.videoGravity = .resizeAspect
        displayLayer.backgroundColor = UIColor.black.cgColor
    }

    /// New connection: forget stream state and clear the picture.
    func reset() {
        lock.lock()
        defer { lock.unlock() }
        sps = nil
        pps = nil
        formatDescription = nil
        needsKeyframe = true
        deliveredFirstFrame = false
        renderer.flush(removingDisplayedImage: true, completionHandler: nil)
    }

    /// (frames, bytes) since the last call.
    func takeStats() -> (frames: Int, bytes: Int) {
        lock.lock()
        defer { statFrames = 0; statBytes = 0; lock.unlock() }
        return (statFrames, statBytes)
    }

    struct Result {
        /// Frame id to acknowledge - also for frames that couldn't be shown, so the host's pacing
        /// never stalls waiting for them.
        var frameId: UInt32?
        /// The decoder can't continue until the next keyframe (the host only sends them on request).
        var needsKeyframe = false
    }

    /// Reader thread. `block` holds one Video message (protocol v2: [u32 LE frame id] first).
    func handleVideo(_ block: CMBlockBuffer, length: Int, hasFrameId: Bool) -> Result {
        var raw: UnsafeMutablePointer<CChar>?
        guard CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil,
                                          totalLengthOut: nil, dataPointerOut: &raw) == kCMBlockBufferNoErr,
              let raw else { return Result() }
        let base = UnsafeRawPointer(raw)

        lock.lock()
        defer { lock.unlock() }
        statFrames += 1
        statBytes += length

        var o = 0
        var frameId: UInt32?
        if hasFrameId {
            guard length >= 4 else { return Result() }
            frameId = UInt32(littleEndian: base.loadUnaligned(as: UInt32.self))
            o = 4
        }

        // Walk the NAL units in place. The picture (slices + SEI) normally follows the parameter sets
        // as one contiguous run: [SPS][PPS][SEI][IDR...] - that run becomes the sample, by reference.
        var pictureStart = -1, pictureEnd = -1
        var contiguous = true, isKeyframe = false, parameterSetsChanged = false
        while o + 4 <= length {
            let n = Int(UInt32(bigEndian: base.loadUnaligned(fromByteOffset: o, as: UInt32.self)))
            guard n > 0, o + 4 + n <= length else { break }
            let nal = base + o + 4
            switch nal.load(as: UInt8.self) & 0x1F {
            case 7:
                if Self.differs(sps, nal, n) { sps = Data(bytes: nal, count: n); parameterSetsChanged = true }
            case 8:
                if Self.differs(pps, nal, n) { pps = Data(bytes: nal, count: n); parameterSetsChanged = true }
            case 9:
                break // access unit delimiter: not part of the sample
            case let type:
                if type == 5 { isKeyframe = true }
                if pictureStart < 0 { pictureStart = o } else if o != pictureEnd { contiguous = false }
                pictureEnd = o + 4 + n
            }
            o += 4 + n
        }

        if parameterSetsChanged { rebuildFormatDescription() }
        guard let format = formatDescription, pictureStart >= 0 else {
            return Result(frameId: frameId, needsKeyframe: formatDescription == nil)
        }
        if needsKeyframe {
            guard isKeyframe else { return Result(frameId: frameId, needsKeyframe: true) }
            needsKeyframe = false
        }

        let sampleData = contiguous
            ? Self.reference(block, offset: pictureStart, length: pictureEnd - pictureStart)
            : Self.gatherPicture(base, from: pictureStart, to: length)
        guard let sampleData, let sampleBuffer = Self.makeSampleBuffer(sampleData, format) else { return Result(frameId: frameId) }

        if renderer.status == .failed || renderer.requiresFlushToResumeDecoding {
            // Decoder lost state (e.g. the app was in the background): restart at the next keyframe.
            renderer.flush()
            needsKeyframe = true
            return Result(frameId: frameId, needsKeyframe: true)
        }
        renderer.enqueue(sampleBuffer)

        if !deliveredFirstFrame {
            deliveredFirstFrame = true
            DispatchQueue.main.async { [weak self] in self?.onFirstFrame?() }
        }
        return Result(frameId: frameId)
    }

    // MARK: - Helpers

    private static func differs(_ stored: Data?, _ bytes: UnsafeRawPointer, _ count: Int) -> Bool {
        guard let stored, stored.count == count else { return true }
        return stored.withUnsafeBytes { memcmp($0.baseAddress!, bytes, count) != 0 }
    }

    /// A block buffer that refers to part of `block` - no copy; it keeps `block` alive.
    private static func reference(_ block: CMBlockBuffer, offset: Int, length: Int) -> CMBlockBuffer? {
        var sub: CMBlockBuffer?
        let status = CMBlockBufferCreateWithBufferReference(
            allocator: kCFAllocatorDefault, referenceBuffer: block,
            offsetToData: offset, dataLength: length, flags: 0, blockBufferOut: &sub)
        return status == kCMBlockBufferNoErr ? sub : nil
    }

    /// Fallback when parameter sets or delimiters sit between slices: copy just the picture NALs.
    private static func gatherPicture(_ base: UnsafeRawPointer, from start: Int, to end: Int) -> CMBlockBuffer? {
        var total = 0
        var o = start
        while o + 4 <= end {
            let n = Int(UInt32(bigEndian: base.loadUnaligned(fromByteOffset: o, as: UInt32.self)))
            guard n > 0, o + 4 + n <= end else { break }
            let type = base.load(fromByteOffset: o + 4, as: UInt8.self) & 0x1F
            if type != 7 && type != 8 && type != 9 { total += 4 + n }
            o += 4 + n
        }
        guard total > 0 else { return nil }

        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: total,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0, dataLength: total,
            flags: kCMBlockBufferAssureMemoryNowFlag, blockBufferOut: &block) == kCMBlockBufferNoErr,
              let block else { return nil }

        var written = 0
        o = start
        while o + 4 <= end {
            let n = Int(UInt32(bigEndian: base.loadUnaligned(fromByteOffset: o, as: UInt32.self)))
            guard n > 0, o + 4 + n <= end else { break }
            let type = base.load(fromByteOffset: o + 4, as: UInt8.self) & 0x1F
            if type != 7 && type != 8 && type != 9 {
                guard CMBlockBufferReplaceDataBytes(with: base + o, blockBuffer: block,
                                                    offsetIntoDestination: written, dataLength: 4 + n) == kCMBlockBufferNoErr
                else { return nil }
                written += 4 + n
            }
            o += 4 + n
        }
        return block
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

    /// One sample, no timing: with DisplayImmediately the layer shows it at the next refresh.
    private static func makeSampleBuffer(_ data: CMBlockBuffer, _ format: CMVideoFormatDescription) -> CMSampleBuffer? {
        var sampleBuffer: CMSampleBuffer?
        var sampleSize = CMBlockBufferGetDataLength(data)
        let status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: data, formatDescription: format,
            sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil,
            sampleSizeEntryCount: 1, sampleSizeArray: &sampleSize, sampleBufferOut: &sampleBuffer)
        guard status == noErr, let sampleBuffer else { return nil }

        // Per-sample attachment (kCMSampleAttachmentKey_*), not a buffer-level one
        // (kCMSampleBufferAttachmentKey_*): this is the key the renderer honours per frame.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true),
           CFArrayGetCount(attachments) > 0 {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(dict,
                                 Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                 Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        }
        return sampleBuffer
    }
}
