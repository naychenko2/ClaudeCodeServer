#!/usr/bin/env python3
"""QA2, фаза 3 — правка времени событий фикстур (правка tasks.json при ОСТАНОВЛЕННОМ бэке).

Зачем: brief.updatedAt — время последнего ЗАПИСАННОГО события состояния фичи
(TaskItem.CreatedAt / ClaudeStartedAt / ExecutorStoppedAt / CompletedAt / AwaitingHumanSince /
Hold.Since). Чтобы получить «3 ч назад» и «дата закрытия» без ждать живых часов,
время событий двигается назад правкой файла задач — при остановленном бэке (он держит
tasks.json в памяти и перезаписывает файл).

Запуск (бэк должен быть остановлен):
    python3 .qa/qa2-patch-time.py
"""

import datetime
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = "/home/an/qa-fs/data"
TASKS = os.path.join(DATA, "tasks.json")


def iso(dt):
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{dt.microsecond // 1000:03d}Z"


def main():
    ids = json.load(open(os.path.join(HERE, "qa2-fixture-ids.json")))
    f = ids["features"]
    now = datetime.datetime.now(datetime.timezone.utc)

    tasks = json.load(open(TASKS))
    by_id = {t["Id"]: t for t in tasks}

    def back(ids_to_shift, hours, also_done=False, completed=False):
        when = now - datetime.timedelta(hours=hours)
        for tid in ids_to_shift:
            t = by_id[tid]
            t["CreatedAt"] = iso(when)
            if also_done:
                t["Status"] = 2  # Done
                t["CompletedAt"] = iso(when)
        return when

    # «Три часа назад»: и эпик, и шаг — три часа назад; шаг стартовал тогда же.
    when3h = back([f["old3h"], f["old3h_step"]], 3)
    by_id[f["old3h_step"]]["ClaudeStartedAt"] = iso(when3h)

    # «Свежая»: событие только что (старт шага) — чтобы «обновлено только что».
    by_id[f["fresh_step"]]["ClaudeStartedAt"] = iso(now)
    by_id[f["fresh"]]["CreatedAt"] = iso(now)

    # «Закрыта давно»: эпик закрыт двое суток назад, шаг закрыт тогда же.
    when2d = back([f["closed"], f["closed_step"]], 48, also_done=True)

    # Стадии: старт шага — час назад (стабильная «Сейчас»-строка, время не тикает).
    when1h = now - datetime.timedelta(hours=1)
    for key in ("cut", "plan", "prod", "review", "qa"):
        by_id[f[f"stage_{key}_cur"]]["ClaudeStartedAt"] = iso(when1h)

    with open(TASKS, "w") as fh:
        json.dump(tasks, fh, ensure_ascii=False)

    print(json.dumps({
        "now": iso(now),
        "old3h": iso(when3h),
        "closed": iso(when2d),
        "fresh": iso(now),
        "stage": iso(when1h),
    }, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
