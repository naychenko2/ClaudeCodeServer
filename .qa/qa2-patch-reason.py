#!/usr/bin/env python3
"""QA2, фаза 4 — правка причины остановки задачи (правка tasks.json при ОСТАНОВЛЕННОМ бэке).

Нужна, чтобы проверить ветку «неизвестный/пустой код» строки «Сейчас» (F17 п.5):
известный код → «Исполнитель встал: <человеческий текст>», неизвестный или пустой →
«Исполнитель встал» без машинного кода.

Запуск (бэк остановлен):
    python3 .qa/qa2-patch-reason.py <код|empty>
"""

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TASKS = "/home/an/qa-fs/data/tasks.json"


def main():
    reason = sys.argv[1] if len(sys.argv) > 1 else "empty"
    value = None if reason in ("empty", "null", "") else reason
    ids = json.load(open(os.path.join(HERE, "qa2-fixture-ids.json")))
    tid = ids["features"]["stopped_step"]
    tasks = json.load(open(TASKS))
    hit = 0
    for t in tasks:
        if t["Id"] == tid:
            t["ExecutorStopReason"] = value
            hit += 1
    with open(TASKS, "w") as fh:
        json.dump(tasks, fh, ensure_ascii=False)
    print(json.dumps({"task": tid, "reason": value, "patched": hit}, ensure_ascii=False))


if __name__ == "__main__":
    main()
