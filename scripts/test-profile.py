#!/usr/bin/env python3
# coding: utf-8
"""Профиль прогона тестов по TRX: гистограмма длительностей, топ тестов и классов,
доля «хостовых» классов (в чьём .cs встречается TestWebApplicationFactory).

Только читает: TRX и исходники не меняются, ничего не пишется на диск.

Запуск:  python3 -I scripts/test-profile.py <папка артефакта run_tests | файл.trx> [--src КОРЕНЬ]
         ... --json              — длительность каждого класса в JSON (вход для scripts/test-shards.py)
         ... --check [--threshold 5] — сторож длительности: exit 1, если есть тест дольше порога;
             тесты между целью доводки (2 с) и порогом печатаются отдельным блоком, exit 0.
             Гонять ТОЛЬКО на TRX набора Fast (фильтр исключает Host/Slow/Integration/Node/Bench):
             там каждый тест дольше порога — нехостовый тест без метки Slow.
"""
import argparse
import json
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
SLOW_MARK_RE = re.compile(r'\[\s*Trait\(\s*"Category"\s*,\s*"Slow"\s*\)\s*\]')
TEST_ATTR_RE = re.compile(r"\[\s*(?:Fact|Theory)\b")


def parse_duration(text):
    """'HH:MM:SS.fffffff' -> секунды; мусор и пустое значение дают 0."""
    try:
        h, m, s = text.split(":")
        return int(h) * 3600 + int(m) * 60 + float(s)
    except (AttributeError, ValueError):
        return 0.0


# Фильтр набора Fast: исключение категорий, а не перечень меток. Единственная копия в скриптах.
FAST_FILTER = "Category!=Host&Category!=Slow&Category!=Integration&Category!=Node&Category!=Bench"
# Гейт — 5 с: стену одного теста под конкуренцией десятков сборок за ядра мерить с точностью
# до 2 с бессмысленно (решение Александра 29.10). 2 с — цель доводки Э4, не гейт
CHECK_THRESHOLD = 5.0
TARGET_SECONDS = 2.0


def find_trx(path):
    if os.path.isfile(path):
        return [path]
    found = []
    for root, dirs, files in os.walk(path):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        found += [os.path.join(root, f) for f in files if f.lower().endswith(".trx")]
    return sorted(found)


# Окно «одного прогона» для --latest-run: потолок run_tests и предгейта — 9 мин
LATEST_RUN_WINDOW = 600


def latest_run(files, window=LATEST_RUN_WINDOW):
    """TRX последнего прогона в общей папке: не старше самого свежего файла больше чем на окно."""
    if not files:
        return files
    newest = max(os.path.getmtime(f) for f in files)
    return [f for f in files if newest - os.path.getmtime(f) <= window]


def read_trx(path):
    """Возвращает (результаты, число не выполненных); результат = (класс, тест, секунды).

    Не выполненные (пропущенные) тоже входят в результаты с их длительностью (≈0) и попадают
    в срез «< 0,1 с»: так профиль сходится с общим числом тестов прогона."""
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
        cls = classes.get(r.get("testId"), "?")
        name = r.get("testName") or "?"
        if name.startswith(cls + "."):
            name = name[len(cls) + 1:]  # xUnit кладёт в testName полное имя
        results.append((cls, name, parse_duration(r.get("duration"))))
    return results, skipped


def read_class_projects(path):
    """{класс: имя тестового проекта} по codeBase теста («…/Имя.Tests.dll»); нет codeBase — класс пропущен."""
    root = ET.parse(path).getroot()
    ns = {"t": root.tag[1:root.tag.index("}")]} if root.tag.startswith("{") else {}
    q = (lambda n: "t:" + n) if ns else (lambda n: n)
    found = {}
    for ut in root.iter(("{%s}" % ns["t"] if ns else "") + "UnitTest"):
        method = ut.find(q("TestMethod"), ns)
        code = method.get("codeBase") if method is not None else None
        if code and method.get("className"):
            found[method.get("className")] = os.path.basename(code.replace("\\", "/")).rsplit(".", 1)[0]
    return found


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


