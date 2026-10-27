#!/usr/bin/env python3
"""Арбитр GPU на «Video»: Bonsai (bonsai.service) уступает карту тяжёлым задачам и возвращается.

Правила (режим «9B рядом с кэшем ComfyUI»):
  уступить — если (а) есть аренда от CCS (LoRA, WhisperX…), (б) на GPU чужой процесс
  (не ComfyUI и не Bonsai), (в) в очереди ComfyUI задание с тяжёлым графом (видео H3 и т.п.);
  вернуть — когда ни одного основания нет подряд RETURN_QUIET_S секунд.

HTTP (порт 18031):
  GET  /state                 → {"bonsai": "ready|yielded|starting", "reasons": [...], ...}
  POST /lease  {"ttl_s": N, "reason": "..."} → {"id": ...}; продление — повторный POST с тем же id
  DELETE /lease/<id>
"""
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

COMFY = os.environ.get("ARBITER_COMFY_URL", "http://127.0.0.1:8188")
BONSAI_UNIT = os.environ.get("ARBITER_BONSAI_UNIT", "bonsai.service")
BONSAI_HEALTH = os.environ.get("ARBITER_BONSAI_HEALTH", "http://127.0.0.1:18030/health")
PORT = int(os.environ.get("ARBITER_PORT", "18031"))
POLL_S = float(os.environ.get("ARBITER_POLL_S", "0.5"))
RETURN_QUIET_S = float(os.environ.get("ARBITER_RETURN_QUIET_S", "10"))
# Пауза возврата после заданий ComfyUI — больше обычной: серия картинок идёт с перерывами на
# просмотр, и возврат LLM между кадрами заставил бы ComfyUI грузить модели заново на каждом.
RETURN_QUIET_AFTER_COMFY_S = float(os.environ.get("ARBITER_RETURN_QUIET_AFTER_COMFY_S", "180"))
# exclusive — Bonsai не делит карту с ComfyUI: уступает любому заданию, перед возвратом выгружает
# кэш моделей ComfyUI (/free) и ждёт VRAM_NEEDED_MB свободных. shared — уступает только тяжёлым
# графам (маркеры ниже), кэш ComfyUI не трогает (режим для 9B рядом с кэшем).
MODE = os.environ.get("ARBITER_MODE", "exclusive")
VRAM_NEEDED_MB = int(os.environ.get("ARBITER_VRAM_NEEDED_MB", "11000"))
# Подстроки в class_type или строковых входах узла, по которым граф ComfyUI считается тяжёлым.
HEAVY_MARKERS = [s.lower() for s in os.environ.get(
    "ARBITER_HEAVY_MARKERS", "h3,minimax,video,fasth3,whisper").split(",") if s]
# «lora» в маркерах нет намеренно: графы картинок с LoRA персонажа — лёгкие; тренер LoRA —
# отдельный процесс и ловится как чужой на GPU.
# Процессы на GPU, которые не считаются чужими: подстрока командной строки ИЛИ рабочего
# каталога (ComfyUI запущен как «python main.py» из /home/an/ComfyUI-h3 — путь только в cwd).
OWN_PROCS = [s for s in os.environ.get("ARBITER_OWN_PROCS", "ComfyUI-h3,llama-server").split(",") if s]
# Под systemd у root в PATH нет /usr/lib/wsl/lib, где в WSL лежит nvidia-smi.
NVSMI = shutil.which("nvidia-smi") or "/usr/lib/wsl/lib/nvidia-smi"
_warned: set[str] = set()


def _warn_once(key: str, msg: str):
    if key not in _warned:
        _warned.add(key)
        print(msg, file=sys.stderr, flush=True)

_lock = threading.Lock()
_leases: dict[str, dict] = {}
_state = {"bonsai": "unknown", "reasons": [], "since": time.time(), "yields": 0, "returns": 0,
          "last_yield_s": None, "last_return_s": None, "mode": MODE, "quiet_left_s": None}


def _get_json(url, timeout=2.0):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.load(r)


def gpu_foreign_procs() -> list[str]:
    try:
        out = subprocess.run([NVSMI, "--query-compute-apps=pid,process_name", "--format=csv,noheader"],
                             capture_output=True, text=True, timeout=3).stdout
    except Exception as ex:
        _warn_once("nvsmi", f"nvidia-smi недоступен ({NVSMI}): {ex}; чужие процессы на GPU не вижу")
        return []
    found = []
    for line in out.splitlines():
        pid = line.split(",")[0].strip()
        if not pid.isdigit():
            continue
        try:
            cmd = open(f"/proc/{pid}/cmdline", "rb").read().replace(b"\0", b" ").decode(errors="replace")
            cwd = os.readlink(f"/proc/{pid}/cwd")
        except OSError:
            continue
        if any(own in cmd or own in cwd for own in OWN_PROCS):
            continue
        found.append(f"{pid}:{cmd[:60]}")
    return found


def _graph_heavy(prompt: dict) -> bool:
    for node in prompt.values():
        hay = [str(node.get("class_type", ""))]
        for v in (node.get("inputs") or {}).values():
            if isinstance(v, str):
                hay.append(v)
        text = " ".join(hay).lower()
        if any(m in text for m in HEAVY_MARKERS):
            return True
    return False


def comfy_heavy_jobs() -> list[str]:
    try:
        q = _get_json(COMFY + "/queue")
    except Exception as ex:
        _warn_once("comfy", f"очередь ComfyUI недоступна ({COMFY}): {ex}")
        return []
    heavy = []
    for item in (q.get("queue_running") or []) + (q.get("queue_pending") or []):
        # формат элемента очереди: [number, prompt_id, prompt, extra, outputs]
        try:
            pid, prompt = item[1], item[2]
        except (IndexError, TypeError):
            continue
        if MODE == "exclusive" or (isinstance(prompt, dict) and _graph_heavy(prompt)):
            heavy.append(str(pid)[:8])
    return heavy


