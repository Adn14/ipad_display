using System.Runtime.InteropServices;
using static IPadScreenHost.Native;

namespace IPadScreenHost;

internal readonly record struct TouchPoint(byte Id, TouchPhase Phase, float X, float Y, float Pressure);

/// <summary>
/// Turns iPad input into Windows input on the target monitor:
///  - fingers  -> real multi-touch (synthetic touch digitizer), so Windows' own touch gestures work
///                (tap, drag-scroll, pinch-zoom, press-and-hold = right click, edge swipes...)
///  - Pencil   -> Windows pen with pressure and tilt (Windows Ink apps)
///  - mouse-mode gestures -> SendInput mouse clicks / wheel
/// Coordinates arrive normalized (0..1) relative to the streamed monitor.
/// </summary>
internal sealed class InputInjector : IDisposable
{
    private const int MaxTouches = 10;
    private readonly object _lock = new();
    private IntPtr _touchDevice, _penDevice;
    private MonitorInfo? _mon;

    // Active contacts, re-injected periodically so Windows doesn't time them out while a finger rests.
    private readonly Dictionary<byte, (int X, int Y, uint Pressure)> _touches = new();
    private (int X, int Y, uint Pressure, int TiltX, int TiltY)? _penContact;
    private long _lastTouchInject, _lastPenInject;
    private readonly Timer _keepAlive;
    private bool _warnedInjectFailure;

    public InputInjector()
    {
        _keepAlive = new Timer(_ => KeepAlive(), null, 50, 50);
    }

    public void SetMonitor(MonitorInfo mon)
    {
        lock (_lock) _mon = mon;
    }

    // ------------------------------------------------------------------ touch

    public void TouchFrame(ReadOnlySpan<TouchPoint> points)
    {
        lock (_lock)
        {
            if (_mon is null || points.Length == 0) return;
            if (_touchDevice == IntPtr.Zero)
            {
                _touchDevice = CreateSyntheticPointerDevice(PT_TOUCH, MaxTouches, POINTER_FEEDBACK_DEFAULT);
                if (_touchDevice == IntPtr.Zero) { WarnOnce("CreateSyntheticPointerDevice(touch)"); return; }
            }

            var frame = new List<POINTER_TYPE_INFO>(points.Length);
            var ended = new List<byte>();
            foreach (var p in points)
            {
                if (p.Id >= MaxTouches) continue;
                var (x, y) = ToScreen(p.X, p.Y);
                uint pressure = (uint)Math.Clamp(p.Pressure * 1024f, 1f, 1024f);
                bool known = _touches.ContainsKey(p.Id);
                uint flags;
                switch (p.Phase)
                {
                    case TouchPhase.Down or TouchPhase.Move:
                        flags = known ? POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT
                                      : POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                        _touches[p.Id] = (x, y, pressure);
                        break;
                    case TouchPhase.Up:
                        if (!known) continue;
                        flags = POINTER_FLAG_UP;
                        ended.Add(p.Id);
                        break;
                    default: // cancel
                        if (!known) continue;
                        flags = POINTER_FLAG_UP | POINTER_FLAG_CANCELED;
                        ended.Add(p.Id);
                        break;
                }
                frame.Add(MakeTouch(p.Id, flags, x, y, pressure));
            }

            // Every currently-down contact must appear in each injected frame.
            foreach (var (id, t) in _touches)
            {
                bool present = false;
                foreach (var f in frame) if (f.touchInfo.pointerInfo.pointerId == id) { present = true; break; }
                if (!present)
                    frame.Add(MakeTouch(id, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT, t.X, t.Y, t.Pressure));
            }

            foreach (var id in ended) _touches.Remove(id);
            if (frame.Count > 0) Inject(_touchDevice, frame.ToArray(), "touch");
            _lastTouchInject = Environment.TickCount64;
        }
    }

