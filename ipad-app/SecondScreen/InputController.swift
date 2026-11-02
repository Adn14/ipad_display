import AVFoundation
import UIKit

enum InputMode: String {
    /// Fingers become real Windows touch contacts; Windows interprets the gestures itself
    /// (tap = click, drag = scroll, pinch = zoom, press-and-hold = right click, ...).
    case touch
    /// Fingers drive the mouse: tap = click, drag = click-drag, two-finger drag = scroll wheel,
    /// pinch = Ctrl+wheel zoom, two-finger tap / long press = right click, three-finger tap = middle click.
    /// Useful for older desktop apps that handle touch poorly.
    case mouse
}

@MainActor
protocol TouchHandling: AnyObject {
    func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?)
    func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?)
    func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?)
    func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?)
}

/// Translates everything the user does on the iPad into protocol messages for the host.
/// Apple Pencil always becomes a Windows pen (pressure + tilt); a trackpad/mouse attached to the
/// iPad drives the Windows cursor; fingers follow `mode`.
@MainActor
final class InputController: NSObject, TouchHandling, UIPointerInteractionDelegate {
    var mode: InputMode = .touch {
        didSet { if oldValue != mode { cancelAll() } }
    }
    var send: ((Data) -> Void)?
    /// Size of the incoming video, to map touches inside the letterboxed picture.
    var videoSize: CGSize = .zero

    private weak var view: UIView?

    // touch mode: up to 10 contacts, each with a stable slot id 0...9
    private var slotOf: [ObjectIdentifier: UInt8] = [:]
    private var touchInSlot: [UInt8: UITouch] = [:]

    // mouse mode
    private enum MouseState { case idle, pending, dragging, twoFinger, consumed }
    private enum TwoFingerKind { case undecided, scroll, zoom }
    private var mState: MouseState = .idle
    private var mTouches: [UITouch] = []
    private var mTouchStart: [ObjectIdentifier: CGPoint] = [:]
    private var mStartPoint = CGPoint.zero
    private var mStartTime: CFTimeInterval = 0
    private var mMaxFingers = 0
    private var mMovedFar = false
    private var longPressTask: Task<Void, Never>?
    private var twoKind: TwoFingerKind = .undecided
    private var twoStartCentroid = CGPoint.zero
    private var twoLastCentroid = CGPoint.zero
    private var twoStartDistance: CGFloat = 0
    private var zoomAnchorDistance: CGFloat = 0
    private var scrollRemainder = CGPoint.zero
    private var lastTapPoint: CGPoint?
    private var lastTapTime: CFTimeInterval = 0

    // Apple Pencil / attached pointer
    private var pencilTouch: UITouch?
    private var pointerRightButton = false
    private var pointerScrollRemainder = CGPoint.zero

    private static let moveThreshold: CGFloat = 10      // points before a touch counts as a drag
    private static let scrollGain: CGFloat = 2.0        // wheel units per point of finger travel
    private static let tapMaxDuration: CFTimeInterval = 0.45
    private static let longPressDelay: UInt64 = 600_000_000

