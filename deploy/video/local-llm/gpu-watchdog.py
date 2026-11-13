#!/usr/bin/env python3
"""Сторож живости GPU-сервисов на «Video»: перезапускает юнит, если процесс жив, а CUDA в нём мёртв.

После TDR драйвера (13.11.2026) comfyui-h3 остался active, но любой CUDA-вызов падал
с «CUDA error: unknown error»; systemd этого не видит. Запускается таймером раз в минуту
(gpu-watchdog.timer), каждый запуск — один проход по целям.

Цель считается мёртвой, если юнит active дольше grace_s и:
  - проба вернула 5xx дважды подряд (между пробами RETRY_GAP_S), или
  - проба не ответила за PROBE_TIMEOUT_S дважды подряд, причём сервер целиком висит
    (не отвечает и queue_url) или простаивает (queue_running пуст): занятый генерацией
    ComfyUI под WDDM может отвечать на /system_stats медленно, за это не перезапускаем;
  - в журнале её текущего процесса за последние JOURNAL_WINDOW_S есть journal_pattern.

Ограничения: не чаще раза в MIN_INTERVAL_S; после HOLD_AFTER перезапусков за HOLD_WINDOW_S —
пауза до человека (запись уровня crit в журнал, флаг в файле состояния).
Снять паузу: python3 -I gpu-watchdog.py --clear <имя>.
"""
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

STATE_FILE = os.environ.get("WATCHDOG_STATE", "/var/lib/gpu-watchdog/state.json")
PROBE_TIMEOUT_S = float(os.environ.get("WATCHDOG_PROBE_TIMEOUT_S", "20"))
RETRY_GAP_S = float(os.environ.get("WATCHDOG_RETRY_GAP_S", "15"))
JOURNAL_WINDOW_S = int(os.environ.get("WATCHDOG_JOURNAL_WINDOW_S", "75"))
MIN_INTERVAL_S = float(os.environ.get("WATCHDOG_MIN_INTERVAL_S", "300"))
HOLD_AFTER = int(os.environ.get("WATCHDOG_HOLD_AFTER", "3"))
HOLD_WINDOW_S = float(os.environ.get("WATCHDOG_HOLD_WINDOW_S", "1800"))
RESTART_TIMEOUT_S = float(os.environ.get("WATCHDOG_RESTART_TIMEOUT_S", "150"))
# Повтор записи «на паузе» при продолжающемся отказе — не чаще раза в это время.
HOLD_REMIND_S = float(os.environ.get("WATCHDOG_HOLD_REMIND_S", "1800"))

DEFAULT_TARGETS = [
    {"name": "comfyui-h3", "unit": "comfyui-h3.service",
     "url": "http://127.0.0.1:8188/system_stats", "queue_url": "http://127.0.0.1:8188/queue",
     # ExecStartPre ждёт диск моделей, потом ComfyUI грузит узлы: порт открывается не сразу.
     "grace_s": 180, "journal_pattern": "CUDA error: unknown error"},
    # Старт vLLM ведёт арбитр (ARBITER_START_WAIT_S 240 с, после ночного бенча бывает 900 с), поэтому
    # сторож судит hyperqwen, только если в этой активации юнита /health уже хоть раз ответил.
    {"name": "hyperqwen", "unit": "hyperqwen.service",
     "url": "http://127.0.0.1:18020/health", "grace_s": 60, "require_seen_ok": True},
]
TARGETS = json.loads(os.environ["WATCHDOG_TARGETS"]) if os.environ.get("WATCHDOG_TARGETS") else DEFAULT_TARGETS


def log(level: int, msg: str):
    # Префикс <N> — уровень syslog для journald (2 crit, 4 warning, 6 info).
    print(f"<{level}>{msg}", flush=True)


def load_state() -> dict:
    try:
        with open(STATE_FILE) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_state(state: dict):
    os.makedirs(os.path.dirname(STATE_FILE), exist_ok=True)
    tmp = STATE_FILE + ".tmp"
    with open(tmp, "w") as f:
        json.dump(state, f, ensure_ascii=False, indent=1)
    os.replace(tmp, STATE_FILE)


def unit_props(unit: str) -> dict:
    out = subprocess.run(["systemctl", "show", "-p", "ActiveState,MainPID,ActiveEnterTimestampMonotonic", unit],
                         capture_output=True, text=True, timeout=10).stdout
    return dict(line.split("=", 1) for line in out.splitlines() if "=" in line)


def uptime_s(props: dict) -> float:
    enter_us = int(props.get("ActiveEnterTimestampMonotonic") or 0)
    return time.monotonic() - enter_us / 1e6 if enter_us else 0.0


def probe(url: str) -> str:
    """ok | 5xx | timeout (сюда же отказ соединения: процесс active, а порт молчит)."""
    try:
        with urllib.request.urlopen(url, timeout=PROBE_TIMEOUT_S):
            return "ok"
    except urllib.error.HTTPError as ex:
        return "5xx" if ex.code >= 500 else "ok"
    except Exception:
        return "timeout"


def comfy_busy_or_hung(queue_url: str) -> str:
    """hung — очередь не отвечает; busy — идёт задание; idle — простаивает."""
    try:
        with urllib.request.urlopen(queue_url, timeout=5) as r:
            q = json.load(r)
    except Exception:
        return "hung"
    return "busy" if q.get("queue_running") else "idle"


