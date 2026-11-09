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
  python3 -I scripts/test-shards.py --run backend/ClaudeHomeServer.Tests [-n auto|3] --results DIR
      локально: список, N параллельных `dotnet test --no-build` по шардам, TRX всех шардов
      в одной папке (её и читает test-profile.py), стена и пик RSS дерева процессов.
      `-n auto` (по умолчанию) — N по нагрузке машины (choose_shards), выбор — первой строкой вывода;
  [CCS_TEST_SHARDS_ADAPTIVE=backend/A.Tests=4] python3 -I scripts/test-shards.py --run-fast backend/X.slnx \
      --split backend/A.Tests=3,backend/B.Tests=2 --filter "<фильтр Fast>" --results DIR
      Fast предгейта: проекты из --split идут шардами по классам, остальные тестовые проекты решения —
      одним процессом на «rest»-slnx; всё параллельно, TRX и логи — в подпапке прогона DIR/<метка>-<pid>
      (профиль следующего прогона берётся из самой свежей), стена и пик RSS общие. Проекты из
      CCS_TEST_SHARDS_ADAPTIVE выбирают N по нагрузке под своим потолком; число в --split — прежнее
      фиксированное (его понимает скрипт старых деревьев, переменную он не видит). «проект=auto» в --split
      тоже принимается, но предгейт его не шлёт: старый скрипт на нём падает.

Перезапуск упавших (только --run и --run-fast): красный прогон, где каждый красный шард красен упавшими тестами
в полном TRX и упавших не больше --rerun-max (20), перезапускает ИХ один раз, последовательно, фильтром по точному
FQN. Прошли все — exit 0, блок «Флаки» в сводке и <--results>/flaky.json; упал хоть один — красный, «Упали дважды».
Аборт шарда перезапуском не маскируется. Выключение — --no-rerun или CCS_TEST_SHARDS_RERUN=0.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import xml.etree.ElementTree as ET

LIST_HEADER = "The following Tests are available:"
# Зерно mtime файловой системы: файл, записанный сразу после запуска, не должен выглядеть старше него
MTIME_SLACK = 1.0
# Спецсимволы выражения фильтра VSTest, которые экранируются обратной косой
FILTER_SPECIAL = set("\\()&|=!~")

# Число шардов по нагрузке машины (`-n auto`, `--split проект=auto`). Те же константы и то же правило —
# в backend/ClaudeHomeServer.TestRuns/TestShards.cs (AdaptiveShards); табличные тесты обеих сторон идут
# по одним строкам, правка порога — в обоих местах сразу
# Потолок по умолчанию (TestRuns:MonolithShards). 4, а не 6: пик RSS при N=6 не замерен (02.11 slice был
# занят на 22 из 24 ГиБ при load1 71 — замер не влезал), поднимать после замера
ADAPTIVE_MAX = 4
ADAPTIVE_FALLBACK = 3       # нет /proc или он не прочитался — прежнее фиксированное значение
ADAPTIVE_IDLE_LOAD = 0.5    # load1 < 0,5·nproc — машина свободна: потолок
ADAPTIVE_BUSY_LOAD = 1.5    # load1 < 1,5·nproc — 3, иначе 2: больше процессов CPU уже не даст
ADAPTIVE_MID = 3
ADAPTIVE_MIN = 2
GIB = 1024 ** 3
# Память на шард: пик RSS на шард прошлого прогона (SHARD_PEAK_FILE в --results) × 1,2, но не ниже 1 ГиБ
# (оборванный на старте прогон не должен сойти за лёгкий); нет записи — запасное 3,5 ГиБ (замер 2026-10-29:
# N=3 дал пик 9,5 ГиБ, ≈3,2 на шард, запас уже внутри — множитель к нему не применяется).
# N ≤ floor(запас / память на шард), не меньше 1; запас — минимум MemAvailable и memory.max − memory.current
# по цепочке своего cgroup (scope и ccs-agents.slice): хост бывает свободен при почти полном slice
ADAPTIVE_SHARD_MEM = int(3.5 * GIB)
ADAPTIVE_PEAK_MARGIN = 1.2
ADAPTIVE_PEAK_FLOOR = 1 * GIB
# Пик на шард прошлого прогона: пишется, только когда процессов было ≥ 2 (один процесс несёт весь монолит,
# и его пик как «на шард» навсегда прижал бы N к 1)
SHARD_PEAK_FILE = "shard-peak.json"
# Ядер на шард: N ≤ max(1, floor(nproc / 4)). У каждого шарда свой testhost с xunit maxParallelThreads=6;
# меньше ~4 ядер на шард — процессы делят одни ядра и только мешают друг другу (24 потока — 6, 8 — 2, 4 — 1)
ADAPTIVE_CORES_PER_SHARD = 4
# Переменная окружения проверки предгейта: «проект=потолок,…» — проекты --split, которым N выбирать по
# нагрузке. Сам --split держит число (3): его понимает и скрипт старых деревьев, который переменную не видит
ADAPTIVE_ENV = "CCS_TEST_SHARDS_ADAPTIVE"


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
# Локальные режимы (--run, --run-fast): все шарды и десятки testhost'ов rest делят одну машину с чужой нагрузкой,
# CPU забит, и стена шарда следует за суммой его длительностей, а не за «сумма / 5». Замер R5 (30.10, load1 ≈ 20):
# при 5 в шард Llm.Tests-2 легло 39 с суммы против 18 с в соседнем, и он держал стену 73–105 с.
LOCAL_PARALLEL = 3.0


def pass_cost(total, longest):
    """Оценка стены прохода: класс серийный и держит поток целиком, остальное делится на потоки."""
    return max(longest, total / PARALLEL)