    /// <summary>Lifts every finger/pen (on disconnect) so nothing stays "stuck" down.</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_touches.Count > 0 && _touchDevice != IntPtr.Zero)
            {
                var frame = _touches.Select(kv => MakeTouch(kv.Key, POINTER_FLAG_UP | POINTER_FLAG_CANCELED, kv.Value.X, kv.Value.Y, kv.Value.Pressure)).ToArray();
                Inject(_touchDevice, frame, "touch");
            }
            _touches.Clear();
            if (_penContact is { } pc && _penDevice != IntPtr.Zero)
            {
                Inject(_penDevice, [MakePen(POINTER_FLAG_UP | POINTER_FLAG_CANCELED | POINTER_FLAG_INRANGE, pc.X, pc.Y, 0, 0, 0)], "pen");
                Inject(_penDevice, [MakePen(POINTER_FLAG_UPDATE, pc.X, pc.Y, 0, 0, 0)], "pen");
            }
            _penContact = null;
        }
    }

    private static POINTER_TYPE_INFO MakeTouch(uint id, uint flags, int x, int y, uint pressure)
    {
        var info = new POINTER_TYPE_INFO { type = PT_TOUCH };
        info.touchInfo.pointerInfo.pointerType = PT_TOUCH;
        info.touchInfo.pointerInfo.pointerId = id;
        info.touchInfo.pointerInfo.pointerFlags = flags;
        info.touchInfo.pointerInfo.ptPixelLocation = new POINT { X = x, Y = y };
        info.touchInfo.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE;
        info.touchInfo.rcContact = new RECT { Left = x - 2, Top = y - 2, Right = x + 2, Bottom = y + 2 };
        info.touchInfo.orientation = 90;
        info.touchInfo.pressure = pressure;
        return info;
    }

    // ------------------------------------------------------------------ pen

    public void Pen(TouchPhase phase, float nx, float ny, float pressure01, float tiltXDeg, float tiltYDeg)
    {
        lock (_lock)
        {
            if (_mon is null) return;
            if (_penDevice == IntPtr.Zero)
            {
                _penDevice = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
                if (_penDevice == IntPtr.Zero) { WarnOnce("CreateSyntheticPointerDevice(pen)"); return; }
            }
            var (x, y) = ToScreen(nx, ny);
            uint pressure = (uint)Math.Clamp(pressure01 * 1024f, 0f, 1024f);
            int tx = (int)Math.Clamp(MathF.Round(tiltXDeg), -90, 90), ty = (int)Math.Clamp(MathF.Round(tiltYDeg), -90, 90);

            switch (phase)
            {
                case TouchPhase.Down or TouchPhase.Move when _penContact is null:
                    // Hover into range first, then touch down - the sequence a real pen produces.
                    Inject(_penDevice, [MakePen(POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE, x, y, 0, tx, ty)], "pen");
                    Inject(_penDevice, [MakePen(POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT, x, y, pressure, tx, ty)], "pen");
                    _penContact = (x, y, pressure, tx, ty);
                    break;
                case TouchPhase.Down or TouchPhase.Move:
                    Inject(_penDevice, [MakePen(POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT, x, y, pressure, tx, ty)], "pen");
                    _penContact = (x, y, pressure, tx, ty);
                    break;
                default: // up / cancel: lift, then leave range (Apple Pencil on this iPad has no hover)
                    if (_penContact is null) return;
                    uint lift = POINTER_FLAG_UP | POINTER_FLAG_INRANGE | (phase == TouchPhase.Cancel ? POINTER_FLAG_CANCELED : 0);
                    Inject(_penDevice, [MakePen(lift, x, y, 0, tx, ty)], "pen");
                    Inject(_penDevice, [MakePen(POINTER_FLAG_UPDATE, x, y, 0, tx, ty)], "pen");
                    _penContact = null;
                    break;
            }
            _lastPenInject = Environment.TickCount64;
        }
    }

    private static POINTER_TYPE_INFO MakePen(uint flags, int x, int y, uint pressure, int tiltX, int tiltY)
    {
        var info = new POINTER_TYPE_INFO { type = PT_PEN };
        info.penInfo.pointerInfo.pointerType = PT_PEN;
        info.penInfo.pointerInfo.pointerId = 0;
        info.penInfo.pointerInfo.pointerFlags = flags;
        info.penInfo.pointerInfo.ptPixelLocation = new POINT { X = x, Y = y };
        info.penInfo.penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y;
        info.penInfo.pressure = pressure;
        info.penInfo.tiltX = tiltX;
        info.penInfo.tiltY = tiltY;
        return info;
    }

    // ------------------------------------------------------------------ mouse

    public void Mouse(MouseAction action, float nx, float ny, float dx, float dy)
    {
        lock (_lock)
        {
            if (_mon is null) return;
            var (x, y) = ToScreen(nx, ny);
            switch (action)
            {
                case MouseAction.Move: Send(MoveTo(x, y)); break;
                case MouseAction.LeftDown: Send(MoveTo(x, y), Button(MOUSEEVENTF_LEFTDOWN)); break;
                case MouseAction.LeftUp: Send(MoveTo(x, y), Button(MOUSEEVENTF_LEFTUP)); break;
                case MouseAction.RightDown: Send(MoveTo(x, y), Button(MOUSEEVENTF_RIGHTDOWN)); break;
                case MouseAction.RightUp: Send(MoveTo(x, y), Button(MOUSEEVENTF_RIGHTUP)); break;
                case MouseAction.MiddleClick: Send(MoveTo(x, y), Button(MOUSEEVENTF_MIDDLEDOWN), Button(MOUSEEVENTF_MIDDLEUP)); break;
                case MouseAction.Scroll:
                    var inputs = new List<INPUT> { MoveTo(x, y) };
                    if ((int)dy != 0) inputs.Add(Wheel(MOUSEEVENTF_WHEEL, (int)dy));
                    if ((int)dx != 0) inputs.Add(Wheel(MOUSEEVENTF_HWHEEL, (int)dx));
                    Send([.. inputs]);
                    break;
                case MouseAction.Zoom:
                    if ((int)dy == 0) break;
                    Send(MoveTo(x, y), Key(VK_CONTROL, false), Wheel(MOUSEEVENTF_WHEEL, (int)dy), Key(VK_CONTROL, true));
                    break;
            }
        }
    }

    private static INPUT MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        return new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT
            {
                dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
                dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1)),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            },
        };
    }

    private static INPUT Button(uint flag) => new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flag } };
    private static INPUT Wheel(uint flag, int amount) => new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flag, mouseData = amount } };
    private static INPUT Key(ushort vk, bool up) => new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } };

    private void Send(params INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            WarnOnce("SendInput");
    }

    /// <summary>Harmless check that injection works: hovers the pen and moves the mouse to the monitor's center. No clicks.</summary>
    public bool SelfTest()
    {
        lock (_lock)
        {
            if (_mon is null) return false;
            Log.Info($"struct sizes: POINTER_TYPE_INFO={Marshal.SizeOf<POINTER_TYPE_INFO>()} (expect 152), INPUT={Marshal.SizeOf<INPUT>()} (expect 40)");
            bool ok = true;
            var (x, y) = ToScreen(0.5f, 0.5f);

            _penDevice = _penDevice != IntPtr.Zero ? _penDevice : CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
            _touchDevice = _touchDevice != IntPtr.Zero ? _touchDevice : CreateSyntheticPointerDevice(PT_TOUCH, MaxTouches, POINTER_FEEDBACK_DEFAULT);
            Log.Info($"synthetic devices: pen={(_penDevice != IntPtr.Zero ? "ok" : "FAILED")} touch={(_touchDevice != IntPtr.Zero ? "ok" : "FAILED")}");
            ok &= _penDevice != IntPtr.Zero && _touchDevice != IntPtr.Zero;
            if (_penDevice != IntPtr.Zero)
            {
                var hover = new[] { MakePen(POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE, x, y, 0, 0, 0) };
                bool a = InjectSyntheticPointerInput(_penDevice, hover, 1);
                if (!a) { Thread.Sleep(5); a = InjectSyntheticPointerInput(_penDevice, hover, 1); }
                int e1 = Marshal.GetLastWin32Error();
                Thread.Sleep(300);
                bool b = InjectSyntheticPointerInput(_penDevice, [MakePen(POINTER_FLAG_UPDATE, x, y, 0, 0, 0)], 1);
                int e2 = Marshal.GetLastWin32Error();
                Log.Info($"pen hover in at ({x},{y}): {(a ? "ok" : $"FAILED (error {e1})")}, out: {(b ? "ok" : $"FAILED (error {e2})")}");
                ok &= a && b;
            }
            var inputs = new[] { MoveTo(x, y) };
            bool m = SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
            Log.Info($"mouse move to ({x},{y}): {(m ? "ok" : "FAILED")}");
            return ok && m;
        }
    }

    // ------------------------------------------------------------------ helpers

    private (int X, int Y) ToScreen(float nx, float ny)
    {
        var m = _mon!;
        int x = m.Left + (int)MathF.Round(Math.Clamp(nx, 0f, 1f) * (m.Width - 1));
        int y = m.Top + (int)MathF.Round(Math.Clamp(ny, 0f, 1f) * (m.Height - 1));
        return (x, y);
    }

    private void Inject(IntPtr device, POINTER_TYPE_INFO[] frame, string what)
    {
        if (InjectSyntheticPointerInput(device, frame, (uint)frame.Length)) return;
        // The first injection right after creating a device occasionally fails; one retry covers it.
        Thread.Sleep(5);
        if (!InjectSyntheticPointerInput(device, frame, (uint)frame.Length))
            WarnOnce($"InjectSyntheticPointerInput({what})");
    }

    private void WarnOnce(string api)
    {
        int err = Marshal.GetLastWin32Error();
        if (_warnedInjectFailure) { Log.Debug($"{api} failed, Win32 error {err}"); return; }
        _warnedInjectFailure = true;
        Log.Warn($"{api} failed (Win32 error {err}). " +
                 (err == 5 ? "Input into apps running as Administrator is blocked unless this host also runs as Administrator."
                           : "Input may not reach Windows."));
    }

    private void KeepAlive()
    {
        lock (_lock)
        {
            long now = Environment.TickCount64;
            if (_touches.Count > 0 && _touchDevice != IntPtr.Zero && now - _lastTouchInject > 60)
            {
                var frame = _touches.Select(kv => MakeTouch(kv.Key, POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,
                                                            kv.Value.X, kv.Value.Y, kv.Value.Pressure)).ToArray();
                Inject(_touchDevice, frame, "touch");
                _lastTouchInject = now;
            }
            if (_penContact is { } p && _penDevice != IntPtr.Zero && now - _lastPenInject > 60)
            {
                Inject(_penDevice, [MakePen(POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT, p.X, p.Y, p.Pressure, p.TiltX, p.TiltY)], "pen");
                _lastPenInject = now;
            }
        }
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        ReleaseAll();
        lock (_lock)
        {
            if (_touchDevice != IntPtr.Zero) DestroySyntheticPointerDevice(_touchDevice);
            if (_penDevice != IntPtr.Zero) DestroySyntheticPointerDevice(_penDevice);
            _touchDevice = _penDevice = IntPtr.Zero;
        }
    }
}
