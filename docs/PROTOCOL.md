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
| `0x01` | HostHello | UTF-8 JSON `{"proto":2,"width":2732,"height":2048,"name":"LAPTOP"}` |
| `0x02` | Video | protocol 2: `u32 frame id` (LE), then one complete H.264 access unit (one picture) as repeated `[u32 BE length][NAL unit]`. Protocol 1: the access unit only. SPS/PPS come with every keyframe. Keyframes are sent at the start and on demand only. |
| `0x03` | HostInfo | UTF-8 JSON `{"encoder":"h264_qsv"}` |
| `0x04` | AudioFormat | UTF-8 JSON `{"sampleRate":48000,"channels":2}`, sent before any audio |
| `0x05` | Audio | interleaved 16-bit little-endian PCM (about 10 ms per message; nothing is sent while Windows is silent) |

## iPad → Host

| type | name | payload |
|---|---|---|
| `0x10` | ClientHello | UTF-8 JSON `{"proto":2,"acks":true,"device":"iPad","widthPx":2732,"heightPx":2048,"scale":2}` |
| `0x11` | Touch | `u8 count`, then `count` × `{u8 id (0-9), u8 phase, f32 x, f32 y, f32 pressure}`. Every frame lists **all** fingers currently down; fingers that didn't change are sent with phase `move`. |
| `0x12` | Pen | `u8 phase, f32 x, f32 y, f32 pressure (0-1), f32 tiltX°, f32 tiltY°` |
| `0x13` | Mouse | `u8 action, f32 x, f32 y, f32 dx, f32 dy` |
| `0x14` | FrameAck | protocol 2: `u32 frame id`, sent once the frame has been handed to the decoder (or discarded) |
| `0x15` | KeyframeRequest | protocol 2: empty. The decoder can't continue without an IDR (at most once a second) |

phase: `0` down, `1` move, `2` up, `3` cancel

mouse action: `0` move, `1` left down, `2` left up, `3` right down, `4` right up,
`5` scroll (dx/dy in wheel units, 120 = one notch; +dy = up, +dx = right), `6` zoom (dy wheel units with Ctrl held),
`7` middle click

## Versions and frame pacing

Each side announces its `proto` in its hello. The host waits up to 2 s for the ClientHello before it
starts video, then uses the lower of the two versions. Protocol 2 adds frame ids and acks, so the host
knows how many frames are in flight. When more than `--max-inflight` (default 3) are unacknowledged:

- pictures with `nal_ref_idc == 0` (nothing references them) are dropped freely;
- dropping any other picture breaks the reference chain, so the host then drops **every** frame until
  the next IDR keyframe, and requests one at once;
- IDR pictures and SPS/PPS are never dropped.

Drops happen on the host before serialization, so a dropped frame never touches the socket.

## Keyframes

There is no periodic keyframe: a keyframe redraws the whole picture at lower quality than the frames
that refine it, so still areas visibly "pulse" whenever one arrives. The stream starts with an IDR;
when one is needed later (frames dropped, or a KeyframeRequest from the iPad) the host restarts the
encoder, which begins with a fresh IDR. Restarts are at least 2 s apart; each freezes the picture
for about 0.7 s.