def layout(classes, n, timings=None, fast=None):
    """Список из n списков классов. timings — {класс: секунды} из профиля или None.

    fast — {класс: секунды в проходе Fast} из профиля. Есть — класс кладётся в шард, где РЕЗУЛЬТАТ
    (стена Fast + стена остального прохода, каждая — max(самый долгий класс, сумма / PARALLEL)) меньше:
    серийный класс вроде членов семейства ClaudeSessionPromptSections* (после разложения R4 это четыре класса по 7,7–9,2 с, сумма
    семейства 34,8 с по TRX 20261030-041127-44e824, максимум семейства 9,15 с; максимум по ВСЕМ серийным классам Fast ≤ ~12,8 с по QA-3) не делает шард медленным,
    пока рядом лежит лишь то, что в его проход укладывается бесплатно. Сумма семейства — не время одного класса: серийность
    переехала в каждый из четырёх, и считается она по классу. Нет — жадная упаковка по сумме секунд."""
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


def median(values):
    """Нижняя медиана: при чётном числе значений — меньшее из двух средних. Среднее двух не отсекло бы
    выброс при двух профилях (6 с и 78 с дали бы 42 с), нижняя берёт спокойный прогон."""
    v = sorted(values)
    return v[(len(v) - 1) // 2]


def split_profiles(path):
    return [p for p in (path or "").split(",") if p]


def load_profile(path):
    """(секунды по классам, проект по классам, секунды Fast по классам) из JSON профиля; нет файла —
    (None, None, None). Профиль без разбивки на проходы даёт fast = None: раскладка тогда по сумме.

    path — один файл или несколько через запятую («run1.json,run2.json,run3.json»): секунды и Fast
    класса — МЕДИАНА (нижняя, см. median) по профилям, где он есть. Один прогон под нагрузкой раздувает вес класса
    в разы (R3: 78 с против 6 с в покое) и ломает раскладку, медиана трёх спокойных прогонов эту помеху отсекает.
    Секунды и Fast класса берутся по ОДНОМУ подмножеству профилей — тем, где есть разбивка Fast: на смешанном
    наборе (часть профилей старого формата без «fast») иначе секунды шли бы по всем файлам, а Fast — по части,
    и класс терял Fast-стоимость или получал Fast из чужого прогона."""
    requested = split_profiles(path)
    files = [p for p in requested if os.path.exists(p)]
    missing = [p for p in requested if p not in files]
    # Пропали ВСЕ из нескольких заказанных — предупреждение молчит: причину и exit 2 называет main (одно сообщение, не два)
    if missing and (files or len(requested) == 1):
        print(f"Предупреждение: профили не найдены: {', '.join(missing)}; "
              f"использовано {len(files)} из {len(requested)}", file=sys.stderr)
    if not files:
        return None, None, None
    rows, projects = {}, {}
    for p in files:
        with open(p, encoding="utf-8") as f:
            classes = json.load(f)["classes"]
        for c, v in classes.items():
            rows.setdefault(c, []).append((v["seconds"], v.get("fast")))
            if v.get("project"):
                projects.setdefault(c, v["project"])
    seconds, fast = {}, {}
    for c, r in rows.items():
        with_fast = [x for x in r if x[1] is not None]
        seconds[c] = median([x[0] for x in (with_fast or r)])
        if with_fast:
            fast[c] = median([x[1] for x in with_fast])
    return seconds, projects, fast or None


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


def trx_proof(results, name, expected, launched_at):
    """Строгое доказательство, что шард дошёл до конца и зелёный: None — доказано, иначе причина отказа.

    Нужны ВСЕ условия: свежий (не старше запуска шарда) TRX шарда, в нём каждый тест шарда по списку
    --list-tests, и ни одного результата кроме Passed/NotExecuted (пропуск). TRX логгер пишет в конце прогона,
    поэтому убитый посреди шард его либо не оставляет, либо оставляет без части тестов."""
    if not expected:
        return "ожидаемый состав шарда пуст"
    found = []
    for f in os.listdir(results):
        if f.startswith(name + "_") and f.endswith(".trx"):
            path = os.path.join(results, f)
            if os.path.getmtime(path) >= launched_at - MTIME_SLACK:
                found.append(path)
    if not found:
        return "свежего TRX шарда нет"
    try:
        root = ET.parse(max(found, key=os.path.getmtime)).getroot()
    except (ET.ParseError, OSError) as e:
        return f"TRX не читается: {e}"
    seen, bad = set(), 0
    for r in root.findall(".//{*}UnitTestResult"):
        seen.add(test_fqn(r.get("testName", "")))
        if r.get("outcome") not in ("Passed", "NotExecuted"):
            bad += 1
    if bad:
        return f"в TRX результатов не Passed/пропуск: {bad}"
    missing = expected - seen
    if missing:
        return f"в TRX нет {len(missing)} из {len(expected)} тестов шарда"
    return None


def trx_report(results, name, launched_at):
    """Сводка шарда из ВСЕХ свежих TRX с префиксом name: {name, total, skipped, failed, passed, seen, assemblies, error}.

    failed — результаты не Passed/NotExecuted (упавшие, ошибки, таймауты), по одному на запись TRX;
    passed и seen — множества FQN с результатом Passed и с любым результатом; assemblies — {FQN: имя сборки}
    по storage определения теста (у rest это единственный способ узнать проект упавшего теста);
    error — причина, по которой TRX не прочитан (total тогда None). Префикс rest-процесса может дать несколько файлов
    (по файлу на тестовый проект решения): числа складываются."""
    paths = [os.path.join(results, f) for f in sorted(os.listdir(results))
             if f.startswith(name + "_") and f.endswith(".trx")
             and os.path.getmtime(os.path.join(results, f)) >= launched_at - MTIME_SLACK]
    report = {"name": name, "total": None, "skipped": 0, "failed": [], "passed": set(), "seen": set(),
              "assemblies": {}, "error": None}
    if not paths:
        report["error"] = "свежего TRX шарда нет"
        return report
    total = 0
    for path in paths:
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError) as e:
            report["error"] = f"TRX не читается: {e}"
            return report
        storage = {d.get("id"): os.path.splitext(os.path.basename(d.get("storage").replace("\\", "/")))[0]
                   for d in root.findall(".//{*}UnitTest") if d.get("id") and d.get("storage")}
        for r in root.findall(".//{*}UnitTestResult"):
            total += 1
            fqn = test_fqn(r.get("testName", ""))
            report["seen"].add(fqn)
            if r.get("testId") in storage:
                report["assemblies"][fqn] = storage[r.get("testId")]
            outcome = r.get("outcome")
            if outcome == "NotExecuted":
                report["skipped"] += 1
            elif outcome == "Passed":
                report["passed"].add(fqn)
            else:
                report["failed"].append(fqn)
    report["total"] = total
    return report


