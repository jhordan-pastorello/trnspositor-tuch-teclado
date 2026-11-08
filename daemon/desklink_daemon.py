#!/usr/bin/env python3
"""
DeskLink daemon (Linux / GNOME Wayland) - injecao via uinput.

Nao usa portal, nao abre dialogo, nao pede permissao: cria um mouse e um
teclado virtuais no kernel (/dev/uinput) e o GNOME os trata como hardware.

Requisitos (uma vez):
  - modulo uinput carregado
  - udev: /dev/uinput com grupo input, 0660
  - usuario no grupo input

Protocolo: [u8 tipo][u32 tamanho LE][payload]
  1 Hello JSON | 2 HelloAck JSON | 3 Pointer i32,i32 | 4 Button u8,u8
  5 Wheel i32,i32 | 6 Key u16,u8 | 7 Leave | 8 Heartbeat | 9 Clipboard UTF-8
"""
import fcntl
import hashlib
import json
import os
import queue
import select
import socket
import ssl
import struct
import subprocess
import sys
import threading
import time

PORT_TCP = 47810
PORT_UDP = 47811
DISCOVER_MAGIC = b"DESKLINK_DISCOVER1"

CFG_DIR = os.path.expanduser("~/.config/desklink")
CERT = os.path.join(CFG_DIR, "cert.pem")
KEY = os.path.join(CFG_DIR, "key.pem")
SCREEN_FILE = os.path.join(CFG_DIR, "screen.json")

# ---- uinput / input-event-codes ----
EV_SYN, EV_KEY, EV_REL, EV_ABS = 0x00, 0x01, 0x02, 0x03
SYN_REPORT = 0
REL_X, REL_Y, REL_HWHEEL, REL_WHEEL = 0x00, 0x01, 0x06, 0x08
ABS_X, ABS_Y = 0x00, 0x01
BTN_LEFT, BTN_RIGHT, BTN_MIDDLE, BTN_SIDE, BTN_EXTRA = 0x110, 0x111, 0x112, 0x113, 0x114
BTN_MAP = {1: BTN_LEFT, 2: BTN_MIDDLE, 3: BTN_RIGHT, 4: BTN_SIDE, 5: BTN_EXTRA}

UI_DEV_CREATE = 0x5501
UI_DEV_DESTROY = 0x5502
UI_SET_EVBIT = 0x40045564
UI_SET_KEYBIT = 0x40045565
UI_SET_RELBIT = 0x40045566
UI_SET_ABSBIT = 0x40045567
UI_ABS_SETUP = 0x401C5504
UI_DEV_SETUP = 0x405C5503

ABS_MAX = 32767
BUS_USB = 0x03


def log(*a):
    print(time.strftime("[%H:%M:%S]"), *a, file=sys.stderr, flush=True)


class UinputDevice:
    def __init__(self, name, keys=(), rels=(), abs_axes=()):
        self.fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
        fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_KEY)
        fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_SYN)
        if rels:
            fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_REL)
            for r in rels:
                fcntl.ioctl(self.fd, UI_SET_RELBIT, r)
        if abs_axes:
            fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_ABS)
            for ax in abs_axes:
                fcntl.ioctl(self.fd, UI_SET_ABSBIT, ax)
                info = struct.pack("iiiiii", 0, 0, ABS_MAX, 0, 0, 0)
                fcntl.ioctl(self.fd, UI_ABS_SETUP, struct.pack("HH", ax, 0) + info)
        for k in keys:
            fcntl.ioctl(self.fd, UI_SET_KEYBIT, k)

        setup = struct.pack("HHHH", BUS_USB, 0x1D6B, 0x0105, 1) \
            + name.encode()[:79].ljust(80, b"\0") + struct.pack("I", 0)
        fcntl.ioctl(self.fd, UI_DEV_SETUP, setup)
        fcntl.ioctl(self.fd, UI_DEV_CREATE)
        self._ev = struct.Struct("llHHi")

    def emit(self, etype, code, value):
        os.write(self.fd, self._ev.pack(0, 0, etype, code, value))

    def syn(self):
        self.emit(EV_SYN, SYN_REPORT, 0)

    def close(self):
        try:
            fcntl.ioctl(self.fd, UI_DEV_DESTROY)
        except Exception:
            pass
        try:
            os.close(self.fd)
        except Exception:
            pass


