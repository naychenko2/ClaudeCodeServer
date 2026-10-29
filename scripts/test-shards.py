#!/usr/bin/env python3
# coding: utf-8
"""Шарды тестового проекта по КЛАССАМ: раскладка, фильтры VSTest и самопроверка покрытия.

Раскладка — жадная упаковка классов по длительностям из профиля
(`scripts/test-profile.py <trx> --json`); класс, которого в профиле нет (или профиля нет
вовсе), уходит в шард по стабильному хешу полного имени mod N. Списка шардов в репозитории
нет: раскладка считается заново на каждый прогон из `dotnet test --list-tests`.

Остальные тестовые проекты решения («rest») шардируются по ПРОЕКТАМ: вес проекта — сумма
классов профиля с его именем в префиксе, шард получает slnx только со своими тестовыми проектами.

Самопроверка обязательна в каждом режиме: каждый тест списка попадает ровно в один шард
(у rest — каждый тестовый проект ровно в один), иначе exit 1.

Режимы:
  python3 -I scripts/test-shards.py --list LIST.txt --shard 2/3 [--timings profile.json]
      печатает фильтр шарда 2 из 3 (CI: dotnet test ... --filter "$(...)");
  python3 -I scripts/test-shards.py --list LIST.txt --verify -n 3 [--timings ...]
      только самопроверка и сводка по шардам;
  python3 -I scripts/test-shards.py --slnx backend/X.slnx --exclude A.Tests,B.Tests --shard 2/3 [--timings ...]
      печатает slnx шарда 2 из 3: все не тестовые проекты, плюс его долю тестовых проектов
      (без исключённых — их гоняют отдельные шарды по классам);
  python3 -I scripts/test-shards.py --run backend/ClaudeHomeServer.Tests -n 3 --results DIR
      локально: список, N параллельных `dotnet test --no-build` по шардам, TRX всех шардов
      в одной папке (её и читает test-profile.py), стена и пик RSS дерева процессов.
  python3 -I scripts/test-shards.py --run-fast backend/X.slnx --split backend/A.Tests=3,backend/B.Tests=2 \
      --filter "<фильтр Fast>" --results DIR
      Fast предгейта: проекты из --split идут шардами по классам, остальные тестовые проекты решения —
      одним процессом на «rest»-slnx; всё параллельно, TRX в одной папке, стена и пик RSS общие.
"""
import argparse
import hashlib
import json
import os
import re
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


# Во сколько раз сумма длительностей тестов проекта больше его стены: 4 потока xUnit на 4 vCPU раннера,
# а длительности теста раздуты ожиданием процессора. Замер CI 29.10 (6 шардов монолита): отношение суммы
# к стене прохода 4,3–5,6, модель с 5 даёт корреляцию с реальной стеной шарда 0,89 против 0,44 у голой суммы.
PARALLEL = 5.0


def pass_cost(total, longest):
    """Оценка стены прохода: класс серийный и держит поток целиком, остальное делится на потоки."""
    return max(longest, total / PARALLEL)


def layout(classes, n, timings=None, fast=None):
    """Список из n списков классов. timings — {класс: секунды} из профиля или None.

    fast — {класс: секунды в проходе Fast} из профиля. Есть — класс кладётся в шард, где РЕЗУЛЬТАТ
    (стена Fast + стена остального прохода, каждая — max(самый долгий класс, сумма / PARALLEL)) меньше:
    серийный гигант вроде SessionManagerTests (56 с в Fast) не делает шард медленным, пока рядом лежит
    лишь то, что в его проход укладывается бесплатно. Нет — жадная упаковка по сумме секунд."""
    shards = [[] for _ in range(n)]
    timings = timings or {}
    known = sorted((c for c in classes if c in timings), key=lambda c: (-timings[c], c))
    if fast is None:
        load = [0.0] * n
        for c in known:
            i = min(range(n), key=lambda k: (load[k], k))
            shards[i].append(c)
            load[i] += timings[c]
    else:
        acc = [[0.0, 0.0, 0.0, 0.0] for _ in range(n)]  # сумма и максимум Fast, сумма и максимум остального

        def cost(a):
            return pass_cost(a[0], a[1]) + pass_cost(a[2], a[3])

        def added(a, f, r):
            return [a[0] + f, max(a[1], f), a[2] + r, max(a[3], r)]

        for c in known:
            f = min(fast.get(c, 0.0), timings[c])
            r = timings[c] - f
            i = min(range(n), key=lambda k: (cost(added(acc[k], f, r)), cost(acc[k]), k))
            shards[i].append(c)
            acc[i] = added(acc[i], f, r)
    for c in sorted(c for c in classes if c not in timings):
        shards[stable_bucket(c, n)].append(c)
    return [sorted(s) for s in shards]


# --- Шардирование по проектам (rest) ---