def log_has_abort(log_path):
    """Признак оборванного прогона в логе dotnet test (testhost упал, прогон прерван)."""
    try:
        with open(log_path, encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        return False
    return any(m in text for m in ABORT_MARKERS)


def launch_shards(project, shards, results, prefix, extra_filter, tests):
    """Запускает шарды; возвращает (процессы, доказательства, отчёты): доказательство шарда — вызываемое без
    аргументов, вердикт trx_proof, нужно wait_all только для сигнального кода выхода; отчёт — trx_report шарда
    для сводки (число тестов и имена упавших)."""
    procs, proofs, reports = [], [], []
    for i, s in enumerate(shards):
        expr = f"({shard_filter(s)})" + (f"&({extra_filter})" if extra_filter else "")
        name = f"{prefix}{i + 1}"
        expected = {t for t in tests if any(matches(c, t) for c in s)}
        launched_at = time.time()
        log_path = os.path.join(results, f"{name}.log")
        procs.append(launch([project, "--no-build", "--filter", expr, "--logger", f"trx;LogFilePrefix={name}",
                             "--results-directory", results], log_path))
        proofs.append(lambda n=name, e=expected, t=launched_at: trx_proof(results, n, e, t))
        # target/expected/log — для перезапуска упавших: куда перезапускать и полон ли TRX шарда
        reports.append(lambda n=name, t=launched_at, e=expected, lp=log_path:
                       {**trx_report(results, n, t), "target": project, "expected": e, "log": lp})
    return procs, proofs, reports


# Сколько имён упавших тестов печатать на шард: хвост — счётчиком, чтобы сводка не раздувала карточку
MAX_FAILED_SHOWN = 20


def print_report(wall, codes, reports):
    """Сводка по TRX: число тестов и стена — всегда (и в зелёном), упавшие тесты — по шардам с кодом выхода ≠ 0."""
    if not reports:
        return
    got = [r() if r else None for r in reports]
    total = sum(r["total"] for r in got if r and r["total"] is not None)
    failed = sum(len(r["failed"]) for r in got if r)
    skipped = sum(r["skipped"] for r in got if r)
    blind = [r["name"] for r in got if r and r["total"] is None]
    line = f"Тестов: {total} (упало {failed}, пропущено {skipped}) за стену {wall:.1f} с"
    if blind:
        line += f"; без TRX: {', '.join(blind)}"
    print(line)
    for i, (code, r) in enumerate(zip(codes, got)):
        if code == 0:
            continue
        label = r["name"] if r else f"шард {i + 1}"
        if r is None or r["total"] is None:
            print(f"Упавшие тесты {label} (код {code}): неизвестны — {r['error'] if r else 'отчёта нет'}")
        elif not r["failed"]:
            print(f"Упавшие тесты {label} (код {code}): в TRX провалов нет — прогон оборван, смотри лог шарда")
        else:
            names = list(dict.fromkeys(r["failed"]))
            print(f"Упавшие тесты {label} (код {code}): результатов {len(r['failed'])}, тестов {len(names)}")
            for n in names[:MAX_FAILED_SHOWN]:
                print(f"  {n}")
            if len(names) > MAX_FAILED_SHOWN:
                print(f"  … и ещё {len(names) - MAX_FAILED_SHOWN}")


# Пик RSS дерева последнего wait_all, байты (0 — не мерялся): его пишет save_shard_peak
LAST_PEAK_BYTES = 0


def wait_all(procs, start, proofs=None, reports=None):
    """Ждёт все процессы, меряя стену и пик RSS дерева; печатает итог, возвращает 0, только если все шарды вышли с 0.

    Убитый сигналом шард (OOM-killer, -9) даёт отрицательный код: max() проглотил бы его нулём соседа.
    Единственное исключение — сигнальный код шарда, у которого есть доказательство из proofs (trx_proof):
    хост testhost падал SIGABRT уже после полного зелёного прогона. Положительный код не прощается никогда,
    шард без доказательства (rest-процесс, нет TRX, неполный TRX) остаётся красным.

    reports — отчёты шардов (trx_report): по ним сводка печатает число тестов всегда, а по каждому шарду с
    ненулевым кодом выхода — имена упавших тестов (до MAX_FAILED_SHOWN)."""
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
    global LAST_PEAK_BYTES
    LAST_PEAK_BYTES = peak[0] * 1024
    print_report(wall, codes, reports)
    failed = []
    for i, c in enumerate(codes):
        verdict, reason = shard_verdict(c, proofs[i] if proofs and i < len(proofs) else None)
        if verdict == "green":
            continue
        if verdict == "forgiven":
            print(f"Шард {i + 1} аварийно завершился после зелёных тестов (код {c}): TRX полный, провалов нет — засчитан зелёным",
                  file=sys.stderr)
            continue
        if reason is not None:
            print(f"Шард {i + 1}: код {c}, доказательства зелёности нет ({reason}) — красный", file=sys.stderr)
        failed.append(c)
    return max((c for c in failed if c > 0), default=1) if failed else 0


def shard_verdict(code, proof):
    """Правило wait_all для одного шарда: ("green" | "forgiven" | "red", причина отказа доказательства или None).

    forgiven — сигнальный код при доказанном trx_proof полном зелёном TRX; положительный код не прощается никогда."""
    if code == 0:
        return "green", None
    if code < 0 and proof is not None:
        reason = proof()
        return ("forgiven", None) if reason is None else ("red", reason)
    return "red", None


# --- Перезапуск упавших (только локальные --run и --run-fast) ---

# Больше упавших — это поломка, а не флак: перезапуска нет, честный красный
RERUN_MAX = 20
# CCS_TEST_SHARDS_RERUN=0 выключает перезапуск так же, как --no-rerun
RERUN_ENV = "CCS_TEST_SHARDS_RERUN"
# Потолок длины фильтра одного запуска, символы. Предел Windows — 32 767 на ВСЮ командную строку (Linux — 128 КиБ
# на аргумент); 8000 оставляет запас под путь проекта и опции, длинный список режется на несколько запусков
RERUN_FILTER_CHARS = 8000
# Машиночитаемый список флаков последнего прогона — в корне --results; его читает оркестратор, заводя карточки
FLAKY_FILE = "flaky.json"
# Подпапка TRX и логов перезапуска в корне --results: не в подпапке прогона, иначе calibrate (test-profile.py
# обходит каталог рекурсивно) посчитал бы классы перезапуска вторым проходом
RERUN_DIR = "rerun"
# Обрыв прогона в логе dotnet test: упавший testhost. Такой шард перезапуском не лечится — неизвестно, что не успело пройти
ABORT_MARKERS = ("Test Run Aborted", "The active test run was aborted")


def fqn_filter(fqns):
    """Фильтр VSTest по точным FQN: «FullyQualifiedName=A|FullyQualifiedName=B»."""
    return "|".join(f"FullyQualifiedName={escape(f)}" for f in fqns)


def rerun_chunks(fqns, limit=RERUN_FILTER_CHARS):
    """Куски fqns, у каждого fqn_filter не длиннее limit (имя длиннее limit — отдельным куском).
    Объединение кусков — ровно fqns, порядок сохраняется."""
    out, cur = [], []
    for fqn in fqns:
        if cur and len(fqn_filter(cur + [fqn])) > limit:
            out.append(cur)
            cur = []
        cur.append(fqn)
    if cur:
        out.append(cur)
    return out


def rerun_blocker(code, report):
    """Почему красный шард НЕ лечится перезапуском упавших (None — лечится): перезапуск повторяет только упавшие
    по TRX, поэтому шард, где неизвестно, что вообще прошло, остаётся красным — аборт не маскируется."""
    if code is None:
        return "код выхода неизвестен"
    if code < 0:
        return f"убит сигналом (код {code})"
    if report is None or report.get("total") is None:
        return report.get("error") if report else "отчёта нет"
    if not report["failed"]:
        return "в TRX провалов нет — прогон оборван"
    expected = report.get("expected")
    if expected and not expected <= report["seen"]:
        return f"TRX неполный: нет {len(expected - report['seen'])} из {len(expected)} тестов шарда"
    if report.get("log") and log_has_abort(report["log"]):
        return "в логе обрыв прогона (testhost упал)"
    return None


def resolve_target(fqn, report, targets):
    """Что перезапускать для упавшего теста: проект его сборки по TRX, иначе проект шарда, иначе проект с самым
    длинным именем-префиксом FQN. None — проект не найден (тест остаётся красным)."""
    assembly = report.get("assemblies", {}).get(fqn)
    # VSTest пишет storage в нижнем регистре («claudehomeserver.memory.tests.dll»): имя сверяется без регистра
    by_lower = {n.lower(): n for n in targets}
    if assembly and assembly.lower() in by_lower:
        name = by_lower[assembly.lower()]
        return targets[name], name
    if report.get("target"):
        return report["target"], assembly or target_name(report["target"])
    owner = next((n for n in sorted(targets, key=len, reverse=True) if fqn.startswith(n + ".")), None)
    return (targets[owner], owner) if owner else (None, assembly)


PROJECT_EXTS = {".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".dll"}


def target_name(path):
    """Имя проекта по пути: каталог — его имя, файл (.csproj/.slnx/.dll) — имя без расширения."""
    base = os.path.basename(path.replace("\\", "/").rstrip("/"))
    # «ClaudeHomeServer.Tests» — каталог: точка в имени проекта не расширение
    return project_name(base) if os.path.splitext(base)[1].lower() in PROJECT_EXTS else base


def rerun_failed(code, procs, proofs, reports, results_root, targets, rerun_max):
    """Один ПОСЛЕДОВАТЕЛЬНЫЙ перезапуск упавших тестов после красного wait_all; возвращает итоговый код.

    Перезапуск только если КАЖДЫЙ красный шард красен упавшими тестами в полном TRX (rerun_blocker) и упавших
    не больше rerun_max. Прошли со второго раза все — 0 и блок «Флаки» (они же — в FLAKY_FILE); хоть один упал
    снова или не нашёлся — прежний код и «Упали дважды»."""
    failed = {}  # FQN -> (проект, сборка)
    for i, (p, _) in enumerate(procs):
        c = getattr(p, "returncode", None)
        verdict, _ = shard_verdict(c, proofs[i] if proofs and i < len(proofs) else None) if c is not None else ("red", None)
        if verdict != "red":
            continue
        report = reports[i]() if reports and i < len(reports) and reports[i] else None
        label = report["name"] if report else f"шард {i + 1}"
        why = rerun_blocker(c, report)
        if why:
            print(f"Перезапуск пропущен: {label} — {why}; перезапуск не маскирует обрыв")
            return code
        for fqn in dict.fromkeys(report["failed"]):
            failed.setdefault(fqn, resolve_target(fqn, report, targets))
    if not failed:
        return code
    if len(failed) > rerun_max:
        print(f"Перезапуск пропущен: упало {len(failed)} > RERUN_MAX ({rerun_max}) — это поломка, не флак")
        return code
    rerun_dir = os.path.join(results_root, RERUN_DIR)
    shutil.rmtree(rerun_dir, ignore_errors=True)
    os.makedirs(rerun_dir, exist_ok=True)
    print(f"Перезапуск упавших: {len(failed)} (по одному разу, последовательно)", flush=True)
    passed, when = set(), {}
    groups = {}
    for fqn, (target, _) in failed.items():
        if target is not None:
            groups.setdefault(target, []).append(fqn)
    start = time.monotonic()
    k = 0
    for target in sorted(groups):
        for chunk in rerun_chunks(groups[target]):
            k += 1
            name = f"rerun-{k}"
            launched_at = time.time()
            proc, log = launch([target, "--no-build", "--filter", fqn_filter(chunk), "--logger",
                                f"trx;LogFilePrefix={name}", "--results-directory", rerun_dir],
                               os.path.join(rerun_dir, f"{name}.log"))
            rc = proc.wait()
            log.close()
            report = trx_report(rerun_dir, name, launched_at)
            ok = [f for f in chunk if f in report["passed"] and f not in report["failed"]]
            # Положительный код верим TRX, только если его объясняет упавший тест запуска (соседи по куску тогда
            # честно прошли); необъяснённый положительный — красные все. Сигнальный — только если прошли все
            if rc == 0 or (rc > 0 and report["failed"]) or (rc < 0 and len(ok) == len(chunk)):
                stamp = time.strftime("%Y-%m-%dT%H:%M:%S%z")
                for f in ok:
                    passed.add(f)
                    when[f] = stamp
    flaky = [f for f in failed if f in passed]
    twice = [f for f in failed if f not in passed]
    print(f"Перезапуск: стена {time.monotonic() - start:.1f} с, запусков {k}; TRX и логи — {rerun_dir}")
    if flaky:
        print(f"Флаки (прошли со второго раза): {len(flaky)}")
        for f in flaky:
            print(f"  {f}")
    if twice:
        print(f"Упали дважды: {len(twice)}")
        for f in twice:
            print(f"  {f}" + ("" if failed[f][0] else " (проект не найден — не перезапускался)"))
    write_flaky(results_root, [{"fqn": f, "class": class_of(f), "assembly": failed[f][1], "time": when[f]} for f in flaky],
                twice)
    return 0 if not twice else (code or 1)


def write_flaky(results_root, flaky, twice):
    """FLAKY_FILE: флаки прогона (FQN, класс, сборка, время) и упавшие дважды; флаки же дописываются в журнал
    FLAKY_JOURNAL. Сбой записи не роняет прогон."""
    try:
        with open(os.path.join(results_root, FLAKY_FILE), "w", encoding="utf-8") as f:
            json.dump({"time": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "flaky": flaky, "failed_twice": twice},
                      f, ensure_ascii=False, indent=2)
    except OSError as e:
        print(f"{FLAKY_FILE} не записан: {e}", file=sys.stderr)
    append_flaky_journal(results_root, flaky)


# Журнал флаков в корне --results: строка JSON на флак, копится между прогонами (FLAKY_FILE перезаписывается) —
# источник для карточек дефектов. Держим последние FLAKY_JOURNAL_MAX строк
FLAKY_JOURNAL = "flaky.jsonl"
FLAKY_JOURNAL_MAX = 2000


def git_head():
    """(ветка, коммит) дерева скрипта; None на месте того, что git не сказал."""
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    def ask(*args):
        try:
            done = subprocess.run(["git", "-C", repo, "rev-parse", *args], capture_output=True, text=True, timeout=10)
            return (done.stdout.strip() or None) if done.returncode == 0 else None
        except (OSError, subprocess.SubprocessError):
            return None
    return ask("--abbrev-ref", "HEAD"), ask("HEAD")


def append_flaky_journal(results_root, flaky, limit=FLAKY_JOURNAL_MAX):
    """Дописывает флаки в FLAKY_JOURNAL (время UTC, FQN, класс, сборка, ветка, коммит) и обрезает журнал до
    последних limit строк. Сбой не роняет прогон."""
    if not flaky:
        return
    path = os.path.join(results_root, FLAKY_JOURNAL)
    try:
        branch, commit = git_head()
        stamp = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        rows = [json.dumps({"time": stamp, "fqn": e["fqn"], "class": e["class"], "assembly": e["assembly"],
                            "branch": branch, "commit": commit}, ensure_ascii=False) + "\n" for e in flaky]
        old = []
        if os.path.exists(path):
            with open(path, encoding="utf-8") as f:
                old = [line if line.endswith("\n") else line + "\n" for line in f if line.strip()]
        lines = (old + rows)[-limit:]
        if len(old) + len(rows) <= limit:
            with open(path, "a", encoding="utf-8") as f:
                f.writelines(rows)
        else:
            tmp = path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                f.writelines(lines)
            os.replace(tmp, path)
    except OSError as e:
        print(f"{FLAKY_JOURNAL} не дописан: {e}", file=sys.stderr)


def drop_flaky(results_root):
    """Старый FLAKY_FILE удаляется в начале прогона: оркестратор не должен принять чужие флаки за свежие."""
    try:
        os.remove(os.path.join(results_root, FLAKY_FILE))
    except OSError:
        pass


def cgroup_free(cgroup_file="/proc/self/cgroup", root="/sys/fs/cgroup"):
    """Запас памяти своего cgroup v2: минимум memory.max − memory.current по цепочке от своего каталога до
    корня (scope прогона со своим MemoryMax и ccs-agents.slice над ним — оба лимита). None — cgroup нет,
    везде max или значение неразборчиво: тогда правило смотрит только MemAvailable."""
    try:
        with open(cgroup_file, encoding="ascii") as f:
            path = next((line.strip()[3:] for line in f if line.startswith("0::")), None)
        if not path or not path.startswith("/"):
            return None
        root = os.path.normpath(root)
        node = os.path.normpath(root + path)
        best = None
        while True:
            try:
                with open(os.path.join(node, "memory.max"), encoding="ascii") as f:
                    limit = f.read().strip()
                if limit != "max":
                    with open(os.path.join(node, "memory.current"), encoding="ascii") as f:
                        free = max(0, int(limit) - int(f.read().strip()))
                    best = free if best is None else min(best, free)
            except FileNotFoundError:
                pass
            if node == root or len(node) <= len(root):
                return best
            node = os.path.dirname(node)
    except (OSError, ValueError, StopIteration):
        return None


def machine_snapshot():
    """(nproc, load1, MemAvailable в байтах, запас cgroup или None) из /proc или None: нет /proc (Windows,
    macOS) или не прочитался."""
    try:
        with open("/proc/loadavg", encoding="ascii") as f:
            load1 = float(f.read().split()[0])
        mem = None
        with open("/proc/meminfo", encoding="ascii") as f:
            for line in f:
                if line.startswith("MemAvailable:"):
                    mem = int(line.split()[1]) * 1024
                    break
        cpus = len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else os.cpu_count()
        if mem is None or not cpus:
            return None
        return cpus, load1, mem, cgroup_free()
    except (OSError, ValueError, IndexError):
        return None


def shard_mem(peak_per_shard):
    """Память на шард для правила: прошлый пик × 1,2 (не ниже 1 ГиБ) или запасные 3,5 ГиБ."""
    if peak_per_shard is None:
        return ADAPTIVE_SHARD_MEM
    return int(max(peak_per_shard, ADAPTIVE_PEAK_FLOOR) * ADAPTIVE_PEAK_MARGIN)


def load_shard_peak(results):
    """Пик RSS на шард прошлого прогона из results/SHARD_PEAK_FILE; нет, битый, вне 1 байта..256 ГиБ — None."""
    try:
        with open(os.path.join(results, SHARD_PEAK_FILE), encoding="utf-8") as f:
            value = json.load(f).get("per_shard_bytes")
    except (OSError, ValueError, AttributeError):
        return None
    if isinstance(value, bool) or not isinstance(value, int) or not 0 < value <= 256 * GIB:
        return None
    return value


def save_shard_peak(results, peak_bytes, processes):
    """Записать пик на шард (пик дерева / число процессов), если процессов было ≥ 2. Сбой записи не роняет прогон."""
    if processes < 2 or peak_bytes <= 0:
        return
    try:
        os.makedirs(results, exist_ok=True)
        with open(os.path.join(results, SHARD_PEAK_FILE), "w", encoding="utf-8") as f:
            json.dump({"per_shard_bytes": peak_bytes // processes, "processes": processes, "peak_bytes": peak_bytes}, f)
    except OSError as e:
        print(f"Пик на шард не записан: {e}", file=sys.stderr)


def choose_shards(snapshot, maximum=ADAPTIVE_MAX, peak_per_shard=None):
    """Число шардов и причина по снимку машины. Чистая функция: снимок и прошлый пик — аргументами (табличный тест)."""
    maximum = max(1, maximum)
    if snapshot is None:
        return min(ADAPTIVE_FALLBACK, maximum), "нет /proc — фиксированное значение"
    cpus, load1, mem, cg = snapshot
    if load1 < ADAPTIVE_IDLE_LOAD * cpus:
        n = maximum
    elif load1 < ADAPTIVE_BUSY_LOAD * cpus:
        n = ADAPTIVE_MID
    else:
        n = ADAPTIVE_MIN
    n = min(n, max(1, cpus // ADAPTIVE_CORES_PER_SHARD))
    headroom = mem if cg is None else min(mem, cg)
    n = min(n, max(1, headroom // shard_mem(peak_per_shard)))
    load = f"{load1:.1f}".replace(".", ",")
    where = "" if cg is None else f", в cgroup {cg // GIB} ГиБ"
    return min(n, maximum), f"load1 {load} из {cpus}, свободно {mem // GIB} ГиБ{where}"


def parse_n(value):
    """`-n`: число ≥ 1 или auto."""
    if value == "auto":
        return value
    if not value.isdigit() or int(value) < 1:
        raise argparse.ArgumentTypeError(f"ожидается число ≥ 1 или auto, получено «{value}»")
    return int(value)


def resolve_n(n, label="Шардов", maximum=ADAPTIVE_MAX, results=None):
    """auto — по нагрузке машины с печатью выбора; явное число — как есть. results — где лежит прошлый пик."""
    if n != "auto":
        return n
    n, reason = choose_shards(machine_snapshot(), maximum, load_shard_peak(results) if results else None)
    print(f"{label}: {n} ({reason})", flush=True)
    return n


def adaptive_split(split, env_value):
    """--split с учётом ADAPTIVE_ENV: перечисленные проекты — auto под своим потолком, остальные как есть.
    [(проект, N или "auto", потолок)]. Неразборчивая переменная — предупреждение и --split без изменений:
    предгейт не должен краснеть из-за опечатки в настройке ускорения."""
    ceilings = {}
    if env_value:
        try:
            ceilings = {os.path.normpath(p): n for p, n in parse_split(env_value) if n != "auto"}
        except ValueError as e:
            print(f"{ADAPTIVE_ENV}: {e} — шарды по --split", file=sys.stderr)
    out = []
    for path, n in split:
        ceiling = ceilings.get(os.path.normpath(path))
        out.append((path, "auto", ceiling) if ceiling else (path, n, ADAPTIVE_MAX))
    return out


def run_shards(project, n, results, timings, extra_filter, fast=None, rerun_max=RERUN_MAX):
    """rerun_max — порог перезапуска упавших (None — без перезапуска)."""
    n = resolve_n(n, results=results)
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
    drop_flaky(results)
    start = time.monotonic()
    procs, proofs, reports = launch_shards(project, shards, results, "shard", extra_filter, tests)
    global LAST_PEAK_BYTES
    LAST_PEAK_BYTES = 0
    code = wait_all(procs, start, proofs, reports)
    save_shard_peak(results, LAST_PEAK_BYTES, len(procs))
    if code != 0 and rerun_max is not None:
        code = rerun_failed(code, procs, proofs, reports, results, {target_name(project): project}, rerun_max)
    return code


def parse_split(text):
    """«путь=N,путь=auto» -> [(проект, N или "auto")]; N ≥ 1."""
    out = []
    for item in (x for x in text.split(",") if x):
        path, _, n = item.rpartition("=")
        if not path or not (n == "auto" or n.isdigit() and int(n) >= 1):
            raise ValueError(f"--split: ожидается «проект=N» или «проект=auto», получено «{item}»")
        out.append((path, n if n == "auto" else int(n)))
    return out


# Подпапка прогона run_fast в --results: «метка времени-pid». Хранятся последние RUN_DIRS_KEEP, остальные чистятся
RUN_DIR = re.compile(r"^\d{8}-\d{6}-\d+$")
RUN_DIRS_KEEP = 5


def run_dirs(results):
    """Подпапки прогонов run_fast в results, от старой к новой (по mtime)."""
    found = [os.path.join(results, d) for d in os.listdir(results)
             if RUN_DIR.match(d) and os.path.isdir(os.path.join(results, d))]
    return sorted(found, key=os.path.getmtime)


def new_run_dir(results):
    """Создаёт подпапку прогона и чистит старые: TRX разных прогонов не смешиваются ни в профиле, ни в
    доказательстве шарда (чужой TRX того же префикса из параллельного прогона не покрывает убитый шард)."""
    path = os.path.join(results, f"{time.strftime('%Y%m%d-%H%M%S')}-{os.getpid()}")
    os.makedirs(path, exist_ok=True)
    for old in run_dirs(results)[:-RUN_DIRS_KEEP]:
        shutil.rmtree(old, ignore_errors=True)
    return path


def calibrate(results):
    """Профиль прошлого прогона: раскладка без него идёт по хешу класса и сваливает серийных гигантов в один
    шард. Берётся САМАЯ СВЕЖАЯ подпапка прогона с TRX (прогон пишет в свою — два прогона подряд не смешиваются);
    подпапок нет — плоский каталог как раньше, с окном --latest-run. Недописанная подпапка параллельного прогона
    даёт неполный профиль: классы вне него уходят в шард по хешу. Нет TRX или профиль не прочитался —
    (None, None, None), раскладка по хешу."""
    if not os.path.isdir(results):
        return None, None, None
    source = next((d for d in reversed(run_dirs(results))
                   if any(f.lower().endswith(".trx") for f in os.listdir(d))), results)
    done = subprocess.run([sys.executable, "-I", os.path.join(os.path.dirname(os.path.abspath(__file__)), "test-profile.py"),
                           source, "--json", "--latest-run"], capture_output=True, text=True)
    if done.returncode != 0:
        return None, None, None
    import tempfile
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as f:
        f.write(done.stdout)
    try:
        return load_profile(f.name)
    finally:
        os.remove(f.name)


def run_fast(slnx, split, results, timings, extra_filter, fast=None, rerun_max=RERUN_MAX):
    """Fast предгейта: проекты --split — шардами по классам, остальные тестовые проекты — одним процессом
    на rest-slnx. Каждый тест списка попадает ровно в один шард (иначе exit 1) — самопроверка до запуска.
    rerun_max — порог перезапуска упавших (None — без перезапуска)."""
    start = time.monotonic()
    planned = adaptive_split(split, os.environ.get(ADAPTIVE_ENV))
    split = [(p, resolve_n(n, f"Шардов {os.path.basename(p.rstrip('/'))}", top, results)) for p, n, top in planned]
    # шарды проектов, у которых N выбирается по нагрузке: на них и делится пик дерева
    adaptive_shards = sum(n for (_, n), (_, raw, _) in zip(split, planned) if raw == "auto")
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
    drop_flaky(results)
    peak_dir = results  # пик на шард и flaky.json — в корне --results: их читают следующий прогон и оркестратор
    results = new_run_dir(results)  # дальше всё (TRX, логи, доказательства) — в подпапке этого прогона
    procs, proofs, reports = [], [], []
    rest_path = os.path.join(os.path.dirname(slnx), "ClaudeHomeServer.FastRest.slnx")
    try:
        if rest_projects:
            with open(rest_path, "w", encoding="utf-8") as f:
                f.write(shard_slnx(text, rest_projects, exclude))
            rest_at = time.time()
            rest_log = os.path.join(results, "rest.log")
            procs.append(launch([rest_path, "--no-build"] + (["--filter", extra_filter] if extra_filter else [])
                                + ["--logger", "trx;LogFilePrefix=fast-rest", "--results-directory", results],
                                rest_log))
            proofs.append(None)  # состав rest не листается — доказать полноту нечем, сигнальный код остаётся красным
            # проект упавшего теста rest — по сборке из TRX (target нет): rest-slnx к перезапуску уже удалён
            reports.append(lambda t=rest_at: {**trx_report(results, "fast-rest", t), "target": None,
                                              "expected": None, "log": rest_log})
        for path, shards in layouts:
            # Префикс fast: профиль этих TRX отдаёт секунды прохода Fast, и следующая раскладка
            # держит серийные классы семейства ClaudeSessionPromptSections* (каждый ≤ ~9,2 с, сумма 34,8 с) в шарде с тем, что укладывается в их поток
            ps, pf, pr = launch_shards(path, shards, results, "fast-" + os.path.basename(path.rstrip("/")) + "-",
                                   extra_filter, listings[path])
            procs += ps
            proofs += pf
            reports += pr
        print(f"Подготовка до запуска: {time.monotonic() - start:.1f} с; процессов {len(procs)}")
        run_start = time.monotonic()
        global LAST_PEAK_BYTES
        LAST_PEAK_BYTES = 0
        code = wait_all(procs, run_start, proofs, reports)
        # пик ВСЕГО дерева (с rest и прочими шардами) на число адаптивных шардов: оценка сверху — безопасная
        # сторона; деление на все процессы занизило бы тяжёлый шард монолита лёгкими соседями
        save_shard_peak(peak_dir, LAST_PEAK_BYTES, adaptive_shards)
        if code != 0 and rerun_max is not None:
            targets = {project_name(m.group(1)): os.path.join(os.path.dirname(slnx), m.group(1).replace("\\", "/"))
                       for m in PROJECT_LINE.finditer(text)}
            targets.update({target_name(p): p for p, _ in split})
            code = rerun_failed(code, procs, proofs, reports, peak_dir, targets, rerun_max)
        return code
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
    """Точка входа. PARALLEL — состояние модуля, которое main переопределяет под режим; после выхода оно
    возвращается, иначе второй вызов main() в том же процессе (тесты, встраивание) унаследовал бы чужое."""
    global PARALLEL
    saved = PARALLEL
    try:
        return _main(argv)
    finally:
        PARALLEL = saved


def _main(argv):
    global PARALLEL
    ap = argparse.ArgumentParser(description="Шарды тестов по классам с самопроверкой покрытия")
    ap.add_argument("--list", help="вывод `dotnet test --list-tests` (файл)")
    ap.add_argument("--shard", help="i/N — напечатать фильтр шарда i из N (с 1)")
    ap.add_argument("--slnx", help="решение: шардирование тестовых проектов по профилю (режим rest)")
    ap.add_argument("--exclude", default="", help="проекты через запятую, которых нет в --slnx-шардах")
    ap.add_argument("--verify", action="store_true", help="только самопроверка и сводка")
    ap.add_argument("--run", metavar="PROJECT", help="локально: N параллельных dotnet test --no-build")
    ap.add_argument("--run-fast", metavar="SLNX", help="локально: Fast решения — проекты --split шардами, остальные одним процессом")
    ap.add_argument("--split", default="", help=f"для --run-fast: «проект=N,проект=N»; N по нагрузке — {ADAPTIVE_ENV}=проект=потолок")
    ap.add_argument("-n", type=parse_n, default="auto",
                    help=f"число шардов или auto (по умолчанию): для --run — по нагрузке машины (load1, запас памяти хоста и cgroup), "
                         f"для --verify/--shard/--slnx — {ADAPTIVE_FALLBACK}")
    ap.add_argument("--results", default=os.path.join(".cc-attachments", "test-shards"), help="папка TRX для --run")
    ap.add_argument("--parallel", type=float, help=f"во сколько раз сумма длительностей шарда больше его стены "
                    f"(CI-режимы {PARALLEL:g}, локальные --run/--run-fast {LOCAL_PARALLEL:g})")
    ap.add_argument("--timings", help="JSON профиля (test-profile.py --json), можно несколько через запятую — вес класса тогда медиана; нет файла — предупреждение, раскладка по доступным (все пропали при нескольких — exit 2; единственный — по хешу)")
    ap.add_argument("--filter", dest="extra_filter", help="дополнительный фильтр к каждому шарду (--run)")
    ap.add_argument("--no-rerun", action="store_true",
                    help=f"для --run/--run-fast: не перезапускать упавшие (то же — {RERUN_ENV}=0)")
    ap.add_argument("--rerun-max", type=int, default=RERUN_MAX,
                    help=f"для --run/--run-fast: упало больше — без перезапуска, красный (по умолчанию {RERUN_MAX})")
    args = ap.parse_args(argv)
    if args.rerun_max < 0:
        ap.error("--rerun-max: ожидается число ≥ 0")
    # CI-режимы (--shard, --verify, --slnx) перезапуск не делают вовсе: там флак обязан быть виден
    rerun_max = None if args.no_rerun or os.environ.get(RERUN_ENV, "").strip() == "0" else args.rerun_max
    if args.n == "auto" and not args.run:
        # раскладка CI и самопроверка не должны зависеть от нагрузки машины, где их считают
        args.n = ADAPTIVE_FALLBACK
    if args.parallel is not None:
        if args.parallel <= 0:
            ap.error("--parallel: ожидается число больше нуля")
        PARALLEL = args.parallel
    elif args.run or args.run_fast:
        PARALLEL = LOCAL_PARALLEL
    timings, class_projects, fast = load_profile(args.timings)
    if timings is None and len(split_profiles(args.timings)) > 1:
        # один отсутствующий файл — штатно (холодный кэш CI, раскладка по хешу); несколько заказанных и ни
        # одного найденного — ошибка команды, а не отсутствие профиля
        print(f"Ни один из профилей --timings не найден: {', '.join(split_profiles(args.timings))}", file=sys.stderr)
        return 2

    if args.run_fast:
        try:
            split = parse_split(args.split)
        except ValueError as e:
            ap.error(str(e))
        if not os.path.isfile(args.run_fast):
            # каталог или опечатка раньше читались как файл решения: IsADirectoryError / FileNotFoundError traceback-ом
            print(f"--run-fast ждёт файл решения (.slnx), а не каталог и не несуществующий путь: {args.run_fast}",
                  file=sys.stderr)
            return 2
        return run_fast(args.run_fast, split, args.results, timings, args.extra_filter, fast, rerun_max)
    if args.run:
        return run_shards(args.run, args.n, args.results, timings, args.extra_filter, fast, rerun_max)
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