    init(view: UIView) {
        self.view = view
        super.init()

        // Trackpad / mouse attached to the iPad: hovering moves the Windows cursor...
        let hover = UIHoverGestureRecognizer(target: self, action: #selector(handleHover(_:)))
        hover.cancelsTouchesInView = false
        view.addGestureRecognizer(hover)

        // ...and two-finger trackpad scrolling / mouse wheel scrolls Windows. Never fingers on the glass.
        let scroll = UIPanGestureRecognizer(target: self, action: #selector(handlePointerScroll(_:)))
        scroll.allowedScrollTypesMask = .all
        scroll.allowedTouchTypes = []
        scroll.cancelsTouchesInView = false
        view.addGestureRecognizer(scroll)

        // Hide the iPad's own pointer over the picture; the Windows cursor is in the video.
        view.addInteraction(UIPointerInteraction(delegate: self))
    }

    func pointerInteraction(_ interaction: UIPointerInteraction, styleFor region: UIPointerRegion) -> UIPointerStyle? {
        UIPointerStyle.hidden()
    }

    // MARK: - Touch entry points (forwarded by DisplayView)

    func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) { route(touches, event, .down) }
    func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) { route(touches, event, .move) }
    func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) { route(touches, event, .up) }
    func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { route(touches, event, .cancel) }

    private func route(_ touches: Set<UITouch>, _ event: UIEvent?, _ phase: TouchPhaseCode) {
        var fingers = Set<UITouch>()
        for t in touches {
            switch t.type {
            case .pencil: handlePencil(t, event, phase)
            case .indirectPointer: handlePointer(t, event, phase)
            default: fingers.insert(t)
            }
        }
        guard !fingers.isEmpty else { return }
        switch mode {
        case .touch: touchModeFrame(fingers, phase)
        case .mouse: mouseMode(fingers, phase)
        }
    }

    /// Releases anything held down (mode switch, disconnect).
    func cancelAll() {
        if !touchInSlot.isEmpty {
            var b = ByteBuffer()
            let slots = touchInSlot.keys.sorted()
            b.u8(UInt8(slots.count))
            for s in slots {
                let (x, y) = normalized(touchInSlot[s]!.location(in: view))
                b.u8(s); b.u8(TouchPhaseCode.cancel.rawValue); b.f32(x); b.f32(y); b.f32(0.5)
            }
            send?(Message.frame(.touch, b.data))
        }
        slotOf.removeAll()
        touchInSlot.removeAll()

        if mState == .dragging, let t = mTouches.first { sendMouse(.leftUp, at: t.location(in: view)) }
        cancelLongPress()
        mState = .idle
        mTouches.removeAll()
        mTouchStart.removeAll()

        if let p = pencilTouch { sendPen(.cancel, p) }
        pencilTouch = nil
    }

    // MARK: - Touch mode

    private func touchModeFrame(_ changed: Set<UITouch>, _ phase: TouchPhaseCode) {
        if phase == .down {
            for t in changed where slotOf[ObjectIdentifier(t)] == nil {
                guard let free = (UInt8(0)..<UInt8(10)).first(where: { touchInSlot[$0] == nil }) else { continue }
                slotOf[ObjectIdentifier(t)] = free
                touchInSlot[free] = t
            }
        }

        // Each frame carries every finger currently down (Windows needs the full contact set).
        let slots = touchInSlot.keys.sorted()
        if !slots.isEmpty {
            var b = ByteBuffer()
            b.u8(UInt8(slots.count))
            for s in slots {
                let t = touchInSlot[s]!
                let ph: TouchPhaseCode = changed.contains(t) ? phase : .move
                let (x, y) = normalized(t.location(in: view))
                b.u8(s); b.u8(ph.rawValue); b.f32(x); b.f32(y); b.f32(0.5)
            }
            send?(Message.frame(.touch, b.data))
        }

        if phase == .up || phase == .cancel {
            for t in changed {
                if let s = slotOf.removeValue(forKey: ObjectIdentifier(t)) { touchInSlot[s] = nil }
            }
        }
    }

    // MARK: - Mouse mode

    private func mouseMode(_ changed: Set<UITouch>, _ phase: TouchPhaseCode) {
        let now = CACurrentMediaTime()
        switch phase {
        case .down:
            if mState == .idle {
                mStartTime = now
                mMovedFar = false
                mMaxFingers = 0
            }
            for t in changed where !mTouches.contains(t) {
                mTouches.append(t)
                mTouchStart[ObjectIdentifier(t)] = t.location(in: view)
            }
            mMaxFingers = max(mMaxFingers, mTouches.count)

            if mTouches.count == 1 && mState == .idle {
                mState = .pending
                mStartPoint = mTouches[0].location(in: view)
                sendMouse(.move, at: mStartPoint)
                scheduleLongPress()
            } else if mTouches.count == 2 && (mState == .pending || mState == .idle) {
                cancelLongPress()
                mState = .twoFinger
                twoKind = .undecided
                let (c, d) = centroidAndDistance()
                twoStartCentroid = c
                twoLastCentroid = c
                twoStartDistance = d
                zoomAnchorDistance = d
                scrollRemainder = .zero
            } else if mTouches.count >= 3 && mState != .dragging {
                cancelLongPress()
                mState = .consumed
            }

        case .move:
            for t in changed {
                if let s = mTouchStart[ObjectIdentifier(t)], distance(s, t.location(in: view)) > Self.moveThreshold {
                    mMovedFar = true
                }
            }
            switch mState {
            case .pending:
                guard let t = mTouches.first else { break }
                let p = t.location(in: view)
                if distance(p, mStartPoint) > Self.moveThreshold {
                    cancelLongPress()
                    mState = .dragging
                    sendMouse(.leftDown, at: mStartPoint)
                    sendMouse(.move, at: p)
                }
            case .dragging:
                if let t = mTouches.first, changed.contains(t) { sendMouse(.move, at: t.location(in: view)) }
            case .twoFinger where mTouches.count >= 2:
                handleTwoFingerMove()
            default:
                break
            }

        case .up, .cancel:
            let liftPoint = changed.first?.location(in: view) ?? mStartPoint
            let primaryLifted = mTouches.first.map { changed.contains($0) } ?? false
            mTouches.removeAll { changed.contains($0) }
            for t in changed { mTouchStart[ObjectIdentifier(t)] = nil }
            let quick = now - mStartTime < Self.tapMaxDuration

            switch mState {
            case .pending:
                cancelLongPress()
                if phase == .up && quick { click(at: mStartPoint) }
                mState = mTouches.isEmpty ? .idle : .consumed
            case .dragging:
                if primaryLifted || mTouches.isEmpty {
                    sendMouse(.leftUp, at: liftPoint)
                    mState = mTouches.isEmpty ? .idle : .consumed
                }
            case .twoFinger:
                if mTouches.isEmpty {
                    if phase == .up && twoKind == .undecided && !mMovedFar && quick && mMaxFingers == 2 {
                        sendMouse(.rightDown, at: twoStartCentroid)
                        sendMouse(.rightUp, at: twoStartCentroid)
                    }
                    mState = .idle
                }
            case .consumed:
                if mTouches.isEmpty {
                    if phase == .up && mMaxFingers == 3 && !mMovedFar && quick {
                        sendMouse(.middleClick, at: mStartPoint)
                    }
                    mState = .idle
                }
            case .idle:
                break
            }
        }
    }

    private func handleTwoFingerMove() {
        let (c, d) = centroidAndDistance()
        if twoKind == .undecided {
            if abs(d - twoStartDistance) > 30 {
                twoKind = .zoom
                zoomAnchorDistance = d
            } else if distance(c, twoStartCentroid) > Self.moveThreshold {
                twoKind = .scroll
                twoLastCentroid = c
            } else {
                return
            }
        }
        switch twoKind {
        case .scroll:
            let delta = CGPoint(x: c.x - twoLastCentroid.x, y: c.y - twoLastCentroid.y)
            twoLastCentroid = c
            emitScroll(delta: delta, at: c, remainder: &scrollRemainder)
        case .zoom:
            guard zoomAnchorDistance > 0 else { return }
            let ratio = d / zoomAnchorDistance
            if ratio > 1.15 {
                sendMouse(.zoom, at: c, dy: 120)
                zoomAnchorDistance = d
            } else if ratio < 1 / 1.15 {
                sendMouse(.zoom, at: c, dy: -120)
                zoomAnchorDistance = d
            }
        case .undecided:
            break
        }
    }

    /// Content follows the fingers ("natural" scrolling), like touch scrolling on Windows.
    private func emitScroll(delta: CGPoint, at p: CGPoint, remainder: inout CGPoint) {
        remainder.x += -delta.x * Self.scrollGain
        remainder.y += delta.y * Self.scrollGain
        let wx = remainder.x.rounded(.towardZero)
        let wy = remainder.y.rounded(.towardZero)
        guard wx != 0 || wy != 0 else { return }
        remainder.x -= wx
        remainder.y -= wy
        sendMouse(.scroll, at: p, dx: Float(wx), dy: Float(wy))
    }

    private func click(at p: CGPoint) {
        let now = CACurrentMediaTime()
        var target = p
        // Fingers never land on the same pixel twice; snap a quick second tap onto the first so
        // Windows recognises the double-click.
        if let last = lastTapPoint, now - lastTapTime < 0.5, distance(last, p) < 30 { target = last }
        sendMouse(.leftDown, at: target)
        sendMouse(.leftUp, at: target)
        lastTapPoint = target
        lastTapTime = now
    }

    private func scheduleLongPress() {
        cancelLongPress()
        longPressTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: InputController.longPressDelay)
            guard !Task.isCancelled, let self, self.mState == .pending else { return }
            self.sendMouse(.rightDown, at: self.mStartPoint)
            self.sendMouse(.rightUp, at: self.mStartPoint)
            self.mState = .consumed
        }
    }

    private func cancelLongPress() {
        longPressTask?.cancel()
        longPressTask = nil
    }

    private func centroidAndDistance() -> (CGPoint, CGFloat) {
        let pts = mTouches.prefix(2).map { $0.location(in: view) }
        guard pts.count == 2 else { return (pts.first ?? .zero, 0) }
        return (CGPoint(x: (pts[0].x + pts[1].x) / 2, y: (pts[0].y + pts[1].y) / 2),
                hypot(pts[0].x - pts[1].x, pts[0].y - pts[1].y))
    }

    // MARK: - Apple Pencil

    private func handlePencil(_ t: UITouch, _ event: UIEvent?, _ phase: TouchPhaseCode) {
        switch phase {
        case .down:
            pencilTouch = t
            sendPen(.down, t)
        case .move:
            guard t === pencilTouch else { return }
            // Coalesced samples give the full 240 Hz Pencil resolution: smoother ink.
            for s in event?.coalescedTouches(for: t) ?? [t] { sendPen(.move, s) }
        case .up, .cancel:
            guard t === pencilTouch else { return }
            sendPen(phase, t)
            pencilTouch = nil
        }
    }

    private func sendPen(_ phase: TouchPhaseCode, _ t: UITouch) {
        let (x, y) = normalized(t.preciseLocation(in: view))
        var pressure: Float = 0
        if phase == .down || phase == .move {
            let maxForce = t.maximumPossibleForce
            pressure = maxForce > 0 ? Float(t.force / maxForce) : 0.5
            pressure = min(max(pressure, 0.01), 1)
        }
        // altitude: 0 = flat on the glass, pi/2 = upright. azimuth: direction the barrel points.
        let altitude = Double(t.altitudeAngle)
        let azimuth = Double(t.azimuthAngle(in: view))
        let tanAlt = max(tan(altitude), 0.001)
        let tiltX = Float(atan(cos(azimuth) / tanAlt) * 180 / .pi)
        let tiltY = Float(atan(sin(azimuth) / tanAlt) * 180 / .pi)

        var b = ByteBuffer()
        b.u8(phase.rawValue); b.f32(x); b.f32(y); b.f32(pressure); b.f32(tiltX); b.f32(tiltY)
        send?(Message.frame(.pen, b.data))
    }

    // MARK: - Trackpad / mouse attached to the iPad

    private func handlePointer(_ t: UITouch, _ event: UIEvent?, _ phase: TouchPhaseCode) {
        let p = t.location(in: view)
        switch phase {
        case .down:
            pointerRightButton = event?.buttonMask.contains(.secondary) ?? false
            sendMouse(pointerRightButton ? .rightDown : .leftDown, at: p)
        case .move:
            sendMouse(.move, at: p)
        case .up, .cancel:
            sendMouse(pointerRightButton ? .rightUp : .leftUp, at: p)
        }
    }

    @objc private func handleHover(_ g: UIHoverGestureRecognizer) {
        switch g.state {
        case .began, .changed: sendMouse(.move, at: g.location(in: view))
        default: break
        }
    }

    @objc private func handlePointerScroll(_ g: UIPanGestureRecognizer) {
        guard let v = view else { return }
        switch g.state {
        case .began:
            pointerScrollRemainder = .zero
            g.setTranslation(.zero, in: v)
        case .changed:
            let t = g.translation(in: v)
            g.setTranslation(.zero, in: v)
            emitScroll(delta: t, at: g.location(in: v), remainder: &pointerScrollRemainder)
        default:
            break
        }
    }

    // MARK: - Helpers

    /// Maps a point in the view to 0...1 coordinates of the Windows monitor (inside the letterbox).
    private func normalized(_ p: CGPoint) -> (Float, Float) {
        guard let v = view else { return (0, 0) }
        let rect = videoSize.width > 0 && videoSize.height > 0
            ? AVMakeRect(aspectRatio: videoSize, insideRect: v.bounds)
            : v.bounds
        guard rect.width > 0, rect.height > 0 else { return (0, 0) }
        let x = min(max((p.x - rect.minX) / rect.width, 0), 1)
        let y = min(max((p.y - rect.minY) / rect.height, 0), 1)
        return (Float(x), Float(y))
    }

    private func sendMouse(_ action: MouseAction, at p: CGPoint, dx: Float = 0, dy: Float = 0) {
        let (x, y) = normalized(p)
        var b = ByteBuffer()
        b.u8(action.rawValue); b.f32(x); b.f32(y); b.f32(dx); b.f32(dy)
        send?(Message.frame(.mouse, b.data))
    }

    private func distance(_ a: CGPoint, _ b: CGPoint) -> CGFloat {
        hypot(a.x - b.x, a.y - b.y)
    }
}