PROJECT_LINE = re.compile(r'<Project\s+Path="([^"]+)"')


def project_name(path):
    """Имя проекта — имя csproj без расширения: «X/Y.Tests/Y.Tests.csproj» -> «Y.Tests»."""
    return os.path.basename(path.replace("\\", "/")).rsplit(".", 1)[0]


def is_test_project(name):
    return name.endswith(".Tests")


def slnx_test_projects(slnx_text, exclude=()):
    """Тестовые проекты решения (имя на «.Tests») без исключённых, по алфавиту."""
    names = {project_name(m.group(1)) for m in PROJECT_LINE.finditer(slnx_text)}
    return sorted(n for n in names if is_test_project(n) and n not in set(exclude))


def project_weights(projects, timings, class_projects=None):
    """Вес проекта — сумма секунд его классов в профиле. Проект класса берётся из профиля
    (class_projects: пространство имён с именем проекта не совпадает); класса там нет —
    проект с самым длинным именем-префиксом («Проект.»). Нет классов в профиле — проекта нет в весах."""
    weights = {}
    ordered = sorted(projects, key=len, reverse=True)
    for cls, sec in (timings or {}).items():
        owner = (class_projects or {}).get(cls)
        if owner is None:
            owner = next((p for p in ordered if cls.startswith(p + ".")), None)
        if owner in projects:
            weights[owner] = weights.get(owner, 0.0) + sec
    return weights


def project_layout(projects, n, timings=None, class_projects=None):
    """Список из n списков проектов: известные профилю — жадно по весу, остальные — по хешу."""
    weights = project_weights(projects, timings, class_projects)
    shards = [[] for _ in range(n)]
    load = [0.0] * n
    for p in sorted((p for p in projects if p in weights), key=lambda p: (-weights[p], p)):
        i = min(range(n), key=lambda k: (load[k], k))
        shards[i].append(p)
        load[i] += weights[p]
    for p in sorted(p for p in projects if p not in weights):
        shards[stable_bucket(p, n)].append(p)
    return [sorted(s) for s in shards]


def verify_projects(projects, shards):
    errors = []
    for p in projects:
        hit = [i + 1 for i, s in enumerate(shards) if p in s]
        if len(hit) != 1:
            errors.append(f"{p}: шардов {len(hit)} {hit}")
    extra = sorted(set(q for s in shards for q in s) - set(projects))
    errors += [f"{q}: нет в решении" for q in extra]
    return errors


def shard_slnx(slnx_text, shard, exclude=()):
    """slnx шарда: не тестовые проекты остаются (тестам нужны ссылки), тестовые — только
    проекты шарда; исключённые и чужие тестовые проекты убираются."""
    keep, drop = set(shard), set(exclude)
    out = []
    for line in slnx_text.splitlines():
        m = PROJECT_LINE.search(line)
        if m:
            name = project_name(m.group(1))
            if name in drop or (is_test_project(name) and name not in keep):
                continue
        out.append(line)
    return "\n".join(out) + "\n"


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


def load_profile(path):
    """(секунды по классам, проект по классам, секунды Fast по классам) из JSON профиля; нет файла —
    (None, None, None). Профиль без разбивки на проходы даёт fast = None: раскладка тогда по сумме."""
    if not path or not os.path.exists(path):
        return None, None, None
    with open(path, encoding="utf-8") as f:
        classes = json.load(f)["classes"]
    fast = {c: v["fast"] for c, v in classes.items() if "fast" in v}
    return ({c: v["seconds"] for c, v in classes.items()},
            {c: v["project"] for c, v in classes.items() if v.get("project")},
            fast or None)


def load_timings(path):
    return load_profile(path)[0]


def summary(tests, shards, timings, fast=None):
    counts = classes_of(tests)
    out = []
    for i, s in enumerate(shards):
        sec = sum((timings or {}).get(c, 0.0) for c in s)
        line = (f"  шард {i + 1}: классов {len(s)}, тестов {sum(counts.get(c, 0) for c in s)}, "
                f"по профилю {sec:.1f} с")
        if fast is not None:
            fs = [min(fast.get(c, 0.0), timings.get(c, 0.0)) for c in s]
            rs = [timings.get(c, 0.0) - f for c, f in zip(s, fs)]
            line += (f", оценка стены {pass_cost(sum(fs), max(fs, default=0.0)) + pass_cost(sum(rs), max(rs, default=0.0)):.0f} с")
        out.append(line)
    return "\n".join(out)


def checked_layout(tests, n, timings, fast=None):
    shards = layout(classes_of(tests), n, timings, fast)
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


