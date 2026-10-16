#!/usr/bin/env python3
"""Калибровка судьи: перепроверяет кэшированные ответы прогона другим судьёй.

Берёт `runs/<прогон>/cNN.eval.json` (ответы уже есть, оценки старого судьи), просит нового
судью оценить те же ответы дважды и считает: согласие со старым судьёй (корреляция Пирсона,
средняя разность, доля совпадений ±1) и шум самого нового судьи (две оценки одного ответа).
Нужен, чтобы смена судьи не воспринималась на веру. Судья — как в run_bench (BENCH_JUDGE=local).
"""
import json
import os
import statistics
import sys
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import common  # noqa: E402
import run_bench as rb  # noqa: E402


def pearson(x, y):
    mx, my = statistics.mean(x), statistics.mean(y)
    sx = sum((a - mx) ** 2 for a in x) ** .5
    sy = sum((b - my) ** 2 for b in y) ** .5
    return sum((a - mx) * (b - my) for a, b in zip(x, y)) / (sx * sy) if sx and sy else float("nan")


def main():
    run = sys.argv[1] if len(sys.argv) > 1 else os.path.join(common.BENCH_HOME, "runs", "old")
    corpus = os.path.join(common.BENCH_HOME, "corpus")
    local = common.local_endpoint()
    jep = (local[0], "x", os.environ.get("BENCH_JUDGE_LOCAL_MODEL", local[2]))
    jobs = []
    for fn in sorted(os.listdir(corpus)):
        if not fn.endswith(".json"):
            continue
        ev_p = os.path.join(run, fn.replace(".json", ".eval.json"))
        if not os.path.exists(ev_p):
            continue
        ev = json.load(open(ev_p, encoding="utf-8"))
        qs = json.load(open(os.path.join(corpus, "questions", fn), encoding="utf-8"))["questions"]
        for k, e in ev.items():
            if e.get("score") is not None:
                jobs.append((fn, qs[int(k)], e))

    def work(j):
        _, q, e = j
        return rb.judge(q, e["answer"], jep)[0], rb.judge(q, e["answer"], jep)[0]

    with ThreadPoolExecutor(2) as ex:
        res = list(ex.map(work, jobs))
    old = [e["score"] for _, _, e in jobs]
    a = [r[0] if r[0] is not None else 0 for r in res]
    b = [r[1] if r[1] is not None else 0 for r in res]
    print(f"ответов: {len(jobs)}; судья: {jep[2]}")
    print(f"средняя оценка: старый {statistics.mean(old):.2f}, новый {statistics.mean(a):.2f} / {statistics.mean(b):.2f}")
    print(f"корреляция со старым судьёй: {pearson(old, a):.2f} / {pearson(old, b):.2f}")
    print(f"доля совпадений ±1 со старым: {sum(abs(x - y) <= 1 for x, y in zip(old, a)) / len(a):.2f}")
    print(f"шум нового судьи (две оценки одного ответа): корреляция {pearson(a, b):.2f}, "
          f"среднее |Δ| {statistics.mean(abs(x - y) for x, y in zip(a, b)):.2f}, "
          f"совпали точно {sum(x == y for x, y in zip(a, b)) / len(a):.2f}")
    bin_old = [s >= rb.GOOD for s in old]
    bin_a = [s >= rb.GOOD for s in a]
    print(f"согласие по порогу «сохранено» (≥4): {sum(x == y for x, y in zip(bin_old, bin_a)) / len(a):.2f}")


if __name__ == "__main__":
    main()
