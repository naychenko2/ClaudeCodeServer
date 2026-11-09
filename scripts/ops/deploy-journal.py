#!/usr/bin/python3 -I
# Единственный писатель журнала deploy-state.json со стороны оркестратора выкатки.
# Запускается через явный `python3 -I` от вызывающих скриптов: на Linux `/usr/bin/env python3`
# НЕ пробрасывает флаги в shebang, поэтому контракт ниже держится прямой интерпретатор
# с `-I`, и этот shebang — страховка от прямого запуска `python3 deploy-journal.py`.
# `-I` — граница привилегий: выкатка не подхватывает site-packages и user-site
# (файл пишется в ReleasesDir, права хоста).
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


def cmd_set_step(state: dict, name: str, status: str, ms: int | None = None) -> None:
    """Шаг выкатки. Один и тот же name переписывается (повторный вызов не плодит дубль —
    удобно, если шаг временно выставил промежуточный статус, а потом получил финал).
    ms — длительность шага в миллисекундах (long), как `DeployStep.Ms`. Если передана —
    перезаписывается на каждом вызове, фронт рисует длительности в карточке."""
    cur = state.get("current")
    if not cur:
        raise SystemExit("set-step: current пуст — писать нечего")
    steps = cur.setdefault("steps", [])
    record = next((s for s in steps if s.get("name") == name), None)
    if record is None:
        record = {"name": name}
        steps.append(record)
    record["status"] = status
    if ms is not None:
        record["ms"] = int(ms)


def cmd_set_sha(state: dict, sha: str) -> None:
    """Фактический sha после git checkout --detach. Перезаписывает snapshot-sha сервера."""
    cur = state.get("current")
    if not cur:
        raise SystemExit("set-sha: current пуст — писать нечего")
    cur["sha"] = sha


def cmd_set_rollback_target(state: dict, target: str) -> None:
    """Transient: switch пишет сюда ID снимка, на который вернулись при откате.
    finalize rolled_back подставляет его в result.releaseId и сам убирает поле, так что
    в файле, которым владеет сервер, оно не остаётся. Это шов switch → журнал, а не часть
    заявки."""
    state["rollbackTarget"] = target


def cmd_set_release(state: dict, release_id: str, sha: str, path: str) -> None:
    """Снимок релиза, пригодный для отката. Новый — в НАЧАЛО списка (newest-first, как
    Windows-агент: @(...) + (ConvertTo-List ...)); повторный вызов с тем же id заменяет
    запись, а не плодит дубль. sha — из build-id.txt снимка (предыдущая сборка, на которую
    вернётся откат), path — каталог снимка, createdAt — момент записи (ISO-8601 UTC).
    Поле releases держит DeployState.Releases; сервер его читает (deploy_status) и
    пересохраняет целиком, так что запись переживает его Save."""
    releases = state.setdefault("releases", [])
    releases[:] = [r for r in releases if r.get("id") != release_id]
    releases.insert(0, {"id": release_id, "sha": sha, "path": path, "createdAt": _utc_now_iso()})


def cmd_drop_release(state: dict, release_id: str) -> None:
    """Убрать снимок из списка при ротации. Отсутствующий id — no-op: ротация может
    догнать запись, которую уже убрал предыдущий прогон (журнал переживает выкатки)."""
    releases = state.get("releases", [])
    state["releases"] = [r for r in releases if r.get("id") != release_id]


def cmd_finalize(
    state: dict,
    status: str,
    message: str | None = None,
    release_id: str | None = None,
) -> None:
    """Закрыть запись: result + перенос current → history. Идемпотентно: повторный вызов при
    уже закрытом current — no-op, чтобы trap EXIT не падал после штатного завершения.
    Опциональный message идёт в result.message — там же, где сервер кладёт текст ошибки
    протухшей заявки и где DeployReportService ищет подробности провала.
    Опциональный release_id — снимок, на котором остановились (при откате — куда вернулись):
    пишется в result.releaseId, как `DeployResult.ReleaseId`. Для rolled_back без явного
    release_id берётся rollbackTarget, который оставил switch-release.sh."""
    # Служебное поле убираем при любом исходе, даже если закрывать уже нечего.
    rollback_target = state.pop("rollbackTarget", None)
    cur = state.get("current")
    if not cur:
        return
    if release_id is None and status == "rolled_back":
        release_id = rollback_target

    # result.status дублирует фазу — это контракт ADR-010, фронт ждёт оба.
    cur["result"] = {
        "ok": status in ("succeeded", "rolled_back"),
        "status": status,
        "finishedAt": _utc_now_iso(),
    }
    if message:
        cur["result"]["message"] = message
    if release_id:
        cur["result"]["releaseId"] = release_id
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
            "команды:\n"
            "  set-phase <phase>\n"
            "  set-step <name> <status> [ms]\n"
            "  set-sha <sha>\n"
            "  set-rollback-target <releaseId>    — transient для шва switch→deploy-linux\n"
            "  set-release <releaseId> [sha] [path]   — снимок в список releases (newest-first)\n"
            "  drop-release <releaseId>           — убрать снимок из releases (ротация)\n"
            "  finalize <status> [message] [releaseId]"
        )

    path = argv[1]
    command = argv[2]

    state = _load(path)

    if command == "set-phase":
        cmd_set_phase(state, argv[3])
    elif command == "set-step":
        cmd_set_step(
            state,
            argv[3],
            argv[4],
            int(argv[5]) if len(argv) > 5 else None,
        )
    elif command == "set-sha":
        cmd_set_sha(state, argv[3])
    elif command == "set-rollback-target":
        cmd_set_rollback_target(state, argv[3])
    elif command == "set-release":
        cmd_set_release(
            state,
            argv[3],
            argv[4] if len(argv) > 4 else "",
            argv[5] if len(argv) > 5 else "",
        )
    elif command == "drop-release":
        cmd_drop_release(state, argv[3])
    elif command == "finalize":
        cmd_finalize(
            state,
            argv[3],
            argv[4] if len(argv) > 4 else None,
            argv[5] if len(argv) > 5 else None,
        )
    else:
        raise SystemExit(f"неизвестная команда: {command}")

    _save(path, state)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))