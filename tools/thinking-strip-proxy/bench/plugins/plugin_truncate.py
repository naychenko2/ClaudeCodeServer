"""Контрольная схема для проверки самого стенда: сводка нынешней схемы, обрезанная до N символов.

Стенд обязан показать провал: если оценки «обрезанной» сводки не падают, значит стенд ничего
не меряет. Требует готового прогона `--scheme old` (берёт его кэш сводок).
"""
import json
import os

KEEP = int(os.environ.get("BENCH_TRUNCATE_KEEP", "1200"))
RUNS = os.path.join(os.environ.get("BENCH_HOME", "/srv/ccs/data/compact-bench"), "runs")


def summarize(item):
    with open(os.path.join(RUNS, "old", item["id"] + ".summary.json"), encoding="utf-8") as f:
        raw = json.load(f)["raw"]
    i = raw.find("<summary>")
    return raw[:i + 9 + KEEP] + "\n</summary>"
