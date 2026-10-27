#!/usr/bin/env python3
"""Фаза 2 фикстуры приёмки «Фильтр фич по состоянию» (бэк ОСТАНОВЛЕН).

Бэк держит tasks.json/sessions.json в памяти и переписывает их сам — патчить
только при остановленном стенде (после патча — рестарт).

Ставит точные состояния фич (по FeatureProjector.ComputeState):
  W waiting_you — AwaitingHumanSince у задачи;
  E error       — ExecutorStoppedAt + StopReason у InProgress-задачи;
  R running     — InProgress-задача с ClaudeStartedAt;
  Q queued      — Hold.Reason=queue на эпике;
  P paused      — Hold.Reason=manual на эпике;
  D draft       — Hold.Reason=draft на эпике;
  I idle        — задача не стартовала (not_started);
  M merged      — merge Done + финал принят, эпик открыт;
  C closed      — эпик Done.
"""
import datetime as dt
import json

DATA = "/home/an/qa-filter/data"
FIX = json.load(open("/home/an/qa-filter/fixture.json", encoding="utf-8"))
now = dt.datetime.now(dt.timezone.utc)

STATUS_TODO, STATUS_INPROGRESS, STATUS_DONE = 0, 1, 2


def ts(minutes_ago):
    return (now - dt.timedelta(minutes=minutes_ago)).isoformat().replace("+00:00", "Z")


HOLD = {
    "Q": ("queue", 90),
    "P": ("manual", 90),
    "D": ("draft", 90),
}

tasks = json.load(open(f"{DATA}/tasks.json", encoding="utf-8"))
by_id = {t["Id"]: t for t in tasks}
chat_to_task = {}

for feat in FIX["features"]:
    key = feat["key"]
    epic = by_id[feat["epicId"]]
    epic["CreatedAt"] = ts(600)
    epic["UpdatedAt"] = ts(600)

    # Удержание на эпике — состояние паузы/очереди/черновика.
    if key in HOLD:
        reason, since = HOLD[key]
        epic["Hold"] = {"Reason": reason, "Since": ts(since),
                        "BySessionId": None, "ByUser": True}

    step = by_id[feat["stepId"]]
    chat_to_task[feat["chatId"]] = step["Id"]
    step["CreatedAt"] = ts(600)
    step["UpdatedAt"] = ts(600)

    if key == "W":          # ждёт вас
        step["Status"] = STATUS_INPROGRESS
        step["ClaudeStartedAt"] = ts(180)
        step["AwaitingHumanSince"] = ts(60)
        step["AwaitingHumanReason"] = "Выбери: тёмная или светлая тема"
    elif key == "E":        # остановлена
        step["Status"] = STATUS_INPROGRESS
        step["ClaudeStartedAt"] = ts(240)
        step["ExecutorStoppedAt"] = ts(45)
        step["ExecutorStopReason"] = "auth_failed"
    elif key in ("R", "R2"):   # идёт
        step["Status"] = STATUS_INPROGRESS
        step["ClaudeStartedAt"] = ts(30)
    elif key in ("Q", "P", "D"):  # удержание решает состояние, шаг не идёт
        step["Status"] = STATUS_TODO
        step["ClaudeStartedAt"] = None
    elif key in ("I",):    # не начата (not_started → idle)
        step["Status"] = STATUS_TODO
        step["ClaudeStartedAt"] = None
    elif key == "M":       # смержена: эпик открыт, merge Done, финал принят
        step["Status"] = STATUS_DONE
        step["ClaudeStartedAt"] = ts(600)
        step["CompletedAt"] = ts(500)
        for c in feat["children"]:
            ct = by_id[c["taskId"]]
            ct["ClaudeStartedAt"] = ts(500)
            if ct["Status"] != STATUS_DONE:
                ct["Status"] = STATUS_DONE
            ct["CompletedAt"] = ts(480)
            ct["UpdatedAt"] = ts(480)
    elif key in ("C", "C2", "C3", "C4"):   # закрыта человеком
        epic["Status"] = STATUS_DONE
        epic["CompletedAt"] = ts(420)
        epic["UpdatedAt"] = ts(420)
        step["Status"] = STATUS_TODO
        step["ClaudeStartedAt"] = None

    for c in feat["children"]:
        chat_to_task[c["chatId"]] = c["taskId"]

json.dump(tasks, open(f"{DATA}/tasks.json", "w", encoding="utf-8"), ensure_ascii=False)

# ── сессии: TaskId (членство чата в фиче), ProjectId, статус, время ──────────
sessions = json.load(open(f"{DATA}/sessions.json", encoding="utf-8"))
for s in sessions:
    tid = chat_to_task.get(s["Id"])
    if tid is None:
        continue
    s["TaskId"] = tid
    s["ProjectId"] = next(f["projectId"] for f in FIX["features"]
                          if tid in ([f["stepId"]] + [c["taskId"] for c in f["children"]]))
    s["Status"] = "Working"
    s["CreatedAt"] = ts(600)
    s["UpdatedAt"] = ts(60)
json.dump(sessions, open(f"{DATA}/sessions.json", "w", encoding="utf-8"), ensure_ascii=False)

print(f"пропатчено задач: {len(tasks)}, сессий с TaskId: "
      f"{sum(1 for s in sessions if s.get('TaskId'))}", flush=True)
