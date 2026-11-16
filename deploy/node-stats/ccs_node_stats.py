#!/usr/bin/env python3
"""Агент узла фермы: отдаёт сырой срез датчиков по HTTP (только чтение).

Контракт — docs/adr/ADR-029-local-model-stats.md, раздел «Узлы фермы» (срез v1).
Агент ничего не толкует: ни уровней, ни порогов, ни агрегатов. Он отдаёт тексты
тех же файлов и тот же вывод nvidia-smi, что читает бэкенд CCS на своей машине;
разбор и история живут в CCS.

Схема работы:
  * фоновый поток раз в INTERVAL_S секунд собирает срез и кладёт его в хранилище;
  * HTTP-сервер (однопоточный HTTPServer: запросы идут строго по очереди) на
    GET /v1/slice отдаёт последний готовый срез, пересчитав ageMs; пока первого
    среза нет — 503;
  * зависший nvidia-smi ограничен тайм-аутом SMI_TIMEOUT_S: срез всё равно
    собирается (gpuCsv = null), а если застрянет вся сборка — растёт ageMs.

Агент не пишет в файлы, не открывает сокеты наружу, кроме единственного
слушающего, и исполняет только фиксированную команду nvidia-smi без шелла.
"""
import argparse
import ipaddress
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlsplit

SCHEMA_VERSION = 1
INTERVAL_S = 2.0
SMI_TIMEOUT_S = 1.0
DEFAULT_CONFIG = "/etc/ccs-node-stats.json"
GDDR6_PATH = "/run/ccs-gddr6/temps.json"
IPMI_PATH = "/run/ccs-ipmi/sdr.json"

# Потолки из ADR (бэкенд проверяет их повторно и агенту не доверяет).
MAX_BODY = 256 * 1024
MAX_GPU_ROWS = 16
MAX_HWMON_DIRS = 64
MAX_FILES = 4096
MAX_LINKS = 4096
MAX_STATVFS = 64
MAX_TEXT = 4096
MAX_MOUNTINFO = 64 * 1024
MAX_GDDR6 = 16 * 1024
MAX_IPMI = 16 * 1024
# Сырой mountinfo на хосте с docker бывает большим: читаем с запасом, потом фильтруем.
MAX_MOUNTINFO_RAW = 4 * 1024 * 1024

# Ровно запрос GpuSampler.QueryArgs (тот же порядок полей). Меняется он — меняется и v.
SMI_QUERY = (
    "--query-gpu=index,uuid,name,utilization.gpu,utilization.memory,memory.used,memory.total,"
    "temperature.gpu,fan.speed,clocks.sm,clocks.max.sm,clocks.mem,pstate,pcie.link.gen.current,"
    "pcie.link.width.current,power.draw,power.limit,clocks_event_reasons.sw_power_cap,"
    "clocks_event_reasons.hw_thermal_slowdown,clocks_event_reasons.sw_thermal_slowdown,pci.bus_id"
)
SMI_FORMAT = "--format=csv,noheader,nounits"

_HWMON_DIR = re.compile(r"hwmon([0-9]+)")
# В точности перечень путей из ADR: temp*_input/_label/_max/_crit и fan*_input/_label, * — число.
_HWMON_FILE = re.compile(r"(temp[0-9]+_(?:input|label|max|crit)|fan[0-9]+_(?:input|label))")
_NUM = re.compile(r"[0-9]+")
_OCTAL = re.compile(r"\\([0-7]{3})")


class Env:
    """Окружение сборки: корень файловой системы, nvidia-smi, statvfs. Подменяется в тестах."""

    def __init__(self, root="/", nvidia_smi=None, smi_timeout=SMI_TIMEOUT_S, statvfs=os.statvfs):
        self.root = root
        self.nvidia_smi = nvidia_smi
        self.smi_timeout = smi_timeout
        self.statvfs = statvfs

    def real(self, path):
        return os.path.join(self.root, path.lstrip("/"))


def read_text(real_path, cap):
    """Файл дословно или None: нет файла, нечитаем, длиннее cap (обрезать нельзя — число станет неправдой)."""
    try:
        with open(real_path, "rb") as f:
            data = f.read(cap + 1)
    except OSError:
        return None
    if len(data) > cap:
        return None
    return data.decode("utf-8", errors="replace")


def collect_hwmon(env, files, links):
    base = "/sys/class/hwmon"
    try:
        names = os.listdir(env.real(base))
    except OSError:
        return
    dirs = []
    for name in names:
        m = _HWMON_DIR.fullmatch(name)
        if m:
            dirs.append((int(m.group(1)), name))
    dirs.sort()
    taken = 0
    for _, dname in dirs:
        if taken >= MAX_HWMON_DIRS:
            break
        logical = base + "/" + dname
        real = env.real(logical)
        if not os.path.isfile(real + "/name"):
            continue
        taken += 1
        try:
            entries = os.listdir(real)
        except OSError:
            continue
        wanted = ["name"] + sorted(
            (e for e in entries if _HWMON_FILE.fullmatch(e)),
            key=lambda e: (e.split("_", 1)[1], int(_NUM.search(e).group()), e))
        for entry in wanted:
            if len(files) >= MAX_FILES:
                break
            text = read_text(real + "/" + entry, MAX_TEXT)
            if text is not None:
                files[logical + "/" + entry] = text
        device = real + "/device"
        if len(links) < MAX_LINKS and os.path.islink(device):
            try:
                links[logical + "/device"] = os.readlink(device)
            except OSError:
                pass