def dead_by_probe(t: dict) -> str | None:
    first = probe(t["url"])
    if first == "ok":
        return None
    time.sleep(RETRY_GAP_S)
    second = probe(t["url"])
    if second == "ok":
        return None
    if first == second == "5xx":
        return f"{t['url']} вернул 5xx дважды подряд"
    # хотя бы одна из проб — таймаут
    if t.get("queue_url"):
        q = comfy_busy_or_hung(t["queue_url"])
        if q == "busy":
            log(4, f"{t['name']}: {t['url']} не ответил за {PROBE_TIMEOUT_S:.0f} с дважды, но идёт задание — не трогаю")
            return None
        return f"{t['url']} не ответил дважды подряд, очередь {'не отвечает' if q == 'hung' else 'пуста'}"
    return f"{t['url']} не ответил за {PROBE_TIMEOUT_S:.0f} с дважды подряд"


def dead_by_journal(t: dict, main_pid: str) -> str | None:
    pattern = t.get("journal_pattern")
    if not pattern or not main_pid or main_pid == "0":
        return None
    # Только текущий процесс: строки прежнего (до перезапуска) не должны срабатывать повторно.
    out = subprocess.run(["journalctl", "-u", t["unit"], f"_PID={main_pid}", "--since", f"-{JOURNAL_WINDOW_S}s",
                          "-o", "cat", "--no-pager", "-q"], capture_output=True, text=True, timeout=20).stdout
    hits = sum(1 for line in out.splitlines() if pattern in line)
    return f"в журнале «{pattern}» ×{hits} за {JOURNAL_WINDOW_S} с" if hits else None


def restart(t: dict, reason: str, state: dict):
    st = state.setdefault(t["name"], {"restarts": [], "total": 0, "hold": None})
    now = time.time()
    st["restarts"] = [x for x in st["restarts"] if now - x < HOLD_WINDOW_S]
    if st.get("hold"):
        if now - st.get("hold_reminded", 0) >= HOLD_REMIND_S:
            st["hold_reminded"] = now
            log(2, f"{t['name']}: мёртв ({reason}), сторож на паузе с {st['hold']} — нужен человек; "
                   f"снять паузу: python3 -I {os.path.abspath(__file__)} --clear {t['name']}")
        return
    if len(st["restarts"]) >= HOLD_AFTER:
        st["hold"] = time.strftime("%Y-%m-%d %H:%M:%S")
        st["hold_reminded"] = now
        log(2, f"{t['name']}: мёртв ({reason}), но уже {len(st['restarts'])} перезапуска за "
               f"{HOLD_WINDOW_S / 60:.0f} мин — перезапуски остановлены, нужен человек; "
               f"снять паузу: python3 -I {os.path.abspath(__file__)} --clear {t['name']}")
        return
    if st["restarts"] and now - st["restarts"][-1] < MIN_INTERVAL_S:
        log(4, f"{t['name']}: мёртв ({reason}), но прошлый перезапуск был {now - st['restarts'][-1]:.0f} с назад "
               f"(минимум {MIN_INTERVAL_S:.0f} с) — жду")
        return
    st["restarts"].append(now)
    st["total"] += 1
    st["last"] = {"ts": time.strftime("%Y-%m-%d %H:%M:%S"), "reason": reason}
    save_state(state)
    log(4, f"{t['name']}: мёртв ({reason}) — systemctl restart {t['unit']}, перезапуск №{st['total']}, "
           f"за {HOLD_WINDOW_S / 60:.0f} мин: {len(st['restarts'])}")
    try:
        rc = subprocess.run(["systemctl", "restart", t["unit"]], capture_output=True, text=True,
                            timeout=RESTART_TIMEOUT_S).returncode
        log(6 if rc == 0 else 4, f"{t['name']}: systemctl restart завершился с кодом {rc}")
    except subprocess.TimeoutExpired:
        # клиент systemctl снят, задание перезапуска в systemd продолжается само
        log(4, f"{t['name']}: systemctl restart не уложился в {RESTART_TIMEOUT_S:.0f} с, задание идёт дальше")


def check(t: dict, state: dict):
    props = unit_props(t["unit"])
    if props.get("ActiveState") != "active":
        return  # остановлен арбитром или перезапускается — не наше дело
    if uptime_s(props) < t.get("grace_s", 180):
        return
    st = state.setdefault(t["name"], {"restarts": [], "total": 0, "hold": None})
    enter = props.get("ActiveEnterTimestampMonotonic")
    if t.get("require_seen_ok") and st.get("ok_enter") != enter:
        if probe(t["url"]) == "ok":
            st["ok_enter"] = enter
        return
    reason = dead_by_journal(t, props.get("MainPID", "")) or dead_by_probe(t)
    if reason:
        restart(t, reason, state)


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--clear":
        state = load_state()
        state.pop(sys.argv[2], None)
        save_state(state)
        log(6, f"{sys.argv[2]}: пауза и счётчик сторожа сброшены вручную")
        return
    state = load_state()
    for t in TARGETS:
        try:
            check(t, state)
        except Exception as ex:
            log(3, f"{t['name']}: проверка упала: {ex!r}")
    save_state(state)


if __name__ == "__main__":
    main()
