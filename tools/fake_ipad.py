"""
Stand-in for the iPad app, for testing the Windows host without an iPad.

Listens like the app does, receives the stream, checks that every Video message is exactly one
complete H.264 picture, and writes it to an .h264 file you can play with ffplay.

    python tools/fake_ipad.py --seconds 10 --out capture.h264
    IPadScreenHost.exe --wifi 127.0.0.1 --monitor 0 --no-input      (in another terminal)
"""
import argparse, json, socket, struct, sys, time


def recv_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise EOFError
        buf += chunk
    return bytes(buf)


def send(sock, mtype, payload):
    sock.sendall(struct.pack("<BI", mtype, len(payload)) + payload)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=7878)
    ap.add_argument("--seconds", type=float, default=8)
    ap.add_argument("--out", default="capture.h264")
    a = ap.parse_args()

    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", a.port))
    srv.listen(1)
    print(f"listening on {a.port}...", flush=True)
    conn, addr = srv.accept()
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    print("host connected from", addr, flush=True)
    send(conn, 0x10, json.dumps({"proto": 1, "device": "FakeIPad", "widthPx": 2732, "heightPx": 2048, "scale": 2}).encode())

    out = open(a.out, "wb")
    aus = bad = idr = 0
    audio_bytes = peak = 0
    total = 0
    first_video = None
    t_end = None
    while True:
        mtype, length = struct.unpack("<BI", recv_exact(conn, 5))
        payload = recv_exact(conn, length)
        if mtype in (0x01, 0x03, 0x04):
            print("JSON", hex(mtype), payload.decode(), flush=True)
            continue
        if mtype == 0x05:
            audio_bytes += length
            peak = max(peak, max((abs(s) for s in struct.unpack(f"<{length // 2}h", payload)), default=0))
            continue
        if mtype != 0x02:
            print("unknown type", mtype)
            continue
        now = time.time()
        if first_video is None:
            first_video = now
            t_end = now + a.seconds
        total += length
        # parse [u32 BE len][nal]...
        o = 0
        vcl_first = []
        types = []
        while o < len(payload):
            (n,) = struct.unpack(">I", payload[o:o + 4])
            nal = payload[o + 4:o + 4 + n]
            o += 4 + n
            t = nal[0] & 0x1F
            types.append(t)
            if t in (1, 5):
                vcl_first.append((nal[1] & 0x80) != 0)
            out.write(b"\x00\x00\x00\x01" + nal)
        ok = len(vcl_first) >= 1 and vcl_first[0] and not any(vcl_first[1:]) and o == len(payload)
        if 5 in types:
            idr += 1
        if not ok:
            bad += 1
            print("BAD AU:", types, vcl_first[:5])
        aus += 1
        if aus <= 2:
            print(f"AU {aus}: NAL types {types[:12]}{'...' if len(types) > 12 else ''}", flush=True)
        if now >= t_end:
            break
    dur = time.time() - first_video
    print(f"RESULT aus={aus} bad={bad} idr={idr} fps={aus / dur:.1f} mbps={total * 8 / dur / 1e6:.1f} "
          f"audio_seconds={audio_bytes / 4 / 48000:.2f} audio_peak={peak}", flush=True)
    out.close()
    conn.close()
    sys.exit(1 if bad or aus == 0 else 0)


if __name__ == "__main__":
    main()
