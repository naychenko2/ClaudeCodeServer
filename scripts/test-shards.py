#!/usr/bin/env python3
# coding: utf-8
"""Шарды тестового проекта по КЛАССАМ: раскладка, фильтры VSTest и самопроверка покрытия.

Раскладка — жадная упаковка классов по длительностям из профиля
(`scripts/test-profile.py <trx> --json`); класс, которого в профиле нет (или профиля нет
вовсе), уходит в шард по стабильному хешу полного имени mod N. Списка шардов в репозитории
нет: раскладка считается заново на каждый прогон из `dotnet test --list-tests`.

Самопроверка обязательна в каждом режиме: каждый тест списка попадает ровно в один шард,
иначе exit 1 (объединение шардов обязано совпасть с полным списком).

Режимы:
  python3 -I scripts/test-shards.py --list LIST.txt --shard 2/3 [--timings profile.json]
      печатает фильтр шарда 2 из 3 (CI: dotnet test ... --filter "$(...)");
  python3 -I scripts/test-shards.py --list LIST.txt --verify -n 3 [--timings ...]
      только самопроверка и сводка по шардам;
  python3 -I scripts/test-shards.py --run backend/ClaudeHomeServer.Tests -n 3 --results DIR
      локально: список, N параллельных `dotnet test --no-build` по шардам, TRX всех шардов
      в одной папке (её и читает test-profile.py), стена и пик RSS дерева процессов.
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys
import threading
import time

LIST_HEADER = "The following Tests are available:"
# Спецсимволы выражения фильтра VSTest, которые экранируются обратной косой
FILTER_SPECIAL = set("\\()&|=!~")


def test_fqn(display):
    """Полное имя метода из строки --list-tests: аргументы теории отрезаются."""
    return display.split("(", 1)[0].strip()


def class_of(fqn):
    return fqn.rsplit(".", 1)[0]


def parse_list(text):
    """Строки тестов из вывода `dotnet test --list-tests` (по всем сборкам вывода)."""
    tests, inside = [], False
    for line in text.splitlines():
        if line.strip() == LIST_HEADER:
            inside = True
            continue
        if inside:
            if line.startswith("    ") and line.strip():
                tests.append(test_fqn(line))
            elif line.strip():
                inside = False
    return tests


def classes_of(tests):
    counts = {}
    for t in tests:
        c = class_of(t)
        counts[c] = counts.get(c, 0) + 1
    return counts


def stable_bucket(cls, n):
    return int(hashlib.sha1(cls.encode("utf-8")).hexdigest(), 16) % n


def layout(classes, n, timings=None):
    """Список из n списков классов. timings — {класс: секунды} из профиля или None."""
    shards = [[] for _ in range(n)]
    load = [0.0] * n
    timings = timings or {}
    known = sorted((c for c in classes if c in timings), key=lambda c: (-timings[c], c))
    for c in known:
        i = min(range(n), key=lambda k: (load[k], k))
        shards[i].append(c)
        load[i] += timings[c]
    for c in sorted(c for c in classes if c not in timings):
        shards[stable_bucket(c, n)].append(c)
    return [sorted(s) for s in shards]


def escape(value):
    return "".join("\\" + ch if ch in FILTER_SPECIAL else ch for ch in value)


def shard_filter(shard):
    """Фильтр VSTest шарда: «содержит Класс.» — точка не даёт зацепить класс с тем же префиксом."""
    return "|".join(f"FullyQualifiedName~{escape(c)}." for c in shard)


def matches(cls, fqn):
    return (cls + ".") in fqn


def verify(tests, shards):
    """Ошибки покрытия: тест вне шардов или в нескольких сразу. Пусто — объединение == список."""
    errors = []
    for t in tests:
        hit = [i + 1 for i, s in enumerate(shards) if any(matches(c, t) for c in s)]
        if len(hit) != 1:
            errors.append(f"{t}: шардов {len(hit)} {hit}")
    return errors


def load_timings(path):
    if not path or not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as f:
        return {c: v["seconds"] for c, v in json.load(f)["classes"].items()}


def summary(tests, shards, timings):
    counts = classes_of(tests)
    out = []
    for i, s in enumerate(shards):
        sec = sum((timings or {}).get(c, 0.0) for c in s)
        out.append(f"  шард {i + 1}: классов {len(s)}, тестов {sum(counts.get(c, 0) for c in s)}, "
                   f"по профилю {sec:.1f} с")
    return "\n".join(out)


def checked_layout(tests, n, timings):
    shards = layout(classes_of(tests), n, timings)
    errors = verify(tests, shards)
    if errors:
        print(f"Объединение шардов не равно списку тестов ({len(errors)}):", file=sys.stderr)
        for e in errors[:20]:
            print("  " + e, file=sys.stderr)
        return None
    return shards


# --- Локальный прогон шардов ---

def descendants_rss_kb(root_pid):
    """Сумма VmRSS процесса и всех его потомков (Linux /proc); вне Linux — 0."""
    if not os.path.isdir("/proc"):
        return 0
    children, rss = {}, {}
    for pid in os.listdir("/proc"):
        if not pid.isdigit():
            continue
        try:
            with open(f"/proc/{pid}/status", encoding="utf-8") as f:
                ppid = kb = 0
                for line in f:
                    if line.startswith("PPid:"):
                        ppid = int(line.split()[1])
                    elif line.startswith("VmRSS:"):
                        kb = int(line.split()[1])
            children.setdefault(ppid, []).append(int(pid))
            rss[int(pid)] = kb
        except (OSError, ValueError):
            continue
    total, stack = 0, [root_pid]
    while stack:
        p = stack.pop()
        total += rss.get(p, 0)
        stack += children.get(p, [])
    return total


def dotnet_env():
    env = dict(os.environ)
    env.update({"DOTNET_CLI_UI_LANGUAGE": "en", "DOTNET_NOLOGO": "1"})
    return env


def run_shards(project, n, results, timings, extra_filter):
    listed = subprocess.run(["dotnet", "test", project, "--no-build", "--list-tests"]
                            + (["--filter", extra_filter] if extra_filter else []),
                            capture_output=True, text=True, env=dotnet_env())
    tests = parse_list(listed.stdout)
    if listed.returncode != 0 or not tests:
        print(listed.stdout[-2000:] + listed.stderr[-2000:], file=sys.stderr)
        return 2
    shards = checked_layout(tests, n, timings)
    if shards is None:
        return 1
    print(f"Тестов {len(tests)}, классов {len(classes_of(tests))}, шардов {n}:")
    print(summary(tests, shards, timings))
    os.makedirs(results, exist_ok=True)
    procs = []
    start = time.monotonic()
    for i, s in enumerate(shards):
        expr = f"({shard_filter(s)})" + (f"&({extra_filter})" if extra_filter else "")
        log = open(os.path.join(results, f"shard{i + 1}.log"), "w", encoding="utf-8")
        procs.append((subprocess.Popen(["dotnet", "test", project, "--no-build", "--filter", expr,
                                        "--logger", f"trx;LogFilePrefix=shard{i + 1}",
                                        "--results-directory", results],
                                       stdout=log, stderr=subprocess.STDOUT, env=dotnet_env()), log))
    peak = [0]
    done = threading.Event()

    def sample():
        while not done.is_set():
            peak[0] = max(peak[0], sum(descendants_rss_kb(p.pid) for p, _ in procs))
            done.wait(0.5)

    sampler = threading.Thread(target=sample, daemon=True)
    sampler.start()
    codes = []
    for p, log in procs:
        codes.append(p.wait())
        log.close()
    wall = time.monotonic() - start
    done.set()
    sampler.join()
    print(f"Стена шардов: {wall:.1f} с; коды выхода: {codes}; пик RSS дерева: {peak[0] / 1024 / 1024:.2f} ГиБ")
    return max(codes)


def main(argv=None):
    ap = argparse.ArgumentParser(description="Шарды тестов по классам с самопроверкой покрытия")
    ap.add_argument("--list", help="вывод `dotnet test --list-tests` (файл)")
    ap.add_argument("--shard", help="i/N — напечатать фильтр шарда i из N (с 1)")
    ap.add_argument("--verify", action="store_true", help="только самопроверка и сводка")
    ap.add_argument("--run", metavar="PROJECT", help="локально: N параллельных dotnet test --no-build")
    ap.add_argument("-n", type=int, default=3, help="число шардов (для --verify и --run)")
    ap.add_argument("--results", default=os.path.join(".cc-attachments", "test-shards"), help="папка TRX для --run")
    ap.add_argument("--timings", help="JSON профиля (test-profile.py --json); нет файла — раскладка по хешу")
    ap.add_argument("--filter", dest="extra_filter", help="дополнительный фильтр к каждому шарду (--run)")
    args = ap.parse_args(argv)
    timings = load_timings(args.timings)

    if args.run:
        return run_shards(args.run, args.n, args.results, timings, args.extra_filter)
    if not args.list:
        ap.error("нужен --list или --run")
    with open(args.list, encoding="utf-8", errors="replace") as f:
        tests = parse_list(f.read())
    if not tests:
        print(f"В {args.list} нет списка тестов", file=sys.stderr)
        return 2
    n, index = args.n, None
    if args.shard:
        index, n = (int(x) for x in args.shard.split("/"))
        if not 1 <= index <= n:
            ap.error("--shard i/N: 1 ≤ i ≤ N")
    shards = checked_layout(tests, n, timings)
    if shards is None:
        return 1
    if index is not None:
        print(shard_filter(shards[index - 1]))
    else:
        print(f"OK: тестов {len(tests)}, каждый ровно в одном из {n} шардов")
        print(summary(tests, shards, timings))
    return 0


if __name__ == "__main__":
    sys.exit(main())
