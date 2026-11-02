# Wire protocol

One TCP connection. The iPad app **listens** on port 7878, and the Windows host **connects** to it, either
through Apple's usbmux tunnel (USB) or directly (Wi-Fi).

Every message:

```
u8   type
u32  payload length (little-endian)
...  payload
```

All numbers are little-endian except the NAL lengths inside video messages (big-endian, AVCC style).
Coordinates are normalized to the streamed monitor: `0,0` is top-left and `1,1` is bottom-right.

## Host → iPad

| type | name | payload |
|---|---|---|
| `0x01` | HostHello | UTF-8 JSON `{"proto":1,"width":2732,"height":2048,"name":"LAPTOP"}` |
| `0x02` | Video | one complete H.264 access unit (one picture): repeated `[u32 BE length][NAL unit]`. SPS/PPS come with every keyframe (once a second). |
| `0x03` | HostInfo | UTF-8 JSON `{"encoder":"h264_qsv"}` |
| `0x04` | AudioFormat | UTF-8 JSON `{"sampleRate":48000,"channels":2}`, sent before any audio |
| `0x05` | Audio | interleaved 16-bit little-endian PCM (about 10 ms per message; nothing is sent while Windows is silent) |

## iPad → Host

| type | name | payload |
|---|---|---|
| `0x10` | ClientHello | UTF-8 JSON `{"proto":1,"device":"iPad","widthPx":2732,"heightPx":2048,"scale":2}` |
| `0x11` | Touch | `u8 count`, then `count` × `{u8 id (0-9), u8 phase, f32 x, f32 y, f32 pressure}`. Every frame lists **all** fingers currently down; fingers that didn't change are sent with phase `move`. |
| `0x12` | Pen | `u8 phase, f32 x, f32 y, f32 pressure (0-1), f32 tiltX°, f32 tiltY°` |
| `0x13` | Mouse | `u8 action, f32 x, f32 y, f32 dx, f32 dy` |

phase: `0` down, `1` move, `2` up, `3` cancel

mouse action: `0` move, `1` left down, `2` left up, `3` right down, `4` right up,
`5` scroll (dx/dy in wheel units, 120 = one notch; +dy = up, +dx = right), `6` zoom (dy wheel units with Ctrl held),
`7` middle click