def collect_proc(env, files):
    for path in ("/proc/meminfo", "/proc/uptime"):
        text = read_text(env.real(path), MAX_TEXT)
        if text is not None:
            files[path] = text
    # Из /proc/stat — только суммарная строка «cpu »; загрузку считает бэкенд по двум срезам.
    stat = read_text(env.real("/proc/stat"), 1024 * 1024)
    if stat is not None:
        for line in stat.splitlines(keepends=True):
            if line.startswith("cpu "):
                files["/proc/stat"] = line if line.endswith("\n") else line + "\n"
                break


def filter_mountinfo(raw):
    """Строки mountinfo, у которых источник начинается с /dev/ (механический фильтр, не толкование)."""
    kept = []
    for line in raw.splitlines():
        parts = line.split(" ")
        try:
            sep = parts.index("-", 6)
        except ValueError:
            continue
        if len(parts) > sep + 2 and parts[sep + 2].startswith("/dev/"):
            kept.append(line)
    return kept


def unescape_mount(raw):
    return _OCTAL.sub(lambda m: chr(int(m.group(1), 8)), raw)


def collect_mounts(env, files):
    """Кладёт отфильтрованный mountinfo в files и возвращает список statvfs по его точкам."""
    raw = read_text(env.real("/proc/self/mountinfo"), MAX_MOUNTINFO_RAW)
    if raw is None:
        return []
    lines = filter_mountinfo(raw)
    text = "".join(line + "\n" for line in lines)
    if len(text.encode("utf-8")) > MAX_MOUNTINFO:
        return []
    files["/proc/self/mountinfo"] = text
    result = []
    seen = set()
    for line in lines:
        mount = line.split(" ")[4]
        if mount in seen or len(result) >= MAX_STATVFS:
            continue
        seen.add(mount)
        try:
            st = env.statvfs(unescape_mount(mount))
        except OSError:
            continue
        result.append({"mount": mount, "total": st.f_blocks * st.f_frsize, "free": st.f_bfree * st.f_frsize})
    return result


def collect_gpu_csv(env):
    """stdout nvidia-smi дословно (до 16 строк) или None: нет бинаря, код ≠ 0, тайм-аут."""
    if not env.nvidia_smi:
        return None
    try:
        proc = subprocess.Popen(
            [env.nvidia_smi, SMI_QUERY, SMI_FORMAT],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            start_new_session=True)
    except OSError:
        return None
    try:
        out, _ = proc.communicate(timeout=env.smi_timeout)
    except subprocess.TimeoutExpired:
        # Убиваем всю группу: зависшая команда не должна пережить ответ.
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except OSError:
            proc.kill()
        proc.wait()
        proc.stdout.close()
        return None
    if proc.returncode != 0:
        return None
    lines = out.decode("utf-8", errors="replace").splitlines(keepends=True)
    lines = [ln for ln in lines if len(ln.encode("utf-8")) <= MAX_TEXT]
    return "".join(lines[:MAX_GPU_ROWS])


def collect_gddr6(env):
    return read_text(env.real(GDDR6_PATH), MAX_GDDR6)


def collect_ipmi(env):
    """Текст SDR помощника ccs-ipmi дословно; агент его не разбирает."""
    return read_text(env.real(IPMI_PATH), MAX_IPMI)


def build_slice(env):
    """Срез v1 без ageMs: его проставляет сервер в момент отправки."""
    files = {}
    links = {}
    collect_hwmon(env, files, links)
    collect_proc(env, files)
    statvfs = collect_mounts(env, files)
    return {
        "v": SCHEMA_VERSION,
        "clockMs": int(time.time() * 1000),
        "files": files,
        "links": links,
        "gpuCsv": collect_gpu_csv(env),
        "gddr6": collect_gddr6(env),
        "ipmi": collect_ipmi(env),
        "statvfs": statvfs,
    }


class SliceStore:
    """Последний готовый срез и монотонный момент его готовности."""

    def __init__(self):
        self._lock = threading.Lock()
        self._slice = None
        self._ready_at = 0.0

    def put(self, slice_, ready_at=None):
        with self._lock:
            self._slice = slice_
            self._ready_at = time.monotonic() if ready_at is None else ready_at

    def get(self):
        with self._lock:
            return self._slice, self._ready_at