class Mouse(UinputDevice):
    """Ponteiro absoluto (estilo usb-tablet): sem aceleracao, posicao exata."""

    def __init__(self):
        super().__init__("DeskLink Virtual Tablet",
                         keys=(BTN_LEFT, BTN_RIGHT, BTN_MIDDLE, BTN_SIDE, BTN_EXTRA),
                         rels=(REL_WHEEL, REL_HWHEEL),
                         abs_axes=(ABS_X, ABS_Y))

    def move_abs(self, x, y, w, h):
        ax = int(x * ABS_MAX / max(1, w - 1))
        ay = int(y * ABS_MAX / max(1, h - 1))
        self.emit(EV_ABS, ABS_X, ax)
        self.emit(EV_ABS, ABS_Y, ay)
        self.syn()

    def button(self, btn, down):
        self.emit(EV_KEY, BTN_MAP.get(btn, BTN_LEFT), 1 if down else 0)
        self.syn()

    def wheel(self, dx, dy):
        if dx:
            self.emit(EV_REL, REL_HWHEEL, int(round(dx / 120.0)) or (1 if dx > 0 else -1))
        if dy:
            self.emit(EV_REL, REL_WHEEL, int(round(dy / 120.0)) or (1 if dy > 0 else -1))
        self.syn()


class Keyboard(UinputDevice):
    def __init__(self):
        codes = list(range(1, 128)) + list(range(0x160, 0x180))
        super().__init__("DeskLink Virtual Keyboard", keys=codes)

    def key(self, code, down):
        self.emit(EV_KEY, int(code), 1 if down else 0)
        self.syn()


# ------------------------------------------------------------------ config --


def load_json(path, key=None):
    try:
        with open(path, "r") as f:
            d = json.load(f)
            return d.get(key) if key else d
    except Exception:
        return None


def save_json(path, **data):
    try:
        os.makedirs(CFG_DIR, exist_ok=True)
        with open(path, "w") as f:
            json.dump(data, f)
    except Exception as e:
        log("falha ao salvar", path, e)


def detect_screen():
    """Resolucao atual: melhor modo do DRM, senao cache, senao 1920x1080."""
    try:
        for base in sorted(os.listdir("/sys/class/drm")):
            if "-" not in base:
                continue
            modes = "/sys/class/drm/%s/modes" % base
            status = "/sys/class/drm/%s/status" % base
            try:
                if open(status).read().strip() != "connected":
                    continue
                line = open(modes).readline().strip()
                if "x" in line:
                    w, h = line.split("x")
                    return int(w), int(h)
            except Exception:
                continue
    except Exception:
        pass
    c = load_json(SCREEN_FILE)
    if c and c.get("w"):
        return c["w"], c["h"]
    return 1920, 1080


# ------------------------------------------------------------------ TLS -----


def ensure_cert():
    os.makedirs(CFG_DIR, exist_ok=True)
    if not (os.path.exists(CERT) and os.path.exists(KEY)):
        log("gerando certificado TLS...")
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                        "-keyout", KEY, "-out", CERT, "-days", "3650",
                        "-subj", "/CN=desklink"], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        os.chmod(KEY, 0o600)


def fingerprint():
    out = subprocess.run(["openssl", "x509", "-in", CERT, "-noout", "-fingerprint", "-sha256"],
                         capture_output=True, text=True, check=True).stdout
    return out.strip().split("=", 1)[1].replace(":", "").upper()


# -------------------------------------------------------------- clipboard ---

_wl_lock = threading.Lock()
_wl_proc = [None]


def set_clipboard(text):
    _wl_copy(("wl-copy", "--type", "text/plain;charset=utf-8"), text.encode("utf-8", "replace"))


def set_clipboard_image(png):
    _wl_copy(("wl-copy", "--type", "image/png"), png)


def _wl_copy(cmd, data):
    with _wl_lock:
        old = _wl_proc[0]
        if old is not None and old.poll() is None:
            try:
                old.kill()
            except Exception:
                pass
        try:
            p = subprocess.Popen(list(cmd), stdin=subprocess.PIPE)
            p.stdin.write(data)
            p.stdin.close()
            _wl_proc[0] = p
        except Exception as e:
            log("wl-copy falhou:", e)