def count_slow_tests(src):
    """(меток Slow, тестов под ними) по исходникам: TRX категорий не несёт.

    Метка на классе покрывает все его [Fact]/[Theory], на методе — один; [Theory] считается
    один раз (строки данных по исходникам не посчитать), так что это нижняя оценка. Границу
    класса ищет по скобкам без учёта строк — для отчёта о росте точности хватает."""
    marks = tests = 0
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
            for m in SLOW_MARK_RE.finditer(text):
                marks += 1
                brace = text.find("{", m.end())
                head = text[m.end():brace] if brace >= 0 else ""
                if not CLASS_RE.search(head):
                    tests += 1  # метка на методе
                    continue
                depth, end = 0, brace
                for i in range(brace, len(text)):
                    depth += (text[i] == "{") - (text[i] == "}")
                    if depth == 0:
                        end = i
                        break
                tests += len(TEST_ATTR_RE.findall(text[brace:end]))
    return marks, tests


def bucket_of(sec):
    for i, (lo, hi, _) in enumerate(BUCKETS):
        if lo <= sec < hi:
            return i
    return len(BUCKETS) - 1


def pct(part, whole):
    return 100.0 * part / whole if whole else 0.0


def fmt(sec, digits=2):
    return f"{sec:.{digits}f}".replace(".", ",")


def build_report(results, skipped, host, known, top_tests=25, top_classes=20,
                fast_results=None, slow=None):
    """fast_results — результаты прохода Fast (None: все переданные TRX считаются Fast);
    slow — (меток, тестов) из count_slow_tests или None, если исходники Slow не сканировались.
    Две строки про Fast и Slow — отчёт о росте Slow-меток, не гейт (вердикт 29.10)."""
    total = sum(s for _, _, s in results)
    out = [f"Тестов: {len(results)} (из них не выполнено: {skipped}), "
           f"сумма длительностей тестов (не wall-clock): {fmt(total)} с"]
    fast = results if fast_results is None else fast_results
    scope = "все переданные TRX" if fast_results is None else "TRX с префиксом fast"
    out.append(f"Fast: {len(fast)} тестов в {len({c for c, _, _ in fast})} классах ({scope})")
    if slow is not None:
        out.append(f"Под Slow: {slow[1]} тестов ({slow[0]} меток в исходниках; [Theory] — как один тест)")
    out.append("")

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
        # Доля хостовых зависит от --src: неизвестные классы считаются нехостовыми, поэтому
        # их долю видно рядом, а не только числом.
        out.append(f"Классы не найдены в исходниках (считаются нехостовыми): {unknown} тестов "
                   f"({fmt(pct(unknown, len(results)), 1)}%) — для прогона нескольких проектов задай --src backend")
    return "\n".join(out)


def class_durations(results, projects=None, fast_results=None):
    """{класс: {"tests": n, "seconds": t[, "fast": f][, "project": p]}} — вход для раскладки шардов;
    fast — секунды класса в проходе Fast (TRX с префиксом fast), по ним шарды учитывают, что класс
    серийный и держит один поток прохода; project
    (тестовый проект класса) нужен шардированию по проектам: пространство имён класса с именем
    проекта не совпадает."""
    per_class = {}
    for cls, _, s in results:
        n, t = per_class.get(cls, (0, 0.0))
        per_class[cls] = (n + 1, t + s)
    out = {c: {"tests": n, "seconds": round(t, 3)} for c, (n, t) in sorted(per_class.items())}
    if fast_results is not None:
        fast = {}
        for cls, _, s in fast_results:
            fast[cls] = fast.get(cls, 0.0) + s
        for c, v in out.items():
            v["fast"] = round(fast.get(c, 0.0), 3)
    for c, p in (projects or {}).items():
        if c in out:
            out[c]["project"] = p
    return out


def slow_offenders(results, threshold):
    """Тесты дольше порога, по убыванию: на TRX набора Fast это нехостовые тесты без Slow."""
    return sorted((r for r in results if r[2] > threshold), key=lambda r: -r[2])