def run_collector(store, env, interval, stop, build=build_slice):
    """Цикл сборки: сбой одной итерации не роняет процесс, прежний срез остаётся (его ageMs растёт)."""
    while not stop.is_set():
        try:
            store.put(build(env))
        except Exception as exc:  # noqa: BLE001 — любой сбой сборки гасим, агент жив
            print("сборка среза не удалась: %r" % (exc,), file=sys.stderr, flush=True)
        stop.wait(interval)


def render_body(store, now=None):
    """Тело ответа или (None, код) при отказе."""
    slice_, ready_at = store.get()
    if slice_ is None:
        return None, 503
    age = max(0, int(((time.monotonic() if now is None else now) - ready_at) * 1000))
    body = {"v": slice_["v"], "ageMs": age}
    body.update((k, v) for k, v in slice_.items() if k != "v")
    data = json.dumps(body, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(data) > MAX_BODY:
        return None, 500
    return data, 200


def make_handler(store, allow):
    class Handler(BaseHTTPRequestHandler):
        # Один медленный клиент не должен держать однопоточный сервер дольше этого.
        timeout = 5
        protocol_version = "HTTP/1.0"
        server_version = "ccs-node-stats"

        def log_message(self, format, *args):  # noqa: A002 — тихо: журнал не нужен на каждый опрос
            pass

        def _send(self, code, body=b"", headers=()):
            self.send_response(code)
            for k, v in headers:
                self.send_header(k, v)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Connection", "close")
            self.end_headers()
            if body and self.command != "HEAD":
                self.wfile.write(body)

        def _allowed(self):
            try:
                return ipaddress.ip_address(self.client_address[0]) in allow
            except ValueError:
                return False

        def _handle(self):
            # Чужой адрес — 403 без тела и до разбора пути: маршруты чужому не раскрываем.
            if not self._allowed():
                return self._send(403)
            if urlsplit(self.path).path != "/v1/slice":
                return self._send(404)
            if self.command != "GET":
                return self._send(405, headers=(("Allow", "GET"),))
            data, code = render_body(store)
            if data is None:
                return self._send(code)
            self._send(200, data, (("Content-Type", "application/json; charset=utf-8"),
                                   ("Cache-Control", "no-store")))

        do_GET = do_POST = do_PUT = do_DELETE = do_PATCH = do_HEAD = do_OPTIONS = _handle

    return Handler


def make_server(host, port, store, allow):
    return HTTPServer((host, port), make_handler(store, allow))


class ConfigError(Exception):
    pass


def load_config(path):
    """Конфиг агента: {"listen": "host:port", "allow": ["ip", …]}. Без адреса или allow-list — не стартуем."""
    try:
        with open(path, "r", encoding="utf-8") as f:
            raw = json.load(f)
    except (OSError, ValueError) as exc:
        raise ConfigError("не прочитан конфиг %s: %s" % (path, exc))
    if not isinstance(raw, dict):
        raise ConfigError("конфиг должен быть объектом")
    listen = raw.get("listen")
    if not isinstance(listen, str) or listen.count(":") != 1:
        raise ConfigError("нужно поле listen вида \"адрес:порт\" (IPv4)")
    host, port_s = listen.split(":")
    try:
        addr = ipaddress.ip_address(host)
        port = int(port_s)
    except ValueError:
        raise ConfigError("listen: неверный адрес или порт")
    if addr.version != 4 or addr.is_unspecified or not (1 <= port <= 65535):
        raise ConfigError("listen: нужен конкретный адрес IPv4 интерфейса (0.0.0.0 не слушаем) и порт 1…65535")
    allow_raw = raw.get("allow")
    if not isinstance(allow_raw, list) or not allow_raw:
        raise ConfigError("нужен непустой список allow с адресами worker")
    try:
        allow = frozenset(ipaddress.ip_address(a) for a in allow_raw)
    except (ValueError, TypeError):
        raise ConfigError("allow: каждый элемент — IP-адрес")
    return host, port, allow


def main(argv=None):
    parser = argparse.ArgumentParser(description="ccs-node-stats: read-only срез датчиков узла")
    parser.add_argument("--config", default=DEFAULT_CONFIG)
    args = parser.parse_args(argv)
    try:
        host, port, allow = load_config(args.config)
    except ConfigError as exc:
        print("ccs-node-stats: %s" % exc, file=sys.stderr)
        return 2
    env = Env(nvidia_smi=shutil.which("nvidia-smi"))
    store = SliceStore()
    stop = threading.Event()
    threading.Thread(target=run_collector, args=(store, env, INTERVAL_S, stop), daemon=True).start()
    server = make_server(host, port, store, allow)

    def _term(*_):
        stop.set()
        threading.Thread(target=server.shutdown, daemon=True).start()

    signal.signal(signal.SIGTERM, _term)
    signal.signal(signal.SIGINT, _term)
    try:
        server.serve_forever()
    finally:
        stop.set()
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
