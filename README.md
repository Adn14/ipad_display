# Second Screen — iPad as a touch monitor for Windows (USB-C)

Turns an iPad (built for the iPad Pro 12.9" 5th gen) into a real extended monitor for a Windows laptop
over a USB-C cable. The Windows cursor moves onto it like any second display, **and you can touch it**:

- **Fingers → Windows multi-touch.** Windows handles the gestures itself: tap = click, drag = scroll,
  pinch = zoom, press-and-hold = right-click, edge swipes.
- **Mouse mode** (switch in the app's `⋯` menu), for desktop apps that handle touch badly:
  tap = click, drag = click-drag, two-finger drag = scroll, pinch = Ctrl+wheel zoom, two-finger tap or
  long press = right-click, three-finger tap = middle-click.
- **Apple Pencil → Windows pen** with pressure and tilt (works in Windows Ink apps, OneNote, Krita…).
- **Trackpad or mouse connected to the iPad** moves and clicks the Windows cursor.
- **Sound (optional):** the iPad appears as a speaker in Windows. Choose it for the whole PC or for single apps,
  and the sound plays on the iPad (or on headphones/AirPods connected to it).

```
 Windows laptop                                                   iPad
 ┌──────────────────────────────────────────┐   USB-C cable   ┌────────────────────────────┐
 │ Virtual Display Driver (fake 2nd monitor)│                 │ Second Screen app          │
 │   ▼                                      │   Apple usbmux  │  hardware H.264 decode      │
 │ IPadScreenHost.exe                       │ ◄─────────────► │  (AVSampleBufferDisplayLayer)│
 │   FFmpeg ddagrab → GPU H.264 (QSV/NVENC/ │  TCP tunnel     │                            │
 │   AMF, x264 fallback) ──── video ───────►│  port 7878      │  touches / Pencil / mouse  │
 │   ◄──── touch/pen/mouse ── injects real  │                 │  ───────────────────────►  │
 │         Windows touch, pen & mouse input │                 │                            │
 └──────────────────────────────────────────┘                 └────────────────────────────┘
```

The USB link uses Apple's own usbmux service (part of iTunes / Apple Devices), the same channel
Xcode and the commercial wired-display apps use. No network is involved. A Wi-Fi mode is also there as a backup.

---

## 1. Windows setup (one time)

### a) Create the virtual monitor
Windows only extends the desktop onto monitors it thinks exist. Install the free, signed
**Virtual Display Driver** ([github.com/VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)).
In an **Administrator** terminal:

```powershell
winget install --id VirtualDrivers.Virtual-Display-Driver -e
```

Then in **Settings → System → Display**:
1. A second display appears. Choose **"Extend these displays"** and drag it to where the iPad sits
   relative to your laptop screen.
2. Select the new display and set **Display resolution** to a **4:3** mode: ideally **2732 × 2048**
   (the iPad's native resolution) with **Scale 200%**. **2048 × 1536** at 150% also looks good and is lighter.
   If 2732 × 2048 isn't in the list, add it as a custom resolution in **Virtual Driver Control**,
   the app installed with the driver.
3. Optional: for a portrait monitor, set the orientation to portrait and rotate the iPad.

> The driver's authors advise uninstalling it before major GPU driver updates and reinstalling
> afterwards.

### b) Turn on Apple's USB service
The host reaches the iPad through **Apple Mobile Device Support**, which listens on `127.0.0.1:27015`. On this
laptop iTunes (Microsoft Store version) is installed, but its device service is a startup task that is **off**:

- **Settings → Apps → Startup → turn ON "AppleMobileDeviceProcess"** (iTunes), then sign out and back in.
- *Or* install **Apple Devices** from the Microsoft Store and open it once.

Plug in the iPad, unlock it and tap **Trust** when asked.

### c) Optional: the iPad as a speaker
The Virtual Display Driver package also contains a **Virtual Audio Driver**. Install it from an
**Administrator** PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File host-windows\install-audio-driver.ps1
```

A new speaker appears in Windows. Choose it in the taskbar's volume flyout (arrow next to the volume slider)
for all sound, or per app in **Settings → System → Sound → Volume mixer**. Renaming it to "iPad"
(**Settings → System → Sound →** the device **→ Rename**) makes it easier to spot. The host picks up a device
named "iPad" or "Virtual Audio" automatically; any other output can be forwarded with `--audio-device NAME`.
The **Play laptop sound** item in the app's `⋯` menu mutes it on the iPad.

### d) FFmpeg
Already installed on this laptop (`winget install Gyan.FFmpeg`). The host finds it on PATH, or you can put
`ffmpeg.exe` next to `IPadScreenHost.exe`.

### e) The host program
`host-windows\dist\IPadScreenHost.exe` (rebuild with `host-windows\build.ps1`, which needs the .NET 9 SDK).
It is a single self-contained exe, and the GitHub Actions build produces it too.

---

## 2. Install the iPad app

iPad apps have to be compiled on macOS. You don't need a Mac, though:

### Option A: no Mac (GitHub builds it, Sideloadly installs it from Windows)
1. Push this folder to a GitHub repository (it can be private). The included workflow
   `.github/workflows/build.yml` runs on GitHub's free macOS machines.
2. Open the repo's **Actions** tab → latest **build** run → download the artifact **SecondScreen-unsigned-ipa**
   and unzip it to get `SecondScreen-unsigned.ipa`.
3. Install **Sideloadly** on Windows: `winget install iOSGods.Sideloadly`. With the iPad plugged in, drag the
   `.ipa` onto Sideloadly, enter your Apple ID and click **Start**. Sideloadly signs the app with your
   free Apple ID.
4. On the iPad:
   - **Settings → Privacy & Security → Developer Mode → On**, then restart. This switch only appears
     *after* the first sideloaded app is installed.
   - **Settings → General → VPN & Device Management** → trust your Apple ID's developer profile.
5. With a **free** Apple ID the app expires after **7 days**. Re-run Sideloadly to refresh it (your settings
   are kept). A paid Apple Developer account ($99/yr) makes it last a year.

### Option B: with a Mac and Xcode
```bash
brew install xcodegen
cd ipad-app && xcodegen generate && open SecondScreen.xcodeproj
```
Select the **SecondScreen** target → **Signing & Capabilities** → choose your team and set a unique bundle ID
→ choose your iPad → **Run**. Then enable Developer Mode on the iPad as described above.

---

## 3. Use it

1. Open **Second Screen** on the iPad. It shows "Waiting for your laptop…".
2. Plug in the USB-C cable.
3. Run `IPadScreenHost.exe` on the laptop. The picture appears within a couple of seconds. The host keeps
   running and reconnects automatically if you unplug and replug the cable or reopen the app.

The **`⋯` button** in the corner (drag it anywhere) switches between Touch and Mouse modes, shows
stats (fps, bitrate, encoder) and lists the gestures.

**Tip:** turn off **Settings → Multitasking & Gestures → Gestures** on the iPad, or use Guided Access, so
four- and five-finger iPad gestures don't pull you out of the app.

### Host options
```
IPadScreenHost.exe --list              show monitors (the iPad's is the non-primary one)
IPadScreenHost.exe --monitor 1         stream a specific monitor
IPadScreenHost.exe --encoder x264      force an encoder: auto | qsv | nvenc | amf | x264
IPadScreenHost.exe --fps 60 --bitrate 30
IPadScreenHost.exe --wifi 192.168.1.20 use Wi-Fi instead of USB (IP shown in the app)
IPadScreenHost.exe --audio-device "Speakers"   forward a different Windows output to the iPad
IPadScreenHost.exe --no-audio          keep sound on the laptop
IPadScreenHost.exe --test-input        check touch/pen/mouse injection works (moves the cursor)
IPadScreenHost.exe --verbose           FFmpeg output + live fps/bitrate
```

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Waiting for a second monitor" | Virtual Display Driver isn't installed/enabled, or Display settings are not on **Extend**. |
| "Apple's USB service … is not running" | See step 1b (turn on the iTunes startup item, or install Apple Devices). |
| "Waiting for the iPad on USB" | Unlock the iPad, tap **Trust**, try another cable or port (it must be a data cable, not charge-only). |
| "connection refused - is the Second Screen app open" | Open the app on the iPad and keep it in the foreground. |
| Black bars around the picture | Set the virtual monitor to a 4:3 resolution (step 1a). |
| Touch doesn't work in one particular app | That app runs as Administrator. Windows blocks input from normal programs into it; run the host as Administrator too. |
| A video looks black on the iPad | DRM-protected video (Netflix etc.) can't be captured; that's a Windows restriction. |
| Laggy or blocky | Try `--bitrate 50`, or a lower resolution such as 2048×1536. On hybrid laptops the encoder used is the one on the GPU that owns the virtual display; see `--verbose`. |

## Limitations
- Sound on the iPad needs the Virtual Audio Driver (step 1c). It runs about 50–150 ms behind the picture.
- The app must stay open in the foreground on the iPad. iPadOS stops background apps.
- With a free Apple ID you must re-sign the app every 7 days.

## Project layout
```
host-windows/     C# (.NET 9) Windows host: capture/encode via FFmpeg, usbmux client, input injection
ipad-app/         Swift/UIKit iPad app (XcodeGen project.yml)
docs/PROTOCOL.md  wire protocol between the two
tools/fake_ipad.py  stand-in iPad for testing the host without one
.github/workflows/build.yml  CI: unsigned .ipa + Windows exe
```
