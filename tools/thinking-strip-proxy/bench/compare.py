#!/usr/bin/env python3
"""Сводит прогоны двух схем при одном судье: `compare.py <каталог прогонов> <схема A> <схема B>`.

Читает `results.json` всех повторов (`<схема>-r1`, `-r2`…), пишет markdown в stdout: средние по
типам вопросов, долю сохранённых (≥4), поправки настоящие/синтетические, размер контекста после
сжатия, шум между повторами и парную разность по запросам.
"""
import glob
import json
import os
import statistics as st
import sys

TYPES = ["fact", "artifact", "continuation", "decision", "correction", "correction_syn"]
RU = {"fact": "факты", "artifact": "артефакты", "continuation": "продолжение", "decision": "решения",
      "correction": "поправки (настоящие)", "correction_syn": "поправки (синтетические)", "all": "все"}
GOOD = 4


def load(d, scheme):
    runs = []
    for p in sorted(glob.glob(os.path.join(d, scheme + "-r*", "results.json"))):
        runs.append(json.load(open(p, encoding="utf-8")))
    return runs


def sel(run, t=None):
    return [q for q in run["per_question"] if q["score"] is not None and (t in (None, "all") or q["type"] == t)]


def mean(xs):
    xs = list(xs)
    return sum(xs) / len(xs) if xs else None


def f(x, nd=2):
    return "—" if x is None else f"{x:.{nd}f}"


def main():
    d, A, B = sys.argv[1:4]
    ra, rb = load(d, A), load(d, B)
    print(f"Судья: {ra[0]['judge']}; повторов: {A} — {len(ra)}, {B} — {len(rb)}\n")

    print("## Средняя оценка 0–5 по типам вопросов (среднее по повторам; в скобках — по каждому повтору)\n")
    print(f"| тип | вопросов | {A} | {B} | Δ ({B}−{A}) | доля ≥4: {A} | доля ≥4: {B} |")
    print("|---|---|---|---|---|---|---|")
    for t in TYPES + ["all"]:
        n = len(sel(ra[0], t))
        ma = [mean(q["score"] for q in sel(r, t)) for r in ra]
        mb = [mean(q["score"] for q in sel(r, t)) for r in rb]
        ga = mean(mean(q["score"] >= GOOD for q in sel(r, t)) for r in ra)
        gb = mean(mean(q["score"] >= GOOD for q in sel(r, t)) for r in rb)
        s = lambda xs: f"{f(mean(xs))} (" + " / ".join(f(x) for x in xs) + ")"
        print(f"| {RU[t]} | {n} | {s(ma)} | {s(mb)} | {f(mean(mb) - mean(ma))} | {f(ga)} | {f(gb)} |")

    print("\n## Сохранность поправок человека (доля ответов с оценкой ≥4, по повторам)\n")
    print(f"| вид | вопросов | {A} | {B} |")
    print("|---|---|---|---|")
    for t in ("correction", "correction_syn"):
        n = len(sel(ra[0], t))
        fa = " / ".join(f(mean(q["score"] >= GOOD for q in sel(r, t))) for r in ra)
        fb = " / ".join(f(mean(q["score"] >= GOOD for q in sel(r, t))) for r in rb)
        print(f"| {RU[t]} | {n} | {fa} | {fb} |")
    for t in ("correction", "correction_syn"):
        va = mean(q["verbatim"] for r in ra for q in r["per_question"] if q["type"] == t and q["verbatim"] is not None)
        vb = mean(q["verbatim"] for r in rb for q in r["per_question"] if q["type"] == t and q["verbatim"] is not None)
        print(f"\nДословность реплики в сводке ({RU[t]}, доля 3-словных окон): {A} {f(va)}, {B} {f(vb)}")

    print("\n## Размер сводки после сжатия (без сохранённого CLI хвоста, одинакового у обеих схем)\n")
    print(f"| запрос | {A}, симв. | {B}, симв. | в т.ч. блок | {A}, ≈токенов | {B}, ≈токенов |")
    print("|---|---|---|---|---|---|")
    ids = sorted(ra[0]["per_item"])
    tot = {"a": [], "b": [], "blk": []}
    for i in ids:
        ca = mean(r["per_item"][i]["chars"] for r in ra)
        cb = mean(r["per_item"][i]["chars"] for r in rb)
        blk = mean((r["per_item"][i]["meta"] or {}).get("block_chars", 0) for r in rb)
        tot["a"].append(ca); tot["b"].append(cb); tot["blk"].append(blk)
        print(f"| {i} | {ca:.0f} | {cb:.0f} | {blk:.0f} | {ca / 3:.0f} | {cb / 3:.0f} |")
    print(f"| **среднее** | {mean(tot['a']):.0f} | {mean(tot['b']):.0f} | {mean(tot['blk']):.0f} | "
          f"{mean(tot['a']) / 3:.0f} | {mean(tot['b']) / 3:.0f} |")

    print("\n## Шум и парная разность\n")
    for name, runs in ((A, ra), (B, rb)):
        if len(runs) >= 2:
            diffs = []
            for i in ids:
                x = [mean(q["score"] for q in sel(r) if q["id"] == i) for r in runs[:2]]
                diffs.append(abs(x[0] - x[1]))
            tm = [abs(mean(q["score"] for q in sel(runs[0], t)) - mean(q["score"] for q in sel(runs[1], t)))
                  for t in TYPES]
            print(f"- {name}: средний |Δ| между повторами по запросу {f(mean(diffs))} (макс {f(max(diffs))}), "
                  f"по типу вопросов {f(mean(tm))} (макс {f(max(tm))})")
    pq = []
    for i in ids:
        a = mean(q["score"] for r in ra for q in sel(r) if q["id"] == i)
        b = mean(q["score"] for r in rb for q in sel(r) if q["id"] == i)
        pq.append(b - a)
    se = st.stdev(pq) / len(pq) ** .5
    print(f"- парная разность по запросам ({B}−{A}): среднее {f(mean(pq))} ± {f(se)} (стандартная ошибка, "
          f"{len(pq)} запросов); улучшились {sum(x > 0.05 for x in pq)}, ухудшились {sum(x < -0.05 for x in pq)}")
    print("\n| запрос | " + A + " | " + B + " | Δ |")
    print("|---|---|---|---|")
    for i, dlt in zip(ids, pq):
        a = mean(q["score"] for r in ra for q in sel(r) if q["id"] == i)
        b = mean(q["score"] for r in rb for q in sel(r) if q["id"] == i)
        print(f"| {i} | {f(a)} | {f(b)} | {dlt:+.2f} |")


if __name__ == "__main__":
    main()
