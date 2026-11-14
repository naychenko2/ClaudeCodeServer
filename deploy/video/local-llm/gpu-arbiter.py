#!/usr/bin/env python3
"""Арбитр GPU на «Video»: дежурная LLM (local-llm.service) уступает карту тяжёлым задачам и возвращается.

Правила (режим «9B рядом с кэшем ComfyUI»):
  уступить — если (а) есть аренда от CCS (LoRA, WhisperX…), (б) на GPU чужой процесс
  (не ComfyUI и не llama-server), (в) в очереди ComfyUI задание с тяжёлым графом (видео H3 и т.п.);
  вернуть — когда ни одного основания нет подряд RETURN_QUIET_S секунд.

HTTP (порт 18031):
  GET  /state                 → {"engine": "ready|yielded|starting|waiting-vram", "reasons": [...], ...}
                                (до 01.11.2026 поле называлось "bonsai"; CCS читает оба)
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
ENGINE_UNIT = os.environ.get("ARBITER_ENGINE_UNIT", "local-llm.service")
ENGINE_HEALTH = os.environ.get("ARBITER_ENGINE_HEALTH", "http://127.0.0.1:18030/health")
PORT = int(os.environ.get("ARBITER_PORT", "18031"))
POLL_S = float(os.environ.get("ARBITER_POLL_S", "0.5"))
START_WAIT_S = float(os.environ.get("ARBITER_START_WAIT_S", "60"))
# Не дождались готовности за START_WAIT_S — останавливаем и пробуем снова; после START_RETRIES
# неудач подряд — пауза START_BACKOFF_S (состояние failed), потом ещё попытка.
START_RETRIES = int(os.environ.get("ARBITER_START_RETRIES", "3"))
START_BACKOFF_S = float(os.environ.get("ARBITER_START_BACKOFF_S", "600"))
# hyperqwen останавливается через docker compose stop -t 30 при TimeoutStopSec=60: 12.11.2026
# прежние 30 с на systemctl stop не хватило на 2 с.
STOP_TIMEOUT_S = float(os.environ.get("ARBITER_STOP_TIMEOUT_S", "90"))
# Как часто проверять, что ComfyUI жив по CUDA (/system_stats), перед уступкой ему карты.
COMFY_ALIVE_EVERY_S = float(os.environ.get("ARBITER_COMFY_ALIVE_EVERY_S", "5"))
RETURN_QUIET_S = float(os.environ.get("ARBITER_RETURN_QUIET_S", "10"))
# Пауза возврата после заданий ComfyUI — больше обычной: серия картинок идёт с перерывами на
# просмотр, и возврат LLM между кадрами заставил бы ComfyUI грузить модели заново на каждом.
RETURN_QUIET_AFTER_COMFY_S = float(os.environ.get("ARBITER_RETURN_QUIET_AFTER_COMFY_S", "180"))
# exclusive — LLM не делит карту с ComfyUI: уступает любому заданию, перед возвратом выгружает
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
# nvidia-smi в WSL идёт в драйвер WDDM той же 3090, что выводит рабочий стол; под нагрузкой
# запрос висит секундами. Поэтому не чаще раза в NVSMI_EVERY_S (до 02.11.2026 — каждые POLL_S).
NVSMI_EVERY_S = float(os.environ.get("ARBITER_NVSMI_EVERY_S", "5"))
_warned: set[str] = set()


def _warn_once(key: str, msg: str):
    if key not in _warned:
        _warned.add(key)
        print(msg, file=sys.stderr, flush=True)

_lock = threading.Lock()
_leases: dict[str, dict] = {}
_state = {"engine": "unknown", "reasons": [], "since": time.time(), "yields": 0, "returns": 0,
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


_gpu_cache = {"t": 0.0, "procs": []}


def gpu_foreign_procs_throttled() -> list[str]:
    if time.time() - _gpu_cache["t"] >= NVSMI_EVERY_S:
        _gpu_cache["procs"] = gpu_foreign_procs()
        _gpu_cache["t"] = time.time()
    return _gpu_cache["procs"]


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


def vram_room_for_engine() -> bool:
    used, total = vram_used_total_mb()
    return used is None or total is None or total - used >= VRAM_NEEDED_MB


def _log(msg: str):
    print(msg, file=sys.stderr, flush=True)


def systemctl(*args, timeout: float = 30) -> bool:
    try:
        return subprocess.run(["systemctl", *args], capture_output=True, timeout=timeout).returncode == 0
    except subprocess.TimeoutExpired:
        # Снят только клиент: задание в systemd идёт дальше, его итог видно на следующем круге.
        _log(f"systemctl {' '.join(args)} не уложился в {timeout:.0f} с — задание systemd продолжается")
        return False


_comfy = {"t": 0.0, "alive": True}


def comfy_alive() -> bool:
    """ComfyUI отвечает на /system_stats — там CUDA-вызов. После TDR 13.11.2026 /queue отвечал,
    а /system_stats — 500: процесс жив, CUDA мёртв, отдавать ему карту бессмысленно."""
    if time.time() - _comfy["t"] >= COMFY_ALIVE_EVERY_S:
        try:
            with urllib.request.urlopen(COMFY + "/system_stats", timeout=5) as r:
                alive = r.status == 200
        except Exception:
            alive = False
        if alive != _comfy["alive"]:
            _log("ComfyUI снова отвечает на /system_stats" if alive else
                 "ComfyUI не отвечает на /system_stats (CUDA мёртв?) — дежурную LLM ради него не останавливаю")
        _comfy.update(t=time.time(), alive=alive)
    return _comfy["alive"]


def engine_active() -> bool:
    return subprocess.run(["systemctl", "is-active", "--quiet", ENGINE_UNIT], timeout=5).returncode == 0


def engine_healthy() -> bool:
    try:
        with urllib.request.urlopen(ENGINE_HEALTH, timeout=1.0) as r:
            return r.status == 200
    except Exception:
        return False


def loop():
    # Поток арбитра не должен умирать: 12.11.2026 TimeoutExpired на systemctl stop убил его,
    # HTTP продолжал отдавать застывшее «starting», а LLM не возвращалась до перезапуска арбитра.
    ctx = {"quiet_since": None, "last_reason_comfy": False}
    while True:
        try:
            _tick(ctx)
        except Exception as ex:
            _log(f"ошибка в цикле арбитра: {ex!r} — продолжаю")
            time.sleep(max(POLL_S, 2.0))


_start = {"attempts": 0, "backoff_until": 0.0}


def _tick(ctx: dict):
    reasons = [f"lease:{r}" for r in active_leases()]
    active = engine_active()
    comfy_jobs = comfy_heavy_jobs()
    # Уступать карту ComfyUI, у которого мёртв CUDA, — значит остановить LLM ради задания,
    # которое всё равно упадёт (13.11.2026, 08:30). Проверяем только когда LLM работает:
    # уже уступленную карту не отбираем — занятый генерацией ComfyUI может отвечать медленно.
    if comfy_jobs and active and not comfy_alive():
        comfy_jobs = []
    _state["comfy_alive"] = _comfy["alive"]
    reasons += [f"comfy:{j}" for j in comfy_jobs]
    if not reasons:
        # аренда или задание ComfyUI уже держат карту — драйвер не опрашиваем
        reasons += [f"gpu:{p}" for p in gpu_foreign_procs_throttled()]
    if reasons:
        ctx["quiet_since"] = None
        ctx["last_reason_comfy"] = any(r.startswith("comfy:") for r in reasons)
        _state["quiet_left_s"] = None
        if active:
            t0 = time.time()
            systemctl("stop", ENGINE_UNIT, timeout=STOP_TIMEOUT_S)
            _state["last_yield_s"] = round(time.time() - t0, 2)
            _state["yields"] += 1
            _state["since"] = time.time()
        _state["engine"] = "yielded"
    else:
        ctx["quiet_since"] = ctx["quiet_since"] or time.time()
        need_quiet = RETURN_QUIET_AFTER_COMFY_S if ctx["last_reason_comfy"] else RETURN_QUIET_S
        left = need_quiet - (time.time() - ctx["quiet_since"])
        _state["quiet_left_s"] = round(max(0.0, left), 1) if not active else None
        if not active and left <= 0 and time.time() < _start["backoff_until"]:
            _state["engine"] = "failed"
            _state["reasons"] = reasons
            time.sleep(POLL_S)
            return
        if not active and left <= 0:
            t0 = time.time()
            # exclusive: освободить кэш моделей ComfyUI и убедиться, что место есть
            if MODE == "exclusive" and not vram_room_for_engine():
                comfy_free()
                for _ in range(20):
                    if vram_room_for_engine():
                        break
                    time.sleep(0.25)
            if not vram_room_for_engine():
                _warn_once("vram", f"для LLM нет {VRAM_NEEDED_MB} МБ VRAM даже после /free — жду")
                _state["engine"] = "waiting-vram"
                time.sleep(max(POLL_S, NVSMI_EVERY_S))
                return
            ctx["last_reason_comfy"] = False
            systemctl("start", ENGINE_UNIT)
            _state["returns"] += 1
            _state["since"] = time.time()
            _state["engine"] = "starting"
            # ждём готовности, но не дольше START_WAIT_S (по умолчанию 60 с) — иначе юнит сам перезапустится
            while time.time() - t0 < START_WAIT_S and not engine_healthy():
                time.sleep(0.25)
            _state["last_return_s"] = round(time.time() - t0, 2)
            if engine_healthy():
                _start["attempts"] = 0
            else:
                _start_timed_out()
                _state["reasons"] = reasons
                time.sleep(POLL_S)
                return
        _state["engine"] = "ready" if active or engine_healthy() else ("starting" if engine_active() else "yielded")
    _state["reasons"] = reasons
    time.sleep(POLL_S)


def _start_timed_out():
    """LLM не ответила за START_WAIT_S: остановить и пробовать снова, а не висеть в starting."""
    _start["attempts"] += 1
    systemctl("stop", ENGINE_UNIT, timeout=STOP_TIMEOUT_S)
    if _start["attempts"] >= START_RETRIES:
        _start["backoff_until"] = time.time() + START_BACKOFF_S
        _start["attempts"] = 0
        _state["engine"] = "failed"
        _log(f"{ENGINE_UNIT} не поднялся за {START_WAIT_S:.0f} с {START_RETRIES} раза подряд — "
             f"пауза {START_BACKOFF_S:.0f} с, потом снова")
    else:
        _state["engine"] = "yielded"
        _log(f"{ENGINE_UNIT} не ответил на health за {START_WAIT_S:.0f} с — остановил, "
             f"попытка {_start['attempts']}/{START_RETRIES} на следующем круге")
    _state["since"] = time.time()


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
        # /ready — для CCS перед вызовом: 200 = LLM отвечает, 503 = выгружен/стартует.
        if self.path == "/ready":
            ok = _state["engine"] == "ready" and engine_healthy()
            return self._send(200 if ok else 503, {"ready": ok, "engine": _state["engine"], "reasons": _state["reasons"]})
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