def clipboard_types():
    try:
        out = subprocess.run(["wl-paste", "--list-types"], capture_output=True, timeout=2)
        if out.returncode == 0:
            return out.stdout.decode("utf-8", "replace").split()
    except Exception:
        pass
    return []


def read_clipboard():
    """Retorna ('text', str) ou ('image', png_bytes) ou None."""
    types = clipboard_types()
    if not types:
        return None

    if "image/png" in types:
        try:
            out = subprocess.run(["wl-paste", "--type", "image/png"], capture_output=True, timeout=5)
            if out.returncode == 0 and out.stdout:
                return ("image", out.stdout)
        except Exception:
            pass

    try:
        out = subprocess.run(["wl-paste", "--no-newline"], capture_output=True, timeout=3)
        if out.returncode == 0:
            return ("text", out.stdout.decode("utf-8", "replace"))
    except Exception:
        pass
    return None


# ------------------------------------------------------------------ daemon --


class Daemon:
    def __init__(self):
        self.conn = None
        self.buf = b""
        self.last_clip_key = None
        self._send_lock = threading.Lock()
        self.remote_w, self.remote_h = detect_screen()
        save_json(SCREEN_FILE, w=self.remote_w, h=self.remote_h)

        self.mouse = Mouse()
        self.keyboard = Keyboard()
        log("uinput: mouse e teclado virtuais criados; tela %dx%d" % (self.remote_w, self.remote_h))

        self.active = False
        self.cx = 0
        self.cy = 0
        self.pressed_keys = set()
        self.pressed_buttons = set()

    # ---- entrada ----
    def move(self, x, y):
        self.active = True
        self.mouse.move_abs(x, y, self.remote_w, self.remote_h)

    def button(self, btn, down):
        self.mouse.button(btn, down)
        (self.pressed_buttons.add(btn) if down else self.pressed_buttons.discard(btn))

    def key(self, code, down):
        self.keyboard.key(code, down)
        (self.pressed_keys.add(code) if down else self.pressed_keys.discard(code))

    def leave(self):
        for k in list(self.pressed_keys):
            self.keyboard.key(k, 0)
        for b in list(self.pressed_buttons):
            self.mouse.button(b, False)
        self.pressed_keys.clear()
        self.pressed_buttons.clear()
        self.active = False

    # ---- rede ----
    def send(self, msg_type, payload=b""):
        with self._send_lock:
            if self.conn is None:
                return
            try:
                self.conn.sendall(struct.pack("<BI", msg_type, len(payload)) + payload)
            except OSError:
                self.conn = None

    def handle(self, msg_type, payload):
        if msg_type == 9:
            text = payload.decode("utf-8", "replace")
            self.last_clip_key = "text:" + hashlib.sha1(text.encode("utf-8", "replace")).hexdigest()
            set_clipboard(text)
            log("clipboard <- windows (%d chars)" % len(text))
        elif msg_type == 10:
            self.last_clip_key = "image:" + hashlib.sha1(payload).hexdigest()
            set_clipboard_image(payload)
            log("clipboard <- windows (imagem %d bytes)" % len(payload))
        elif msg_type == 1:
            info = json.loads(payload)
            log("cliente:", info.get("name"), info.get("w"), "x", info.get("h"))
            self.send(2, json.dumps({"v": 1, "name": socket.gethostname(),
                                     "w": self.remote_w, "h": self.remote_h}).encode())
        elif msg_type == 3:
            x, y = struct.unpack("<ii", payload)
            self.move(max(0, min(self.remote_w - 1, x)), max(0, min(self.remote_h - 1, y)))
        elif msg_type == 4:
            btn, down = struct.unpack("<BB", payload)
            self.button(btn, bool(down))
        elif msg_type == 5:
            dx, dy = struct.unpack("<ii", payload)
            self.mouse.wheel(dx, dy)
        elif msg_type == 6:
            code, down = struct.unpack("<HB", payload)
            self.key(code, bool(down))
        elif msg_type == 7:
            self.leave()

    def tcp_loop(self):
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ctx.load_cert_chain(CERT, KEY)
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("0.0.0.0", PORT_TCP))
        srv.listen(2)
        srv.settimeout(0.5)
        log("ouvindo em tcp/%d e udp/%d" % (PORT_TCP, PORT_UDP))

        while True:
            if self.conn is None:
                try:
                    raw, addr = srv.accept()
                except socket.timeout:
                    continue
                except OSError as e:
                    log("accept falhou:", e)
                    time.sleep(0.2)
                    continue
                try:
                    self.conn = ctx.wrap_socket(raw, server_side=True)
                    self.buf = b""
                    log("conectado:", addr[0])
                except (ssl.SSLError, OSError) as e:
                    log("TLS falhou:", e)
                    try:
                        raw.close()
                    except Exception:
                        pass
                    self.conn = None
                continue

            try:
                r, _, _ = select.select([self.conn], [], [], 0.2)
                if self.conn not in r:
                    continue
                chunk = self.conn.recv(65536)
                if not chunk:
                    raise ConnectionError("fim")
                self.buf += chunk
                while len(self.buf) >= 5:
                    mt, ln = struct.unpack("<BI", self.buf[:5])
                    if len(self.buf) < 5 + ln:
                        break
                    payload = self.buf[5:5 + ln]
                    self.buf = self.buf[5 + ln:]
                    try:
                        self.handle(mt, payload)
                    except Exception as e:
                        log("erro no evento", mt, e)
            except (OSError, ConnectionError, ssl.SSLError) as e:
                log("desconectado:", e)
                self.leave()
                try:
                    self.conn.close()
                except Exception:
                    pass
                self.conn = None
                self.buf = b""

    def clip_loop(self):
        # GNOME nao suporta wl-paste --watch; usamos um watcher GTK3 que avisa
        # por evento (sinal owner-change) - zero polling, zero engasgo.
        helper = os.path.join(os.path.dirname(os.path.abspath(__file__)), "clipwatch.py")
        try:
            self.sync_clipboard()
        except Exception as e:
            log("clipboard inicial:", e)

        while True:
            try:
                proc = subprocess.Popen([sys.executable, helper],
                                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
                for _line in proc.stdout:
                    try:
                        self.sync_clipboard()
                    except Exception as e:
                        log("clipboard:", e)
                proc.wait()
            except Exception as e:
                log("clipwatch:", e)
            time.sleep(2)

    def sync_clipboard(self):
        if self.conn is None:
            return

        # 1 processo: texto
        try:
            out = subprocess.run(["wl-paste", "--no-newline"], capture_output=True, timeout=2)
        except Exception:
            return

        if out.returncode == 0 and out.stdout:
            key = "text:" + hashlib.sha1(out.stdout).hexdigest()
            if key == self.last_clip_key:
                return
            self.last_clip_key = key
            text = out.stdout.decode("utf-8", "replace")
            self.send(9, text.encode("utf-8"))
            log("clipboard -> windows (%d chars)" % len(text))
            return

        # sem texto: pode ser imagem (print do GNOME)
        types = clipboard_types()
        if "image/png" not in types:
            return
        try:
            img = subprocess.run(["wl-paste", "--type", "image/png"], capture_output=True, timeout=5)
        except Exception:
            return
        if img.returncode != 0 or not img.stdout:
            return

        key = "image:" + hashlib.sha1(img.stdout).hexdigest()
        if key == self.last_clip_key:
            return
        self.last_clip_key = key
        self.send(10, img.stdout)
        log("clipboard -> windows (imagem %d bytes)" % len(img.stdout))


class Discovery(threading.Thread):
    daemon = True

    def __init__(self, d):
        super().__init__(name="discovery")
        self.d = d
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        except OSError:
            pass
        self.sock.bind(("0.0.0.0", PORT_UDP))

    def run(self):
        while True:
            try:
                data, addr = self.sock.recvfrom(1024)
            except OSError:
                continue
            if not data.startswith(DISCOVER_MAGIC):
                continue
            reply = json.dumps({"name": socket.gethostname(), "port": PORT_TCP,
                                "w": self.d.remote_w, "h": self.d.remote_h, "v": 1}).encode()
            try:
                self.sock.sendto(reply, addr)
            except OSError:
                pass


if __name__ == "__main__":
    if not os.access("/dev/uinput", os.W_OK):
        log("SEM acesso a /dev/uinput (grupo input / regra udev?)")
        sys.exit(2)
    ensure_cert()
    log("fingerprint:", fingerprint())
    d = Daemon()
    Discovery(d).start()
    threading.Thread(target=d.clip_loop, daemon=True, name="clip").start()
    try:
        d.tcp_loop()
    except KeyboardInterrupt:
        d.leave()
