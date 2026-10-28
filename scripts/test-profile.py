#!/usr/bin/env python3
# coding: utf-8
"""Профиль прогона тестов по TRX: гистограмма длительностей, топ тестов и классов,
доля «хостовых» классов (в чьём .cs встречается TestWebApplicationFactory).

Только читает: TRX и исходники не меняются, ничего не пишется на диск.

Запуск:  python3 -I scripts/test-profile.py <папка артефакта run_tests | файл.trx> [--src КОРЕНЬ]
"""
import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

HOST_MARKER = "TestWebApplicationFactory"
# Срезы длительности в секундах: [нижняя, верхняя). Последний — без верхней границы.
BUCKETS = [(0.0, 0.1, "< 0,1 с"), (0.1, 1.0, "0,1–1 с"), (1.0, 5.0, "1–5 с"),
           (5.0, 30.0, "5–30 с"), (30.0, float("inf"), "≥ 30 с")]
CLASS_RE = re.compile(r"\b(?:class|record|struct)\s+([A-Za-z_]\w*)")
SKIP_DIRS = {"bin", "obj", "node_modules", ".git"}


def parse_duration(text):
    """'HH:MM:SS.fffffff' -> секунды; мусор и пустое значение дают 0."""
    try:
        h, m, s = text.split(":")
        return int(h) * 3600 + int(m) * 60 + float(s)
    except (AttributeError, ValueError):
        return 0.0


def find_trx(path):
    if os.path.isfile(path):
        return [path]
    found = []
    for root, dirs, files in os.walk(path):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        found += [os.path.join(root, f) for f in files if f.lower().endswith(".trx")]
    return sorted(found)


def read_trx(path):
    """Возвращает (результаты, число не выполненных); результат = (класс, тест, секунды)."""
    root = ET.parse(path).getroot()
    ns = {"t": root.tag[1:root.tag.index("}")]} if root.tag.startswith("{") else {}
    q = (lambda n: "t:" + n) if ns else (lambda n: n)
    classes = {}
    for ut in root.iter(("{%s}" % ns["t"] if ns else "") + "UnitTest"):
        method = ut.find(q("TestMethod"), ns)
        if method is not None:
            classes[ut.get("id")] = method.get("className") or "?"
    results, skipped = [], 0
    for r in root.iter(("{%s}" % ns["t"] if ns else "") + "UnitTestResult"):
        if r.get("outcome") not in ("Passed", "Failed"):
            skipped += 1
            continue
        cls = classes.get(r.get("testId"), "?")
        name = r.get("testName") or "?"
        if name.startswith(cls + "."):
            name = name[len(cls) + 1:]  # xUnit кладёт в testName полное имя
        results.append((cls, name, parse_duration(r.get("duration"))))
    return results, skipped


def short_class(full):
    """'Ns.Outer+Inner' -> 'Inner'; для поиска файла нужно простое имя."""
    return re.split(r"[.+]", full)[-1]


def host_classes(src):
    """Простые имена классов, в чьих .cs есть HOST_MARKER; плюс множество всех найденных."""
    host, known = set(), set()
    for root, dirs, files in os.walk(src):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if not f.endswith(".cs"):
                continue
            try:
                with open(os.path.join(root, f), encoding="utf-8-sig", errors="replace") as fh:
                    text = fh.read()
            except OSError:
                continue
            names = set(CLASS_RE.findall(text))
            known |= names
            if HOST_MARKER in text:
                host |= names
    return host, known


def bucket_of(sec):
    for i, (lo, hi, _) in enumerate(BUCKETS):
        if lo <= sec < hi:
            return i
    return len(BUCKETS) - 1


def pct(part, whole):
    return 100.0 * part / whole if whole else 0.0


def fmt(sec, digits=2):
    return f"{sec:.{digits}f}".replace(".", ",")


def build_report(results, skipped, host, known, top_tests=25, top_classes=20):
    total = sum(s for _, _, s in results)
    out = [f"Тестов: {len(results)}, суммарное время: {fmt(total)} с, не выполнено: {skipped}", ""]

    out.append("Гистограмма длительностей:")
    counts, sums = [0] * len(BUCKETS), [0.0] * len(BUCKETS)
    for _, _, s in results:
        i = bucket_of(s)
        counts[i] += 1
        sums[i] += s
    for (_, _, label), c, t in zip(BUCKETS, counts, sums):
        out.append(f"  {label:<9} {c:>7} тестов {fmt(pct(c, len(results)), 1):>6}%   {fmt(t):>10} с {fmt(pct(t, total), 1):>6}%")

    out += ["", f"Топ-{top_tests} тестов:"]
    for cls, name, s in sorted(results, key=lambda r: -r[2])[:top_tests]:
        out.append(f"  {fmt(s):>8} с  {short_class(cls)}.{name}")

    per_class = {}
    for cls, _, s in results:
        n, t = per_class.get(cls, (0, 0.0))
        per_class[cls] = (n + 1, t + s)
    out += ["", f"Топ-{top_classes} классов (сумма времени):"]
    for cls, (n, t) in sorted(per_class.items(), key=lambda kv: -kv[1][1])[:top_classes]:
        mark = "хост" if short_class(cls) in host else "    "
        out.append(f"  {fmt(t):>8} с  {n:>5} тестов  {mark}  {cls}")

    h_n = h_t = unknown = 0
    for cls, (n, t) in per_class.items():
        short = short_class(cls)
        if short in host:
            h_n += n
            h_t += t
        if short not in known:
            unknown += n
    out += ["", f"Хостовые (TestWebApplicationFactory в .cs класса): {h_n} тестов ({fmt(pct(h_n, len(results)), 1)}%), "
            f"{fmt(h_t)} с ({fmt(pct(h_t, total), 1)}% времени)"]
    if unknown:
        out.append(f"Классы не найдены в исходниках (считаются нехостовыми): {unknown} тестов")
    return "\n".join(out)


def main(argv=None):
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description="Профиль тестов по TRX (только чтение)")
    ap.add_argument("path", help="папка артефакта run_tests или путь к .trx")
    ap.add_argument("--src", default=os.path.join(os.path.dirname(here), "backend"),
                    help="где искать .cs для определения хостовых классов (по умолчанию backend/)")
    ap.add_argument("--top-tests", type=int, default=25)
    ap.add_argument("--top-classes", type=int, default=20)
    args = ap.parse_args(argv)

    files = find_trx(args.path)
    if not files:
        print(f"TRX не найден: {args.path}", file=sys.stderr)
        return 2
    results, skipped = [], 0
    for f in files:
        r, s = read_trx(f)
        results += r
        skipped += s
    host, known = host_classes(args.src)
    print(build_report(results, skipped, host, known, args.top_tests, args.top_classes))
    return 0


if __name__ == "__main__":
    sys.exit(main())