def main(argv=None):
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description="Профиль тестов по TRX (только чтение)")
    ap.add_argument("path", help="папка артефакта run_tests или путь к .trx")
    ap.add_argument("--src", default=os.path.join(os.path.dirname(here), "backend", "ClaudeHomeServer.Tests"),
                    help="где искать .cs для определения хостовых классов (по умолчанию монолит "
                         "backend/ClaudeHomeServer.Tests; для прогона нескольких проектов — backend/)")
    ap.add_argument("--slow-src", default=os.path.join(os.path.dirname(here), "backend"),
                    help="где считать метки Slow (по умолчанию весь backend/: метки живут в нескольких проектах)")
    ap.add_argument("--top-tests", type=int, default=25)
    ap.add_argument("--top-classes", type=int, default=20)
    ap.add_argument("--json", action="store_true", help="длительность каждого класса в JSON (stdout)")
    ap.add_argument("--check", action="store_true",
                    help="сторож длительности на TRX набора Fast: exit 1, если есть тест дольше --threshold")
    ap.add_argument("--threshold", type=float, default=CHECK_THRESHOLD, help="порог --check, секунды (по умолчанию 5)")
    ap.add_argument("--latest-run", action="store_true",
                    help="только TRX последнего прогона в папке (предгейт пишет в одну и ту же папку)")
    ap.add_argument("--target", type=float, default=TARGET_SECONDS,
                    help="цель доводки, секунды: тесты между целью и порогом печатаются отдельно, exit 0")
    args = ap.parse_args(argv)

    files = find_trx(args.path)
    if args.latest_run:
        files = latest_run(files)
    if not files:
        print(f"TRX не найден: {args.path}", file=sys.stderr)
        return 2
    results, skipped, broken, projects, fast_results, saw_fast = [], 0, 0, {}, [], False
    for f in files:
        try:
            r, s = read_trx(f)
        except ET.ParseError as e:
            # Битый или посторонний .trx не роняет профиль по остальным файлам.
            print(f"предупреждение: пропущен нечитаемый TRX {f}: {e}", file=sys.stderr)
            broken += 1
            continue
        results += r
        skipped += s
        if os.path.basename(f).lower().startswith("fast"):
            fast_results += r
            saw_fast = True
        if args.json:
            projects.update(read_class_projects(f))
    if broken == len(files):
        print(f"Ни одного читаемого TRX: {args.path}", file=sys.stderr)
        return 2
    if args.json:
        json.dump({"tests": len(results), "skipped": skipped, "seconds": round(sum(r[2] for r in results), 3),
                   "classes": class_durations(results, projects, fast_results if saw_fast else None)}, sys.stdout, ensure_ascii=False, indent=1)
        print()
        return 0
    if args.check:
        bad = slow_offenders(results, args.threshold)
        near = [r for r in slow_offenders(results, args.target) if r[2] <= args.threshold]
        if near:
            print(f"Цель доводки: {len(near)} тестов дольше {fmt(args.target, 1)} с, но не дольше "
                  f"{fmt(args.threshold, 1)} с — не гейт:")
            for cls, name, s in near:
                print(f"  {fmt(s):>8} с  {cls}.{name}")
        for cls, name, s in bad:
            print(f"  {fmt(s):>8} с  {cls}.{name}")
        if bad:
            print(f"Сторож длительности: {len(bad)} тестов дольше {fmt(args.threshold, 1)} с без метки Slow "
                  f"(TRX набора Fast). Переведи ожидание на событие или поставь [Trait(\"Category\", \"Slow\")] "
                  f"на метод.", file=sys.stderr)
            return 1
        print(f"Сторож длительности: OK, {len(results)} тестов, все не дольше {fmt(args.threshold, 1)} с")
        return 0
    host, known = host_classes(args.src)
    print(build_report(results, skipped, host, known, args.top_tests, args.top_classes,
                       fast_results if saw_fast else None, count_slow_tests(args.slow_src)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
