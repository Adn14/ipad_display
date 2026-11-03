"""
Stand-in for the iPad app, for testing the Windows host without an iPad.

Listens like the app does, receives the stream, checks that every Video message is exactly one
complete H.264 picture, and writes it to an .h264 file you can play with ffplay. Speaks protocol v2
(frame ids + acks) by default; --ack-delay-ms simulates a slow client to exercise frame pacing.

    python tools/fake_ipad.py --seconds 10 --out capture.h264
    IPadScreenHost.exe --wifi 127.0.0.1 --monitor 0 --no-input      (in another terminal)

Check that drops never broke the reference chain:
    ffmpeg -v error -i capture.h264 -f null -        (no output = decoded cleanly)
"""
import argparse, heapq, json, socket, struct, sys, threading, time


def recv_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise EOFError
        buf += chunk
    return bytes(buf)


def send(sock, lock, mtype, payload):
    with lock:
        sock.sendall(struct.pack("<BI", mtype, len(payload)) + payload)


class DelayedAcker(threading.Thread):
    """Sends frame acks after a delay, like a client whose decoder or link is slow."""

    def __init__(self, sock, lock):
        super().__init__(daemon=True)
        self.sock, self.lock = sock, lock
        self.heap, self.cv, self.stop = [], threading.Condition(), False

    def ack(self, frame_id, delay_s):
        with self.cv:
            heapq.heappush(self.heap, (time.time() + delay_s, frame_id))
            self.cv.notify()

    def run(self):
        while True:
            with self.cv:
                while not self.stop and (not self.heap or self.heap[0][0] > time.time()):
                    self.cv.wait(timeout=(self.heap[0][0] - time.time()) if self.heap else None)
                if self.stop:
                    return
                _, fid = heapq.heappop(self.heap)
            try:
                send(self.sock, self.lock, 0x14, struct.pack("<I", fid))
            except OSError:
                return


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=7878)
    ap.add_argument("--seconds", type=float, default=8)
    ap.add_argument("--out", default="capture.h264")
    ap.add_argument("--proto", type=int, default=2)
    ap.add_argument("--ack-delay-ms", type=float, default=0, help="delay before acking each frame")
    ap.add_argument("--slow-from", type=float, default=0, help="apply the ack delay from this second...")
    ap.add_argument("--request-keyframe-at", type=float, default=-1, help="send a keyframe request at this second")
    ap.add_argument("--show-gaps", type=float, default=0, help="print inter-frame gaps above this many ms")
    ap.add_argument("--slow-to", type=float, default=1e9, help="...until this second")
    a = ap.parse_args()

    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("0.0.0.0", a.port))
    srv.listen(1)
    print(f"listening on {a.port}...", flush=True)
    conn, addr = srv.accept()
    conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    lock = threading.Lock()
    print("host connected from", addr, flush=True)
    hello = {"proto": a.proto, "device": "FakeIPad", "widthPx": 2732, "heightPx": 2048, "scale": 2}
    if a.proto >= 2:
        hello["acks"] = True
    send(conn, lock, 0x10, json.dumps(hello).encode())
    acker = DelayedAcker(conn, lock)
    acker.start()

    out = open(a.out, "wb")
    aus = bad = idr = id_errors = 0
    audio_bytes = peak = 0
    total = 0
    host_proto = 1
    last_id = None
    first_video = None
    t_end = None
    max_gap_ms = 0.0
    requested = False
    idr_times = []
    last_arrival = None
    while True:
        mtype, length = struct.unpack("<BI", recv_exact(conn, 5))
        payload = recv_exact(conn, length)
        if mtype in (0x01, 0x03, 0x04):
            print("JSON", hex(mtype), payload.decode(), flush=True)
            if mtype == 0x01:
                host_proto = json.loads(payload).get("proto", 1)
            continue
        if mtype == 0x05:
            audio_bytes += length
            peak = max(peak, max((abs(s) for s in struct.unpack(f"<{length // 2}h", payload)), default=0))
            continue
        if mtype != 0x02:
            print("unknown type", mtype)
            continue
        now = time.perf_counter()
        if first_video is None:
            first_video = now
            t_end = now + a.seconds
        if last_arrival is not None:
            gap = (now - last_arrival) * 1000
            max_gap_ms = max(max_gap_ms, gap)
            if a.show_gaps and gap > a.show_gaps:
                print(f"gap {gap:.0f} ms at t={now - first_video:.2f}s before AU {aus + 1}", flush=True)
        last_arrival = now
        total += length

        o = 0
        frame_id = None
        if host_proto >= 2 and a.proto >= 2:
            (frame_id,) = struct.unpack("<I", payload[:4])
            o = 4
            if last_id is not None and frame_id != last_id + 1:
                id_errors += 1
            last_id = frame_id

        vcl_first, types = [], []
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
            idr_times.append(round(now - first_video, 2))
        if not ok:
            bad += 1
            print("BAD AU:", types, vcl_first[:5])
        aus += 1
        if aus <= 2:
            print(f"AU {aus}: id={frame_id} NAL types {types[:12]}{'...' if len(types) > 12 else ''}", flush=True)

        if a.request_keyframe_at >= 0 and not requested and now - first_video >= a.request_keyframe_at:
            requested = True
            send(conn, lock, 0x15, b"")
            print(f"keyframe requested at t={now - first_video:.2f}s", flush=True)
        if frame_id is not None:
            elapsed = now - first_video
            slow = a.slow_from <= elapsed < a.slow_to
            acker.ack(frame_id, a.ack_delay_ms / 1000 if slow else 0)
        if now >= t_end:
            break

    dur = time.perf_counter() - first_video
    print(f"IDR at t= {idr_times}", flush=True)
    print(f"RESULT aus={aus} bad={bad} idr={idr} id_errors={id_errors} fps={aus / dur:.1f} "
          f"mbps={total * 8 / dur / 1e6:.1f} max_gap_ms={max_gap_ms:.0f} "
          f"audio_seconds={audio_bytes / 4 / 48000:.2f} audio_peak={peak}", flush=True)
    out.close()
    with acker.cv:
        acker.stop = True
        acker.cv.notify()
    conn.close()
    sys.exit(1 if bad or id_errors or aus == 0 else 0)


if __name__ == "__main__":
    main()
