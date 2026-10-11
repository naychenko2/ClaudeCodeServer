#!/usr/bin/env python3
# Единственный писатель журнала deploy-state.json со стороны оркестратора выкатки.
# Запускается через `python3 -I`, чтобы скрипты выкатки не тянули ничего из site-packages
# и user-site — это граница привилегий (файл пишется в ReleasesDir, права хоста).
#
# Контракт:
#   - Сервер пишет заявку в current ДО побудки агента: id, kind, phase=queued, ref,
#     sha (от snapshot), dirty, dirtyFiles, request, initiatedBy, startedAt.
#     Агент эти поля НЕ меняет.
#   - Агент пишет ТОЛЬКО: current.phase, current.sha (фактический после checkout),
#     current.steps[], current.result. Поле current.request и всю заявку не трогает.
#   - history и releases агент тоже не трогает (сервер архивирует при следующей заявке;
#     агенту достаточно закрыть result и подвинуть current → history в trap EXIT).
#
# Атомарность: tmp + os.replace — ни одна итерация не оставляет половинного файла,
# а сервер и новый инстанс читают его в любой момент.

import json
import os
import sys
from datetime import datetime, timezone


HISTORY_LIMIT = 30  # см. DeployState.HistoryLimit — сервер режет там же


def _utc_now_iso() -> str:
    """ISO-8601 в UTC с секундами и явным Z. Соответствует DateTime.UtcNow.ToString("O")."""
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _load(path: str) -> dict:
    if not os.path.exists(path):
        # Файла ещё нет — это валидное состояние до первой заявки. Поведение — отказ,
        # потому что без файла нечего фиксировать.
        raise SystemExit(f"deploy-state.json не найден: {path}")
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def _save(path: str, state: dict) -> None:
    """Атомарная перезапись: tmp в том же каталоге, os.replace → единая точка синхронизации."""
    tmp = path + ".tmp"
    # sort_keys=False — у сервера порядок ключей фиксирован в DeployState.cs, лишний diff
    # в git истории глазами не нужен. ensure_ascii=False — UTF-8 без \uXXXX (UnsafeRelaxedJsonEscaping).
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2, ensure_ascii=False, sort_keys=False)
        f.write("\n")
    os.replace(tmp, path)


def cmd_set_phase(state: dict, phase: str) -> None:
    cur = state.get("current")
    if not cur:
        raise SystemExit("set-phase: current пуст — писать нечего")
    cur["phase"] = phase


def cmd_set_step(state: dict, name: str, status: str) -> None:
    """Шаг выкатки. Один и тот же name переписывается (счётчик ms не ведём в журнале — шаги
    идут сразу с финальным status; ms добавит отдельная команда, когда понадобится)."""
    cur = state.get("current")
    if not cur:
        raise SystemExit("set-step: current пуст — писать нечего")
    steps = cur.setdefault("steps", [])
    for s in steps:
        if s.get("name") == name:
            s["status"] = status
            return
    steps.append({"name": name, "status": status})


def cmd_set_sha(state: dict, sha: str) -> None:
    """Фактический sha после git checkout --detach. Перезаписывает snapshot-sha сервера."""
    cur = state.get("current")
    if not cur:
        raise SystemExit("set-sha: current пуст — писать нечего")
    cur["sha"] = sha


def cmd_finalize(state: dict, status: str, message: str | None = None) -> None:
    """Закрыть запись: result + перенос current → history. Идемпотентно: повторный вызов при
    уже закрытом current — no-op, чтобы trap EXIT не падал после штатного завершения.
    Опциональный message идёт в result.message — там же, где сервер кладёт текст ошибки
    протухшей заявки и где DeployReportService ищет подробности провала."""
    cur = state.get("current")
    if not cur:
        return

    # result.status дублирует фазу — это контракт ADR-010, фронт ждёт оба.
    cur["result"] = {
        "ok": status in ("succeeded", "rolled_back"),
        "status": status,
        "finishedAt": _utc_now_iso(),
    }
    if message:
        cur["result"]["message"] = message
    cur["phase"] = status

    history = state.get("history", [])
    history.insert(0, cur)
    if len(history) > HISTORY_LIMIT:
        del history[HISTORY_LIMIT:]
    state["history"] = history
    state["current"] = None


def main(argv: list) -> int:
    if len(argv) < 3:
        raise SystemExit(
            "использование: deploy-journal.py <state-path> <command> [args...]\n"
            "команды: set-phase <phase> | set-step <name> <status> | "
            "set-sha <sha> | finalize <status> [message]"
        )

    path = argv[1]
    command = argv[2]

    state = _load(path)

    if command == "set-phase":
        cmd_set_phase(state, argv[3])
    elif command == "set-step":
        cmd_set_step(state, argv[3], argv[4])
    elif command == "set-sha":
        cmd_set_sha(state, argv[3])
    elif command == "finalize":
        cmd_finalize(state, argv[3], argv[4] if len(argv) > 4 else None)
    else:
        raise SystemExit(f"неизвестная команда: {command}")

    _save(path, state)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))