def list_tests(project, extra_filter):
    """(тесты, ошибка) из `dotnet test --no-build --list-tests`."""
    listed = subprocess.run(["dotnet", "test", project, "--no-build", "--list-tests"]
                            + (["--filter", extra_filter] if extra_filter else []),
                            capture_output=True, text=True, env=dotnet_env())
    tests = parse_list(listed.stdout)
    if listed.returncode != 0 or not tests:
        return None, listed.stdout[-2000:] + listed.stderr[-2000:]
    return tests, None


def launch(args, log_path):
    log = open(log_path, "w", encoding="utf-8")
    return subprocess.Popen(["dotnet", "test"] + args, stdout=log, stderr=subprocess.STDOUT, env=dotnet_env()), log


def launch_shards(project, shards, results, prefix, extra_filter):
    procs = []
    for i, s in enumerate(shards):
        expr = f"({shard_filter(s)})" + (f"&({extra_filter})" if extra_filter else "")
        name = f"{prefix}{i + 1}"
        procs.append(launch([project, "--no-build", "--filter", expr, "--logger", f"trx;LogFilePrefix={name}",
                             "--results-directory", results], os.path.join(results, f"{name}.log")))
    return procs


def wait_all(procs, start):
    """Ждёт все процессы, меряя стену и пик RSS дерева; печатает итог, возвращает 0, только если все шарды вышли с 0.

    Убитый сигналом шард (OOM-killer, -9) даёт отрицательный код: max() проглотил бы его нулём соседа."""
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
    failed = [c for c in codes if c != 0]
    return max((c for c in failed if c > 0), default=1) if failed else 0


def run_shards(project, n, results, timings, extra_filter, fast=None):
    tests, err = list_tests(project, extra_filter)
    if tests is None:
        print(err, file=sys.stderr)
        return 2
    shards = checked_layout(tests, n, timings, fast)
    if shards is None:
        return 1
    print(f"Тестов {len(tests)}, классов {len(classes_of(tests))}, шардов {n}:")
    print(summary(tests, shards, timings, fast))
    os.makedirs(results, exist_ok=True)
    start = time.monotonic()
    return wait_all(launch_shards(project, shards, results, "shard", extra_filter), start)


def parse_split(text):
    """«путь=N,путь=N» -> [(проект, N)]; N ≥ 1."""
    out = []
    for item in (x for x in text.split(",") if x):
        path, _, n = item.rpartition("=")
        if not path or not n.isdigit() or int(n) < 1:
            raise ValueError(f"--split: ожидается «проект=N», получено «{item}»")
        out.append((path, int(n)))
    return out


def calibrate(results):
    """Профиль прошлого прогона из TRX той же папки (предгейт пишет в одну и ту же): раскладка без него
    идёт по хешу класса и сваливает серийных гигантов в один шард. Нет TRX или профиль не прочитался —
    (None, None, None), раскладка по хешу."""
    if not os.path.isdir(results):
        return None, None, None
    done = subprocess.run([sys.executable, "-I", os.path.join(os.path.dirname(os.path.abspath(__file__)), "test-profile.py"),
                           results, "--json", "--latest-run"], capture_output=True, text=True)
    if done.returncode != 0:
        return None, None, None
    import tempfile
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as f:
        f.write(done.stdout)
    try:
        return load_profile(f.name)
    finally:
        os.remove(f.name)


def run_fast(slnx, split, results, timings, extra_filter, fast=None):
    """Fast предгейта: проекты --split — шардами по классам, остальные тестовые проекты — одним процессом
    на rest-slnx. Каждый тест списка попадает ровно в один шард (иначе exit 1) — самопроверка до запуска."""
    start = time.monotonic()
    if timings is None:
        timings, _, fast = calibrate(results)
        print("Раскладка по профилю прошлого прогона" if timings else "Профиля нет — раскладка по хешу класса")
    with open(slnx, encoding="utf-8") as f:
        text = f.read()
    exclude = [os.path.basename(p.rstrip("/")) for p, _ in split]
    rest_projects = slnx_test_projects(text, exclude)
    listings, errors = {}, {}

    def lister(path):
        listings[path], errors[path] = list_tests(path, extra_filter)

    threads = [threading.Thread(target=lister, args=(p,)) for p, _ in split]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    layouts = []
    for path, n in split:
        if listings[path] is None:
            print(errors[path], file=sys.stderr)
            return 2
        shards = checked_layout(listings[path], n, timings, fast)
        if shards is None:
            return 1
        print(f"{os.path.basename(path.rstrip('/'))}: тестов {len(listings[path])}, шардов {n}")
        print(summary(listings[path], shards, timings, fast))
        layouts.append((path, shards))
    os.makedirs(results, exist_ok=True)
    procs = []
    rest_path = os.path.join(os.path.dirname(slnx), "ClaudeHomeServer.FastRest.slnx")
    try:
        if rest_projects:
            with open(rest_path, "w", encoding="utf-8") as f:
                f.write(shard_slnx(text, rest_projects, exclude))
            procs.append(launch([rest_path, "--no-build"] + (["--filter", extra_filter] if extra_filter else [])
                                + ["--logger", "trx;LogFilePrefix=fast-rest", "--results-directory", results],
                                os.path.join(results, "rest.log")))
        for path, shards in layouts:
            # Префикс fast: профиль этих TRX отдаёт секунды прохода Fast, и следующая раскладка
            # держит серийного гиганта (SessionManagerTests) в шарде с тем, что укладывается в его поток
            procs += launch_shards(path, shards, results, "fast-" + os.path.basename(path.rstrip("/")) + "-", extra_filter)
        print(f"Подготовка до запуска: {time.monotonic() - start:.1f} с; процессов {len(procs)}")
        run_start = time.monotonic()
        return wait_all(procs, run_start)
    finally:
        if os.path.exists(rest_path):
            os.remove(rest_path)


