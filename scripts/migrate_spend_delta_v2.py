#!/usr/bin/env python3
"""Разовая идемпотентная миграция v2 накопленных итогов cost-state в дельты хода.

Перепрогон v1 (migrate_spend_delta.py, задача ad52316b) по ИСПРАВЛЕННОМУ правилу (задача
e487db76, финальное ревью Александра). v1 брал «любое уменьшение → весь снимок», поэтому
внутри одной csid небольшое уменьшение (гонка двух процессов CLI: фоновое продолжение,
ре-аттемпт, ход-продолжение) засчитывало всю историю чата. v2:

  * читает исходные НАКОПЛЁННЫЕ значения из pre-delta-backup/ (резервная копия v1), а не из
    уже модифицированных turns-*.jsonl — иначе пересчитывал бы дельту от дельты;
  * та же sessionId и уменьшение → max(0, cur − prev) по каждому полю (не весь снимок);
  * первая запись чата, начавшегося ДО cutoff, — разность с последней записью ДО cutoff
    (а не весь снимок): до cutoff CLI не копировал накопленный итог, поэтому база = последний
    пре-cutoff снимок, а не ноль;
  * записи ДО cutoff не трогает (там расход хода = расход хода, накопленного итога ещё не было);
  * маркер delta-backfill-v2.done (отдельный от delta-backfill.done, чтобы v1 не заблокировал
    перепрогон).

Запуск: python3 scripts/migrate_spend_delta_v2.py [SPEND_DIR] [--dry]
"""
import json, os, sys

SPEND = sys.argv[1] if len(sys.argv) > 1 else "/srv/ccs/data/spend"
CUTOFF = "2026-09-24"
FIELDS = ["inputTokens", "outputTokens", "cacheReadTokens", "cacheCreationTokens"]
MARKER = os.path.join(SPEND, "delta-backfill-v2.done")
BACKUP = os.path.join(SPEND, "pre-delta-backup")
DRY = "--dry" in sys.argv


def getf(r, k):
    v = r.get(k)
    return v if isinstance(v, (int, float)) else 0


def read_jsonl(path):
    """Все записи файла (битые строки — как SpendStore.Load — пропускаем)."""
    if not os.path.exists(path):
        return []
    recs = []
    for l in open(path, encoding="utf-8").read().splitlines():
        if not l.strip():
            continue
        try:
            recs.append(json.loads(l))
        except json.JSONDecodeError:
            pass
    return recs


def write_jsonl(path, recs):
    with open(path, "w", encoding="utf-8") as fh:
        for r in recs:
            fh.write(json.dumps(r, ensure_ascii=False, separators=(",", ":")) + "\n")


def main():
    if os.path.exists(MARKER) and not DRY:
        print("маркер delta-backfill-v2.done уже есть — миграция no-op")
        return
    os.makedirs(BACKUP, exist_ok=True)

    # Целевые файлы: даты >= CUTOFF, для которых в pre-delta-backup/ есть исходник.
    # pre-delta-backup/ заполнен v1 только для целевых дат, поэтому пересечение = ровно те файлы.
    all_files = sorted(f for f in os.listdir(SPEND)
                       if f.startswith("turns-") and f.endswith(".jsonl"))
    target = [f for f in all_files
              if f[len("turns-"):-len(".jsonl")] >= CUTOFF
              and os.path.exists(os.path.join(BACKUP, f))]

    # 1. исходные (накопленные) значения: из pre-delta-backup/ (копия v1), не из turns-*.jsonl
    backup_records = {f: read_jsonl(os.path.join(BACKUP, f)) for f in target}

    # 2. база для чатов, начавшихся ДО cutoff: последняя пре-cutoff запись каждого sessionId.
    #    Читаем ВСЕ turns-*.jsonl (в т.ч. < CUTOFF): до cutoff расход = расход хода, и последний
    #    такой снимок = накопленный итог, с которого CLI начал копировать итог на 24.09.
    pre_cutoff_last = {}  # sessionId -> (последние исходные значения, costUsd)
    for f in all_files:
        if f[len("turns-"):-len(".jsonl")] >= CUTOFF:
            continue
        for r in read_jsonl(os.path.join(SPEND, f)):
            if r.get("source") != "chat-turn":
                continue
            sid = r.get("sessionId")
            if sid is None:
                continue
            pre_cutoff_last[sid] = {k: getf(r, k) for k in FIELDS} | {"costUsd": r.get("costUsd")}

    # 3. дельта per-session в хронологическом порядке (файлы по дате, внутри — порядок записи)
    last = {}          # sessionId -> последние ИСХОДНЫЕ (накопленные) значения
    total_changed = 0
    per_file = {}
    for f in target:  # target отсортирован по дате восходяще
        changed = 0
        out = []
        for r in backup_records[f]:
            out.append(r)
            if r.get("source") != "chat-turn":
                continue
            sid = r.get("sessionId")
            if sid is None:
                continue
            orig = {k: getf(r, k) for k in FIELDS} | {"costUsd": r.get("costUsd")}
            # База: предыдущая запись того же sessionId; если её нет, а чат начался до cutoff —
            # последний пре-cutoff снимок (накопленный итог, с которого CLI пошёл копировать).
            prev = last.get(sid)
            if prev is None:
                prev = pre_cutoff_last.get(sid)
            if prev is not None:
                # ta же sessionId: дельта; уменьшение (гонка процессов) → max(0, …), не весь снимок
                for k in FIELDS:
                    r[k] = max(0, orig[k] - getf(prev, k))
                cu, pv = orig["costUsd"], prev.get("costUsd")
                if cu is not None and pv is not None:
                    r["costUsd"] = max(0.0, cu - pv)
                changed += 1
            # prev is None (первая запись чата, начавшегося ПОСЛЕ cutoff) — расход = весь снимок,
            # запись не трогаем (она и есть накопленный итог с нуля)
            last[sid] = orig
        per_file[f] = changed
        total_changed += changed

    # 4. запись (только если не dry): исходники из pre-delta-backup/ → turns-*.jsonl
    if not DRY:
        for f in target:
            if per_file[f] == 0:
                continue
            write_jsonl(os.path.join(SPEND, f), backup_records[f])
        with open(MARKER, "w", encoding="utf-8") as fh:
            fh.write("delta-backfill-v2 done " + __import__("datetime").datetime.now(
                __import__("datetime").timezone.utc).isoformat() + "\n")

    print(f"{'[dry] ' if DRY else ''}записей с дельтой: {total_changed}")
    for f in target:
        print(f"  {f}: {per_file.get(f, 0)}")


if __name__ == "__main__":
    main()
