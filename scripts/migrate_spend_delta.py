#!/usr/bin/env python3
"""Разовая идемпотентная миграция накопленных итогов cost-state в дельты хода (задача ad52316b).

С 24.09 claude CLI на --resume восстанавливает накопленный cost-state, поэтому записи
source=chat-turn в turns-*.jsonl хранят НАКОПЛЁННЫЙ итог сессии, а не расход хода. Миграция:
внутри каждого sessionId, по возрастанию времени (через ВСЕ файлы, т.к. сессия живёт через
много дней), заменяет значения разностью с предыдущей записью; при уменьшении итога
(сброс/новая csid/форк) запись остаётся как есть.
"""
import json, os, shutil, sys

SPEND = sys.argv[1] if len(sys.argv) > 1 else "/srv/ccs/data/spend"
CUTOFF = "2026-09-24"
FIELDS = ["inputTokens", "outputTokens", "cacheReadTokens", "cacheCreationTokens"]
MARKER = os.path.join(SPEND, "delta-backfill.done")
BACKUP = os.path.join(SPEND, "pre-delta-backup")
DRY = "--dry" in sys.argv

def getf(r, k):
    v = r.get(k)
    return v if isinstance(v, (int, float)) else 0

def main():
    if os.path.exists(MARKER) and not DRY:
        print("маркер delta-backfill.done уже есть — миграция no-op"); return
    os.makedirs(BACKUP, exist_ok=True)

    files = sorted(f for f in os.listdir(SPEND)
                    if f.startswith("turns-") and f.endswith(".jsonl"))
    target = [f for f in files if f[len("turns-"):-len(".jsonl")] >= CUTOFF]

    # 1. бэкап исходников (только целевые даты)
    if not DRY:
        for f in target:
            shutil.copy2(os.path.join(SPEND, f), os.path.join(BACKUP, f))
        print(f"бэкап: {len(target)} файлов -> pre-delta-backup/")

    # 2. читаем ВСЕ целевые файлы (сессия живёт через много дней — база общая, не per-файл)
    file_records = {}   # file -> list[record]
    for f in target:
        path = os.path.join(SPEND, f)
        raw = open(path, encoding="utf-8").read()
        recs = []
        for l in raw.splitlines():
            if not l.strip():
                continue
            try:
                recs.append(json.loads(l))
            except json.JSONDecodeError:
                pass  # битая строка (оборванный append) — пропускаем, как SpendStore.Load
        file_records[f] = recs

    # 3. дельта per-session в хронологическом порядке (файлы по дате, внутри — порядок записи)
    last = {}  # sessionId -> последние ИСХОДНЫЕ (накопленные) значения
    total_changed = 0
    per_file = {}
    for f in target:  # target отсортирован по дате восходяще
        changed = 0
        for r in file_records[f]:
            if r.get("source") != "chat-turn":
                continue
            sid = r.get("sessionId")
            if sid is None:
                continue
            # база для следующего хода = исходные (накопленные) значения ЭТОЙ записи —
            # снимаем ДО in-place-мутации r (иначе в last уйдёт уже дельта, а не накопленный итог)
            orig = {k: getf(r, k) for k in FIELDS} | {"costUsd": r.get("costUsd")}
            prev = last.get(sid)
            if prev is not None:
                decreased = any(orig[k] < getf(prev, k) for k in FIELDS)
                if not decreased:
                    cu, pv = orig["costUsd"], prev.get("costUsd")
                    if cu is not None and pv is not None and cu < pv:
                        decreased = True
                if not decreased:
                    for k in FIELDS:
                        r[k] = orig[k] - getf(prev, k)
                    if orig["costUsd"] is not None and prev.get("costUsd") is not None:
                        r["costUsd"] = orig["costUsd"] - prev["costUsd"]
                    changed += 1
            last[sid] = orig
        per_file[f] = changed
        total_changed += changed

    # 4. запись (только если не dry)
    if not DRY:
        for f in target:
            if per_file[f] == 0:
                continue
            path = os.path.join(SPEND, f)
            with open(path, "w", encoding="utf-8") as fh:
                for r in file_records[f]:
                    fh.write(json.dumps(r, ensure_ascii=False, separators=(",", ":")) + "\n")
        with open(MARKER, "w", encoding="utf-8") as fh:
            fh.write("delta-backfill done " + __import__("datetime").datetime.now(
                __import__("datetime").timezone.utc).isoformat() + "\n")

    print(f"{'[dry] ' if DRY else ''}записей с дельтой: {total_changed}")
    for f in target:
        print(f"  {f}: {per_file.get(f, 0)}")

if __name__ == "__main__":
    main()