def parse_shard(arg, ap, default_n):
    if not arg:
        return None, default_n
    index, n = (int(x) for x in arg.split("/"))
    if not 1 <= index <= n:
        ap.error("--shard i/N: 1 ≤ i ≤ N")
    return index, n


def main_slnx(args, ap, timings, class_projects):
    with open(args.slnx, encoding="utf-8") as f:
        text = f.read()
    exclude = [x for x in args.exclude.split(",") if x]
    projects = slnx_test_projects(text, exclude)
    if not projects:
        print(f"В {args.slnx} нет тестовых проектов", file=sys.stderr)
        return 2
    index, n = parse_shard(args.shard, ap, args.n)
    shards = project_layout(projects, n, timings, class_projects)
    errors = verify_projects(projects, shards)
    if errors:
        print(f"Объединение шардов не равно набору тестовых проектов ({len(errors)}):", file=sys.stderr)
        for e in errors[:20]:
            print("  " + e, file=sys.stderr)
        return 1
    if index is not None:
        sys.stdout.write(shard_slnx(text, shards[index - 1], exclude))
        return 0
    weights = project_weights(projects, timings, class_projects)
    print(f"OK: тестовых проектов {len(projects)}, каждый ровно в одном из {n} шардов")
    for i, s in enumerate(shards):
        print(f"  шард {i + 1}: проектов {len(s)}, по профилю {sum(weights.get(p, 0.0) for p in s):.1f} с")
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description="Шарды тестов по классам с самопроверкой покрытия")
    ap.add_argument("--list", help="вывод `dotnet test --list-tests` (файл)")
    ap.add_argument("--shard", help="i/N — напечатать фильтр шарда i из N (с 1)")
    ap.add_argument("--slnx", help="решение: шардирование тестовых проектов по профилю (режим rest)")
    ap.add_argument("--exclude", default="", help="проекты через запятую, которых нет в --slnx-шардах")
    ap.add_argument("--verify", action="store_true", help="только самопроверка и сводка")
    ap.add_argument("--run", metavar="PROJECT", help="локально: N параллельных dotnet test --no-build")
    ap.add_argument("--run-fast", metavar="SLNX", help="локально: Fast решения — проекты --split шардами, остальные одним процессом")
    ap.add_argument("--split", default="", help="для --run-fast: «проект=N,проект=N»")
    ap.add_argument("-n", type=int, default=3, help="число шардов (для --verify и --run)")
    ap.add_argument("--results", default=os.path.join(".cc-attachments", "test-shards"), help="папка TRX для --run")
    ap.add_argument("--timings", help="JSON профиля (test-profile.py --json); нет файла — раскладка по хешу")
    ap.add_argument("--filter", dest="extra_filter", help="дополнительный фильтр к каждому шарду (--run)")
    args = ap.parse_args(argv)
    timings, class_projects, fast = load_profile(args.timings)

    if args.run_fast:
        try:
            split = parse_split(args.split)
        except ValueError as e:
            ap.error(str(e))
        return run_fast(args.run_fast, split, args.results, timings, args.extra_filter, fast)
    if args.run:
        return run_shards(args.run, args.n, args.results, timings, args.extra_filter, fast)
    if args.slnx:
        return main_slnx(args, ap, timings, class_projects)
    if not args.list:
        ap.error("нужен --list или --run")
    with open(args.list, encoding="utf-8", errors="replace") as f:
        tests = parse_list(f.read())
    if not tests:
        print(f"В {args.list} нет списка тестов", file=sys.stderr)
        return 2
    index, n = parse_shard(args.shard, ap, args.n)
    shards = checked_layout(tests, n, timings, fast)
    if shards is None:
        return 1
    if index is not None:
        print(shard_filter(shards[index - 1]))
    else:
        print(f"OK: тестов {len(tests)}, каждый ровно в одном из {n} шардов")
        print(summary(tests, shards, timings, fast))
    return 0


if __name__ == "__main__":
    sys.exit(main())
