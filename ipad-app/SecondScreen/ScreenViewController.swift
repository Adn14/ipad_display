import UIKit

final class ScreenViewController: UIViewController {
    private let displayView = DisplayView()
    private let overlay = WaitingOverlayView()
    private let statsLabel = UILabel()
    private let menuButton = UIButton(type: .system)

    private var stream: StreamController!
    private var input: InputController!
    private var statsTimer: Timer?
    private var encoderName = ""
    private var videoSize = CGSize.zero
    private var state: StreamController.State = .waiting

    private let defaults = UserDefaults.standard
    private var showStats: Bool {
        get { defaults.bool(forKey: "showStats") }
        set { defaults.set(newValue, forKey: "showStats"); updateStatsVisibility() }
    }
    private var playSound: Bool {
        get { defaults.object(forKey: "playSound") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "playSound"); stream?.audio.setEnabled(newValue); rebuildMenu() }
    }
    /// Menu button position as a fraction of the view size, so it survives rotation.
    private var buttonPosition: CGPoint {
        get {
            let x = defaults.object(forKey: "buttonX") as? Double ?? 0.97
            let y = defaults.object(forKey: "buttonY") as? Double ?? 0.05
            return CGPoint(x: x, y: y)
        }
        set {
            defaults.set(Double(newValue.x), forKey: "buttonX")
            defaults.set(Double(newValue.y), forKey: "buttonY")
        }
    }

    // MARK: - Lifecycle

    override func loadView() {
        view = UIView()
        view.backgroundColor = .black
    }

    override func viewDidLoad() {
        super.viewDidLoad()

        displayView.frame = view.bounds
        displayView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.addSubview(displayView)

        overlay.frame = view.bounds
        overlay.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.addSubview(overlay)

        statsLabel.font = .monospacedDigitSystemFont(ofSize: 13, weight: .medium)
        statsLabel.textColor = .white
        statsLabel.backgroundColor = UIColor.black.withAlphaComponent(0.55)
        statsLabel.layer.cornerRadius = 6
        statsLabel.layer.masksToBounds = true
        statsLabel.textAlignment = .center
        statsLabel.isUserInteractionEnabled = false
        view.addSubview(statsLabel)

        var config = UIButton.Configuration.filled()
        config.image = UIImage(systemName: "ellipsis", withConfiguration: UIImage.SymbolConfiguration(pointSize: 15, weight: .bold))
        config.cornerStyle = .capsule
        config.baseBackgroundColor = UIColor.darkGray.withAlphaComponent(0.6)
        config.baseForegroundColor = .white
        menuButton.configuration = config
        menuButton.alpha = 0.45
        menuButton.showsMenuAsPrimaryAction = true
        menuButton.frame = CGRect(x: 0, y: 0, width: 44, height: 44)
        menuButton.addGestureRecognizer(UIPanGestureRecognizer(target: self, action: #selector(dragMenuButton(_:))))
        view.addSubview(menuButton)

        input = InputController(view: displayView)
        input.mode = InputMode(rawValue: defaults.string(forKey: "inputMode") ?? "") ?? .touch
        displayView.touchHandler = input

        let streamController = StreamController(port: AppConfig.port, displayLayer: displayView.displayLayer)
        stream = streamController
        streamController.audio.setEnabled(playSound)
        input.send = { [weak streamController] data in streamController?.send(data) }
        stream.onStateChange = { [weak self] s in self?.apply(state: s) }
        stream.onHostInfo = { [weak self] info in
            self?.encoderName = info["encoder"] as? String ?? ""
        }
        stream.renderer.onVideoSize = { [weak self] size in
            self?.videoSize = size
            self?.input.videoSize = size
        }

        rebuildMenu()
        updateStatsVisibility()
        apply(state: .waiting)
        setNeedsUpdateOfScreenEdgesDeferringSystemGestures()
        setNeedsUpdateOfHomeIndicatorAutoHidden()
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        let screen = view.window?.windowScene?.screen
        let native = screen?.nativeBounds.size ?? .zero
        stream.setHello([
            "proto": 1,
            "device": UIDevice.current.model,
            "widthPx": Int(max(native.width, native.height)),
            "heightPx": Int(min(native.width, native.height)),
            "scale": Double(screen?.nativeScale ?? 2),
        ])
        stream.start()
        overlay.setAddresses(NetworkInfo.ipv4Addresses(), port: AppConfig.port)
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        layoutMenuButton()
        let size = statsLabel.intrinsicContentSize
        statsLabel.frame = CGRect(x: view.safeAreaInsets.left + 12, y: view.safeAreaInsets.top + 12,
                                  width: size.width + 16, height: size.height + 8)
    }

    // A monitor should be edge-to-edge and keep iPadOS swipes from stealing touches meant for Windows.
    override var prefersStatusBarHidden: Bool { true }
    override var prefersHomeIndicatorAutoHidden: Bool { true }
    override var preferredScreenEdgesDeferringSystemGestures: UIRectEdge { .all }

    // MARK: - State

    private func apply(state newState: StreamController.State) {
        state = newState
        switch newState {
        case .waiting:
            input.cancelAll()
            overlay.setStatus("Waiting for your laptop…", detail: nil)
            setOverlayVisible(true)
        case .connected(let host):
            overlay.setStatus("Connected to \(host)", detail: "Starting video…")
            setOverlayVisible(true)
        case .streaming:
            setOverlayVisible(false)
        }
        rebuildMenu()
    }

    private func setOverlayVisible(_ visible: Bool) {
        if visible { overlay.setAddresses(NetworkInfo.ipv4Addresses(), port: AppConfig.port) }
        UIView.animate(withDuration: 0.25) { self.overlay.alpha = visible ? 1 : 0 }
        overlay.isUserInteractionEnabled = visible
    }

    // MARK: - Menu

    private func rebuildMenu() {
        let touch = UIAction(title: "Touch", subtitle: "Windows touch gestures",
                             image: UIImage(systemName: "hand.point.up.left"),
                             state: input.mode == .touch ? .on : .off) { [weak self] _ in self?.setMode(.touch) }
        let mouse = UIAction(title: "Mouse", subtitle: "Tap = click, two fingers = scroll / right-click",
                             image: UIImage(systemName: "cursorarrow"),
                             state: input.mode == .mouse ? .on : .off) { [weak self] _ in self?.setMode(.mouse) }
        let modeMenu = UIMenu(title: "Finger input", options: .displayInline, children: [touch, mouse])

        let sound = UIAction(title: "Play laptop sound", subtitle: "Pick “Virtual Audio Driver” / “iPad” as the speaker in Windows",
                             image: UIImage(systemName: "speaker.wave.2"),
                             state: playSound ? .on : .off) { [weak self] _ in self?.playSound.toggle() }
        let stats = UIAction(title: "Show stats", image: UIImage(systemName: "speedometer"),
                             state: showStats ? .on : .off) { [weak self] _ in self?.showStats.toggle() }
        let help = UIAction(title: "Gestures & help", image: UIImage(systemName: "questionmark.circle")) { [weak self] _ in
            self?.showHelp()
        }
        menuButton.menu = UIMenu(title: "Second Screen", children: [modeMenu, UIMenu(options: .displayInline, children: [sound, stats, help])])
    }

    private func setMode(_ mode: InputMode) {
        input.mode = mode
        defaults.set(mode.rawValue, forKey: "inputMode")
        rebuildMenu()
    }

    private func showHelp() {
        let text = """
        TOUCH mode (default) - your fingers are a Windows touchscreen:
        • Tap = click, double-tap = double-click
        • Drag = scroll pages / drag things
        • Pinch = zoom, two-finger rotate where supported
        • Press and hold = right-click
        • Swipe in from the left/right edge = Windows Task View / notifications

        MOUSE mode - for apps that ignore touch:
        • Tap = click, drag = click-and-drag (select text, move windows)
        • Two-finger drag = scroll, pinch = zoom (Ctrl + wheel)
        • Two-finger tap or long press = right-click
        • Three-finger tap = middle-click

        Apple Pencil is always a Windows pen with pressure and tilt.
        A trackpad or mouse connected to the iPad moves the Windows cursor.

        Tip: turn off Settings > Multitasking & Gestures > "Gestures" on the iPad (or use Guided Access) so 4/5-finger iPad gestures don't interrupt.
        """
        let alert = UIAlertController(title: "Second Screen", message: text, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "OK", style: .default))
        present(alert, animated: true)
    }

    @objc private func dragMenuButton(_ g: UIPanGestureRecognizer) {
        let p = g.location(in: view)
        let b = view.bounds
        guard b.width > 0, b.height > 0 else { return }
        buttonPosition = CGPoint(x: min(max(p.x / b.width, 0), 1), y: min(max(p.y / b.height, 0), 1))
        layoutMenuButton()
    }

    private func layoutMenuButton() {
        let b = view.bounds.inset(by: view.safeAreaInsets).insetBy(dx: 26, dy: 26)
        guard b.width > 0, b.height > 0 else { return }
        let pos = buttonPosition
        menuButton.center = CGPoint(x: b.minX + pos.x * b.width, y: b.minY + pos.y * b.height)
    }

    // MARK: - Stats

    private func updateStatsVisibility() {
        statsLabel.isHidden = !showStats
        statsTimer?.invalidate()
        statsTimer = nil
        guard showStats else { return }
        _ = stream?.renderer.takeStats()
        statsTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            DispatchQueue.main.async { self?.refreshStats() }
        }
    }

    private func refreshStats() {
        guard let stream else { return }
        let s = stream.renderer.takeStats()
        var parts = ["\(s.frames) fps", String(format: "%.1f Mbps", Double(s.bytes) * 8 / 1_000_000)]
        if videoSize != .zero { parts.append("\(Int(videoSize.width))×\(Int(videoSize.height))") }
        if !encoderName.isEmpty { parts.append(encoderName) }
        parts.append(input.mode == .touch ? "touch" : "mouse")
        statsLabel.text = parts.joined(separator: "  ·  ")
        view.setNeedsLayout()
    }
}

