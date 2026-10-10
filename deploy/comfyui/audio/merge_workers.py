"""Сливает семейства воркеров в общий workers.json: merge_workers.py <workers.json> <часть.json>.

workers.json один на машину, а пишут его два установщика (аудио и LoRA). Каждый заменяет только
свои семейства и не трогает чужие; запись атомарная, чтобы узел не прочёл полфайла.
"""
import json
import os
import sys

dst, part = sys.argv[1], sys.argv[2]
current = {"families": {}}
if os.path.exists(dst):
    with open(dst, encoding="utf-8") as f:
        current = json.load(f)
with open(part, encoding="utf-8") as f:
    current.setdefault("families", {}).update(json.load(f)["families"])
tmp = dst + ".tmp"
with open(tmp, "w", encoding="utf-8") as f:
    json.dump(current, f, ensure_ascii=False, indent=1)
os.replace(tmp, dst)
print("семейства:", ", ".join(sorted(current["families"])))
