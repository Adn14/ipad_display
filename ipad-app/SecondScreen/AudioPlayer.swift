import AVFoundation
import QuartzCore

/// Plays the laptop's sound (16-bit interleaved PCM from the host) through the iPad's speakers,
/// or whatever the iPad is using for audio (headphones, AirPods). Thread-safe.
final class AudioPlayer {
    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let queue = DispatchQueue(label: "secondscreen.audio", qos: .userInteractive)

    private var format: AVAudioFormat?
    private var enabled = true
    private var queuedFrames = 0
    private var generation = 0      // bumps on reset so callbacks from old buffers are ignored
    private var maxQueuedFrames = 0 // beyond this we're lagging: drop audio to stay in sync
    private var startFrames = 0     // small cushion before starting, against network jitter
    private var lastConsumed = CACurrentMediaTime()

    init() {
        engine.attach(player)
        NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil) { [weak self] _ in
            // Output route changed (headphones etc.): the engine stopped; restart on the next buffer.
            self?.queue.async { self?.resetPlayback() }
        }
        NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: nil) { [weak self] _ in
            // Siri, a call, another app's audio...: playback was paused; start clean on the next buffer.
            self?.queue.async { self?.resetPlayback() }
        }
    }

    func setEnabled(_ on: Bool) {
        queue.async { [self] in
            enabled = on
            if !on { resetPlayback() }
        }
    }

    /// Network thread: the host announced its audio format.
    func configure(sampleRate: Double, channels: Int) {
        queue.async { [self] in
            guard let fmt = AVAudioFormat(standardFormatWithSampleRate: sampleRate,
                                          channels: AVAudioChannelCount(max(1, min(channels, 2)))) else { return }
            resetPlayback()
            engine.stop()
            engine.disconnectNodeOutput(player)
            engine.connect(player, to: engine.mainMixerNode, format: fmt)
            format = fmt
            maxQueuedFrames = Int(sampleRate * 0.12)
            startFrames = Int(sampleRate * 0.03)
        }
    }

    /// Network thread: one chunk of interleaved little-endian Int16 samples.
    func enqueue(_ pcm: Data) {
        queue.async { [self] in
            guard enabled, let fmt = format else { return }
            let channels = Int(fmt.channelCount)
            let frames = pcm.count / (2 * channels)
            guard frames > 0 else { return }
            if queuedFrames > maxQueuedFrames {
                // Normally: we're behind, so drop this chunk to catch up. But if nothing has been played
                // for a while, the output stalled (e.g. iPadOS paused our audio): reset instead of
                // dropping forever.
                if CACurrentMediaTime() - lastConsumed < 0.5 { return }
                resetPlayback()
                engine.stop()
            }

            guard let buffer = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(frames)),
                  let out = buffer.floatChannelData else { return }
            buffer.frameLength = AVAudioFrameCount(frames)
            pcm.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
                for f in 0..<frames {
                    for c in 0..<channels {
                        let sample = raw.loadUnaligned(fromByteOffset: (f * channels + c) * 2, as: Int16.self)
                        out[c][f] = Float(Int16(littleEndian: sample)) / 32768
                    }
                }
            }

            if !engine.isRunning {
                let session = AVAudioSession.sharedInstance()
                try? session.setCategory(.playback, mode: .default, options: [.mixWithOthers])
                try? session.setPreferredIOBufferDuration(0.005)
                try? session.setActive(true)
                do { try engine.start() } catch { return }
            }

            let gen = generation
            queuedFrames += frames
            player.scheduleBuffer(buffer, completionCallbackType: .dataConsumed) { [weak self] _ in
                self?.queue.async {
                    guard let self, self.generation == gen else { return }
                    self.queuedFrames = max(0, self.queuedFrames - frames)
                    self.lastConsumed = CACurrentMediaTime()
                }
            }
            if !player.isPlaying && queuedFrames >= startFrames { player.play() }
        }
    }

    /// Connection ended.
    func stop() {
        queue.async { [self] in
            resetPlayback()
            engine.stop()
        }
    }

    // Must run on `queue`.
    private func resetPlayback() {
        generation += 1
        player.stop()
        queuedFrames = 0
        lastConsumed = CACurrentMediaTime()
    }
}
