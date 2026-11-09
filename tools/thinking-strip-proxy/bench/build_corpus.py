#!/usr/bin/env python3
"""Шаг 1 стенда: корпус настоящих запросов сжатия из транскриптов локальной модели.

Запрос сжатия = история до границы `compact_boundary` МИНУС сообщения из
`compactMetadata.preservedMessages` (их CLI оставляет дословно, сводка их не видит) +
инструкция сжатия CLI. Из транскрипта собираем историю и хвост; инструкцию добавляет
`run_bench.py` в момент запроса (общий текст — `common.compact_instruction`).

Корпус ложится ТОЛЬКО вне репозитория (по умолчанию $BENCH_HOME/corpus): там чужие чаты.
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common  # noqa: E402

DEFAULT_ROOT = "/srv/ccs/data/claude-profiles/local-qwen/projects"


def repo_root():
    try:
        return subprocess.check_output(["git", "rev-parse", "--show-toplevel"],
                                       cwd=os.path.dirname(os.path.abspath(__file__)),
                                       text=True).strip()
    except Exception:  # noqa: BLE001
        return None


def find_boundaries(root):
    """[(путь, индекс строки, compactMetadata)] по всем основным транскриптам (без subagents/)."""
    res = []
    for d, _, files in os.walk(root):
        if os.sep + "subagents" in d:
            continue
        for fn in sorted(files):
            if not fn.endswith(".jsonl"):
                continue
            p = os.path.join(d, fn)
            recs = common.load_jsonl(p)
            for i, o in enumerate(recs):
                if o.get("subtype") == "compact_boundary" and o.get("compactMetadata"):
                    res.append((p, i, o["compactMetadata"]))
    return res


def build_item(path, idx, meta):
    recs = common.load_jsonl(path)
    boundary = recs[idx]
    by_uuid = {o["uuid"]: o for o in recs if o.get("uuid")}
    leaf = boundary.get("logicalParentUuid")
    chain, cur, seen = [], by_uuid.get(leaf), set()
    while cur is not None and cur["uuid"] not in seen:
        seen.add(cur["uuid"])
        chain.append(cur)
        cur = by_uuid.get(cur.get("parentUuid"))
    # Параллельные вызовы инструментов пишутся ветвящимися записями: по parentUuid доходит
    # только одна ветка результатов. Поэтому к цепочке добавляем все user/assistant-записи
    # файла между предыдущей границей и этой (в порядке файла).
    prev = max([j for j, o in enumerate(recs[:idx]) if o.get("subtype") == "compact_boundary"],
               default=-1)
    pos = {id(o): j for j, o in enumerate(recs)}
    extra = [o for o in recs[prev + 1:idx]
             if o.get("type") in ("user", "assistant") and not o.get("isSidechain")
             and o.get("uuid") not in seen]
    chain = sorted(chain + extra, key=lambda o: pos[id(o)])
    pres = set((meta.get("preservedMessages") or {}).get("allUuids") or [])
    history = [o for o in chain if o["uuid"] not in pres]
    preserved = [o for o in chain if o["uuid"] in pres]
    summary = next((o for o in recs[idx + 1:] if o.get("isCompactSummary")), None)
    summary_text = common.human_text(summary) if summary else ""
    variant = "up_to" if "Work Completed" in summary_text or pres else "full"
    hist_msgs = common.chain_to_messages(history)
    pres_msgs = common.chain_to_messages(preserved)
    humans = []
    for n, o in enumerate(history):
        if common.is_human(o):
            humans.append({"n": n, "pos": round(n / max(1, len(history)), 3),
                           "text": common.human_text(o)})
    return {
        "source": os.path.relpath(path, DEFAULT_ROOT), "boundary_line": idx,
        "meta": {k: meta.get(k) for k in ("trigger", "preTokens", "postTokens")},
        "variant": variant, "system": common.COMPACT_SYSTEM,
        "history": hist_msgs, "preserved": pres_msgs, "humans": humans,
        "actual_summary": summary_text,
        "est_tokens": common.est_tokens(hist_msgs),
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--root", default=DEFAULT_ROOT)
    ap.add_argument("--out", default=os.path.join(common.BENCH_HOME, "corpus"))
    ap.add_argument("--count", type=int, default=10)
    ap.add_argument("--min-pre", type=int, default=150000, help="минимум preTokens (порог маршрута сжатия)")
    ap.add_argument("--min-humans", type=int, default=2, help="минимум реплик человека в истории")
    ap.add_argument("--min-est", type=int, default=60000, help="минимум оценки токенов истории")
    ap.add_argument("--trigger", default="auto", choices=["auto", "manual", "any"],
                    help="какие сжатия брать; manual — ручные (без сохранённого хвоста), у них бывают живые диалоги")
    ap.add_argument("--max-est", type=int, default=400000, help="потолок оценки токенов истории")
    ap.add_argument("--list", action="store_true", help="только перечислить кандидатов")
    a = ap.parse_args()

    rr = repo_root()
    if rr and os.path.realpath(a.out).startswith(os.path.realpath(rr) + os.sep):
        sys.exit("корпус нельзя класть внутрь репозитория: там чужие чаты (--out вне git)")

    cands = [c for c in find_boundaries(a.root)
             if a.trigger in ("any", c[2].get("trigger")) and (c[2].get("preTokens") or 0) >= a.min_pre]
    pick = sorted(cands, key=lambda c: hashlib.sha1((c[0] + str(c[1])).encode()).hexdigest())
    print(f"кандидатов {len(cands)}, границ {len(pick)}", file=sys.stderr)
    if a.list:
        for p, i, m in pick:
            print(os.path.relpath(p, a.root), i, m.get("preTokens"))
        return
    os.makedirs(a.out, exist_ok=True)
    items = []
    for p, i, m in pick:
        item = build_item(p, i, m)
        # нужны живые поправки в середине истории: в корпус идут истории с несколькими
        # репликами человека, и чем их больше, тем раньше выбор
        if len(item["humans"]) < a.min_humans or not a.min_est <= item["est_tokens"] <= a.max_est:
            print("  пропуск (мало реплик человека или короткая):", item["source"], file=sys.stderr)
            continue
        items.append(item)
    items.sort(key=lambda it: -len(it["humans"]))
    uniq, seen_src = [], set()
    for it in items:  # по одной границе на сессию — истории не должны дублировать друг друга
        if it["source"] not in seen_src:
            seen_src.add(it["source"])
            uniq.append(it)
    n = 0
    for item in uniq[:a.count]:
        n += 1
        item["id"] = f"c{n:02d}"
        fp = os.path.join(a.out, item["id"] + ".json")
        with open(fp, "w", encoding="utf-8") as f:
            json.dump(item, f, ensure_ascii=False)
        os.chmod(fp, 0o600)
        print(f"{item['id']}  ~{item['est_tokens'] // 1000}k ток  реплик человека {len(item['humans'])}  "
              f"хвост {len(item['preserved'])} сообщ.  вариант {item['variant']}  {item['source']}")
    print(f"готово: {n} запросов в {a.out}")


if __name__ == "__main__":
    main()