/// Shown while no picture is streaming: status plus setup instructions.
final class WaitingOverlayView: UIView {
    private let statusLabel = UILabel()
    private let detailLabel = UILabel()
    private let addressLabel = UILabel()

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = UIColor(white: 0.07, alpha: 1)

        let icon = UIImageView(image: UIImage(systemName: "display.2",
                                              withConfiguration: UIImage.SymbolConfiguration(pointSize: 64, weight: .light)))
        icon.tintColor = .systemBlue

        let title = UILabel()
        title.text = "Second Screen"
        title.font = .systemFont(ofSize: 34, weight: .bold)
        title.textColor = .white

        statusLabel.font = .systemFont(ofSize: 20, weight: .semibold)
        statusLabel.textColor = .systemGreen
        detailLabel.font = .systemFont(ofSize: 15)
        detailLabel.textColor = .lightGray

        let steps = UILabel()
        steps.numberOfLines = 0
        steps.textColor = UIColor(white: 0.85, alpha: 1)
        steps.font = .systemFont(ofSize: 17)
        steps.text = """
        1.  Connect this iPad to the laptop with the USB-C cable (tap “Trust” if asked).
        2.  On the laptop, run IPadScreenHost.exe.
        3.  Keep this app open - the picture appears in a few seconds.
        """

        addressLabel.numberOfLines = 0
        addressLabel.textColor = .gray
        addressLabel.font = .monospacedSystemFont(ofSize: 13, weight: .regular)
        addressLabel.textAlignment = .center

        let stack = UIStackView(arrangedSubviews: [icon, title, statusLabel, detailLabel, steps, addressLabel])
        stack.axis = .vertical
        stack.alignment = .center
        stack.spacing = 14
        stack.setCustomSpacing(28, after: detailLabel)
        stack.setCustomSpacing(28, after: steps)
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            stack.widthAnchor.constraint(lessThanOrEqualToConstant: 620),
            stack.leadingAnchor.constraint(greaterThanOrEqualTo: leadingAnchor, constant: 24),
        ])
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    func setStatus(_ status: String, detail: String?) {
        statusLabel.text = status
        detailLabel.text = detail
        detailLabel.isHidden = detail == nil
    }

    func setAddresses(_ ips: [String], port: UInt16) {
        let wifi = ips.isEmpty ? "" : "\nWi-Fi instead of USB:  IPadScreenHost.exe --wifi \(ips[0])"
        addressLabel.text = "Listening on port \(port)\(wifi)"
    }
}