def active_leases() -> list[str]:
    now = time.time()
    with _lock:
        for k in [k for k, v in _leases.items() if v["until"] < now]:
            del _leases[k]
        return [f'{v["reason"]}' for v in _leases.values()]


def vram_used_total_mb():
    try:
        out = subprocess.run([NVSMI, "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=3).stdout.strip().split(",")
        return int(out[0]), int(out[1])
    except Exception:
        return None, None


def comfy_free():
    try:
        req = urllib.request.Request(COMFY + "/free", data=json.dumps({"unload_models": True, "free_memory": True}).encode(),
                                     headers={"Content-Type": "application/json"})
        urllib.request.urlopen(req, timeout=10).read()
        return True
    except Exception as ex:
        _warn_once("free", f"ComfyUI /free не сработал: {ex}")
        return False


def vram_room_for_bonsai() -> bool:
    used, total = vram_used_total_mb()
    return used is None or total is None or total - used >= VRAM_NEEDED_MB


def systemctl(*args) -> bool:
    return subprocess.run(["systemctl", *args], capture_output=True, timeout=30).returncode == 0


def bonsai_active() -> bool:
    return subprocess.run(["systemctl", "is-active", "--quiet", BONSAI_UNIT], timeout=5).returncode == 0


def bonsai_healthy() -> bool:
    try:
        with urllib.request.urlopen(BONSAI_HEALTH, timeout=1.0) as r:
            return r.status == 200
    except Exception:
        return False


def loop():
    quiet_since = None
    last_reason_comfy = False
    while True:
        reasons = [f"lease:{r}" for r in active_leases()]
        reasons += [f"gpu:{p}" for p in gpu_foreign_procs()]
        reasons += [f"comfy:{j}" for j in comfy_heavy_jobs()]
        active = bonsai_active()
        if reasons:
            quiet_since = None
            last_reason_comfy = any(r.startswith("comfy:") for r in reasons)
            _state["quiet_left_s"] = None
            if active:
                t0 = time.time()
                systemctl("stop", BONSAI_UNIT)
                _state["last_yield_s"] = round(time.time() - t0, 2)
                _state["yields"] += 1
                _state["since"] = time.time()
            _state["bonsai"] = "yielded"
        else:
            quiet_since = quiet_since or time.time()
            need_quiet = RETURN_QUIET_AFTER_COMFY_S if last_reason_comfy else RETURN_QUIET_S
            left = need_quiet - (time.time() - quiet_since)
            _state["quiet_left_s"] = round(max(0.0, left), 1) if not active else None
            if not active and left <= 0:
                t0 = time.time()
                # exclusive: освободить кэш моделей ComfyUI и убедиться, что место есть
                if MODE == "exclusive" and not vram_room_for_bonsai():
                    comfy_free()
                    for _ in range(20):
                        if vram_room_for_bonsai():
                            break
                        time.sleep(0.25)
                if not vram_room_for_bonsai():
                    _warn_once("vram", f"для Bonsai нет {VRAM_NEEDED_MB} МБ VRAM даже после /free — жду")
                    _state["bonsai"] = "waiting-vram"
                    time.sleep(POLL_S)
                    continue
                last_reason_comfy = False
                systemctl("start", BONSAI_UNIT)
                _state["returns"] += 1
                _state["since"] = time.time()
                _state["bonsai"] = "starting"
                # ждём готовности, но не дольше 60 с — иначе юнит сам перезапустится
                while time.time() - t0 < 60 and not bonsai_healthy():
                    time.sleep(0.25)
                _state["last_return_s"] = round(time.time() - t0, 2)
            _state["bonsai"] = "ready" if active or bonsai_healthy() else ("starting" if bonsai_active() else "yielded")
        _state["reasons"] = reasons
        time.sleep(POLL_S)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):  # тихий лог: журнал ведёт systemd
        pass

    def _send(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        # /ready — для CCS перед вызовом: 200 = Bonsai отвечает, 503 = выгружен/стартует.
        if self.path == "/ready":
            ok = _state["bonsai"] == "ready" and bonsai_healthy()
            return self._send(200 if ok else 503, {"ready": ok, "bonsai": _state["bonsai"], "reasons": _state["reasons"]})
        if self.path == "/state":
            with _lock:
                leases = {k: {"reason": v["reason"], "left_s": round(v["until"] - time.time(), 1)} for k, v in _leases.items()}
            return self._send(200, {**_state, "leases": leases, "ts": time.time()})
        self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/lease":
            return self._send(404, {"error": "not found"})
        n = int(self.headers.get("Content-Length") or 0)
        try:
            body = json.loads(self.rfile.read(n) or b"{}")
        except json.JSONDecodeError:
            return self._send(400, {"error": "bad json"})
        ttl = float(body.get("ttl_s") or 600)
        lid = str(body.get("id") or uuid.uuid4())
        with _lock:
            _leases[lid] = {"reason": str(body.get("reason") or "lease"), "until": time.time() + ttl}
        self._send(200, {"id": lid, "until": _leases[lid]["until"]})

    def do_DELETE(self):
        if not self.path.startswith("/lease/"):
            return self._send(404, {"error": "not found"})
        lid = self.path[len("/lease/"):]
        with _lock:
            existed = _leases.pop(lid, None) is not None
        self._send(200 if existed else 404, {"released": existed})


if __name__ == "__main__":
    threading.Thread(target=loop, daemon=True).start()
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
