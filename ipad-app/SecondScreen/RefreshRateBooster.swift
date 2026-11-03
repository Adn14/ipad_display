import QuartzCore
import UIKit

/// Asks ProMotion to stay at the panel's top refresh rate (120 Hz on iPad Pro) while streaming.
///
/// AVSampleBufferDisplayLayer shows a "display immediately" frame at the next screen refresh. When the
/// rest of the UI is idle, ProMotion drops the panel to a low rate and that wait can approach 16.7 ms
/// or more; holding a 120 Hz display link caps it at one 8.3 ms refresh.
///
/// This is a request, not a lock: iPadOS still limits it in Low Power Mode or when the device is hot.
/// It costs battery, so it only runs while a picture is streaming.
final class RefreshRateBooster {
    private var link: CADisplayLink?

    /// CADisplayLink retains its target. Targeting the view controller would keep it alive until the
    /// link is invalidated, so the target is this small object with no references back.
    private final class Target: NSObject {
        @objc func tick(_ link: CADisplayLink) {}
    }

    var isRunning: Bool { link != nil }

    /// Main thread. `maxFramesPerSecond` is the screen's maximumFramesPerSecond (120 on ProMotion).
    func start(maxFramesPerSecond: Int) {
        guard link == nil else { return }
        let fps = Float(max(maxFramesPerSecond, 60))
        let l = CADisplayLink(target: Target(), selector: #selector(Target.tick(_:)))
        l.preferredFrameRateRange = CAFrameRateRange(minimum: fps, maximum: fps, preferred: fps)
        l.add(to: .main, forMode: .common)
        link = l
    }

    /// Main thread.
    func stop() {
        link?.invalidate()
        link = nil
    }

    deinit {
        link?.invalidate()
    }
}
