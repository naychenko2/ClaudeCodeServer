# coding: utf-8
import contextlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest

_here = os.path.dirname(__file__)
_spec = importlib.util.spec_from_file_location("ts", os.path.join(_here, "test-shards.py"))
ts = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ts)

LIST = """Test run for /x/App.Tests.dll (.NETCoreApp,Version=v10.0)
VSTest version 18.0.2 (x64)

The following Tests are available:
    App.Tests.Services.AlphaTests.Один
    App.Tests.Services.AlphaTests.Два(x: 1, s: "a.b(c)")
    App.Tests.Services.AlphaTests.Два(x: 2, s: "d")
    App.Tests.Services.Alpha.BetaTests.Три
    App.Tests.Controllers.GammaTests.Четыре
    App.Tests.Controllers.GammaTests+Nested.Пять
    App.Tests.Controllers.DeltaTests.Шесть
"""


class TestShardsTests(unittest.TestCase):
    def setUp(self):
        self.tests = ts.parse_list(LIST)

    def test_список_разбирается_с_теориями_и_вложенными_классами(self):
        self.assertEqual(7, len(self.tests))
        self.assertEqual({"App.Tests.Services.AlphaTests": 3, "App.Tests.Services.Alpha.BetaTests": 1,
                          "App.Tests.Controllers.GammaTests": 1, "App.Tests.Controllers.GammaTests+Nested": 1,
                          "App.Tests.Controllers.DeltaTests": 1}, ts.classes_of(self.tests))

    def test_каждый_тест_ровно_в_одном_шарде(self):
        for n in (1, 2, 3, 5):
            self.assertEqual([], ts.verify(self.tests, ts.layout(ts.classes_of(self.tests), n)))

    def test_удалённый_класс_из_раскладки_ловится(self):
        shards = ts.layout(ts.classes_of(self.tests), 3)
        victim = next(s for s in shards if s)
        victim.pop()
        self.assertNotEqual([], ts.verify(self.tests, shards))

    def test_класс_в_двух_шардах_ловится(self):
        shards = ts.layout(ts.classes_of(self.tests), 2)
        shards[0].append(shards[1][0]) if shards[1] else shards[1].append(shards[0][0])
        self.assertTrue(any("шардов 2" in e for e in ts.verify(self.tests, shards)))

    def test_упаковка_по_длительностям_ровняет_нагрузку(self):
        timings = {"App.Tests.Services.AlphaTests": 10, "App.Tests.Services.Alpha.BetaTests": 6,
                   "App.Tests.Controllers.GammaTests": 4, "App.Tests.Controllers.DeltaTests": 1}
        shards = ts.layout(ts.classes_of(self.tests), 2, timings)
        loads = sorted(sum(timings.get(c, 0) for c in s) for s in shards)
        self.assertEqual([10, 11], loads)
        # класс без профиля — по стабильному хешу, а не потерян
        self.assertEqual([], ts.verify(self.tests, shards))

    def test_раскладка_детерминирована(self):
        a = ts.layout(ts.classes_of(self.tests), 3)
        b = ts.layout(dict(reversed(list(ts.classes_of(self.tests).items()))), 3)
        self.assertEqual(a, b)

    def test_фильтр_класса_с_точкой_не_цепляет_соседа_по_префиксу(self):
        self.assertFalse(ts.matches("App.Tests.Services.Alpha", "App.Tests.Services.AlphaTests.Один"))
        self.assertTrue(ts.matches("App.Tests.Services.AlphaTests", "App.Tests.Services.AlphaTests.Один"))

    def test_фильтр_экранирует_спецсимволы(self):
        self.assertEqual("FullyQualifiedName~A.B\\(x\\).|FullyQualifiedName~C.", ts.shard_filter(["A.B(x)", "C"]))

    def _main(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = ts.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_cli_объединение_фильтров_шардов_покрывает_список(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "list.txt")
            with open(path, "w", encoding="utf-8") as f:
                f.write(LIST)
            filters = []
            for i in (1, 2, 3):
                code, out, _ = self._main("--list", path, "--shard", f"{i}/3")
                self.assertEqual(0, code)
                filters.append(out.strip())
            classes = [c[len("FullyQualifiedName~"):-1] for f in filters if f for c in f.split("|")]
            self.assertEqual(sorted(ts.classes_of(self.tests)), sorted(classes))

    def test_cli_сломанная_раскладка_даёт_exit_1(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "list.txt")
            with open(path, "w", encoding="utf-8") as f:
                f.write(LIST)
            original = ts.layout
            ts.layout = lambda classes, n, timings=None, fast=None: [s[1:] for s in original(classes, n, timings, fast)]
            try:
                code, _, err = self._main("--list", path, "--shard", "1/3")
            finally:
                ts.layout = original
            self.assertEqual(1, code)
            self.assertIn("Объединение шардов не равно списку тестов", err)

    def test_cli_timings_из_профиля(self):
        with tempfile.TemporaryDirectory() as tmp:
            path, prof = os.path.join(tmp, "list.txt"), os.path.join(tmp, "p.json")
            with open(path, "w", encoding="utf-8") as f:
                f.write(LIST)
            with open(prof, "w", encoding="utf-8") as f:
                json.dump({"classes": {"App.Tests.Services.AlphaTests": {"tests": 3, "seconds": 9.0}}}, f)
            code, out, _ = self._main("--list", path, "--verify", "-n", "2", "--timings", prof)
            self.assertEqual(0, code)
            self.assertIn("по профилю 9.0 с", out)

    def test_cli_пустой_список_exit_2(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "list.txt")
            with open(path, "w", encoding="utf-8") as f:
                f.write("Build failed")
            self.assertEqual(2, self._main("--list", path, "--verify")[0])


class CostLayoutTests(unittest.TestCase):
    TIMINGS = {"S": 60, "F1": 30, "F2": 30, **{f"H{i}": 40 for i in range(10)}}
    FAST = {"S": 60, "F1": 30, "F2": 30}  # S — серийный гигант Fast; H* — только второй проход

    def _max_cost(self, shards):
        costs = []
        for s in shards:
            fs = [self.FAST.get(c, 0) for c in s]
            rs = [self.TIMINGS[c] - self.FAST.get(c, 0) for c in s]
            costs.append(ts.pass_cost(sum(fs), max(fs, default=0)) + ts.pass_cost(sum(rs), max(rs, default=0)))
        return max(costs)

    def test_серийный_гигант_fast_делит_шард_только_с_тем_что_в_его_проход_укладывается(self):
        classes = {c: 1 for c in self.TIMINGS}
        by_sum = ts.layout(classes, 2, self.TIMINGS)
        by_cost = ts.layout(classes, 2, self.TIMINGS, self.FAST)
        # по сумме гигант делит шард с тяжёлым вторым проходом и тянет шард вверх; по модели — нет
        self.assertLess(self._max_cost(by_cost), self._max_cost(by_sum))
        self.assertEqual(sorted(self.TIMINGS), sorted(c for s in by_cost for c in s))

    def test_без_разбивки_на_проходы_раскладка_по_сумме_как_раньше(self):
        classes = {c: 1 for c in self.TIMINGS}
        self.assertEqual(ts.layout(classes, 3, self.TIMINGS), ts.layout(classes, 3, self.TIMINGS, None))

    def test_раскладка_с_fast_детерминирована_и_не_теряет_классы_без_профиля(self):
        classes = {**{c: 1 for c in self.TIMINGS}, "Unknown": 1}
        a = ts.layout(classes, 3, self.TIMINGS, self.FAST)
        self.assertEqual(a, ts.layout(dict(reversed(list(classes.items()))), 3, self.TIMINGS, self.FAST))
        self.assertIn("Unknown", [c for s in a for c in s])

    def test_load_profile_отдаёт_fast_только_если_он_есть_в_профиле(self):
        with tempfile.TemporaryDirectory() as tmp:
            old, new = os.path.join(tmp, "old.json"), os.path.join(tmp, "new.json")
            with open(old, "w", encoding="utf-8") as f:
                json.dump({"classes": {"A": {"tests": 1, "seconds": 2.0}}}, f)
            with open(new, "w", encoding="utf-8") as f:
                json.dump({"classes": {"A": {"tests": 1, "seconds": 2.0, "fast": 0.5, "project": "P"}}}, f)
            self.assertIsNone(ts.load_profile(old)[2])
            self.assertEqual(({"A": 2.0}, {"A": "P"}, {"A": 0.5}), ts.load_profile(new))

    def test_локальная_модель_PARALLEL_выравнивает_суммы_шардов_вокруг_серийного_гиганта(self):
        timings = {"A": 8.0, **{c: 4.0 for c in "BCDEFGHI"}}
        classes = {c: 1 for c in timings}

        def sums(parallel):
            old, ts.PARALLEL = ts.PARALLEL, parallel
            try:
                return sorted(sum(timings[c] for c in s) for s in ts.layout(classes, 2, timings, dict(timings)))
            finally:
                ts.PARALLEL = old

        ci, local = sums(5.0), sums(ts.LOCAL_PARALLEL)
        self.assertGreater(ci[1] - ci[0], local[1] - local[0])

    def test_load_profile_несколько_профилей_даёт_медиану_и_отсекает_выброс_под_нагрузкой(self):
        with tempfile.TemporaryDirectory() as tmp:
            paths = []
            for i, (sec, fast) in enumerate([(6.0, 5.0), (78.0, 70.0), (7.0, 6.0)]):
                p = os.path.join(tmp, f"r{i}.json")
                with open(p, "w", encoding="utf-8") as f:
                    json.dump({"classes": {"A": {"tests": 1, "seconds": sec, "fast": fast, "project": "P"}}}, f)
                paths.append(p)
            self.assertEqual(({"A": 7.0}, {"A": "P"}, {"A": 6.0}), ts.load_profile(",".join(paths)))
            # чётное число профилей — нижняя медиана: выброс при двух файлах отсекается, а не усредняется
            self.assertEqual(6.0, ts.load_profile(",".join([paths[0], paths[2]]))[0]["A"])
            self.assertEqual(6.0, ts.load_profile(",".join([paths[0], paths[1]]))[0]["A"])
            self.assertEqual(5.0, ts.load_profile(",".join([paths[0], paths[1]]))[2]["A"])

    def test_load_profile_класс_только_в_части_профилей_берётся_по_имеющимся(self):
        with tempfile.TemporaryDirectory() as tmp:
            a, b = os.path.join(tmp, "a.json"), os.path.join(tmp, "b.json")
            with open(a, "w", encoding="utf-8") as f:
                json.dump({"classes": {"A": {"tests": 1, "seconds": 2.0}, "B": {"tests": 1, "seconds": 9.0}}}, f)
            with open(b, "w", encoding="utf-8") as f:
                json.dump({"classes": {"A": {"tests": 1, "seconds": 4.0}}}, f)
            timings, _, fast = ts.load_profile(f"{a},{b}")
            self.assertEqual({"A": 2.0, "B": 9.0}, timings)  # A — нижняя медиана 2 и 4
            self.assertIsNone(fast)

    def test_load_profile_смешанный_набор_секунды_и_fast_по_одним_и_тем_же_профилям(self):
        with tempfile.TemporaryDirectory() as tmp:
            paths = []
            # старый профиль без «fast» (2 с) и два новых: секунды 4 и 8 с, Fast 1 и 3 с
            for i, v in enumerate([{"tests": 1, "seconds": 2.0}, {"tests": 1, "seconds": 4.0, "fast": 1.0},
                                   {"tests": 1, "seconds": 8.0, "fast": 3.0}]):
                p = os.path.join(tmp, f"r{i}.json")
                with open(p, "w", encoding="utf-8") as f:
                    json.dump({"classes": {"A": v}}, f)
                paths.append(p)
            timings, _, fast = ts.load_profile(",".join(paths))
            # секунды — по тем же двум профилям, что и Fast (нижняя медиана 4), а не по всем трём (медиана 4 случайно)
            self.assertEqual({"A": 4.0}, timings)
            self.assertEqual({"A": 1.0}, fast)
            # старый профиль с выбросом не тянет секунды вниз/вверх мимо Fast
            with open(paths[0], "w", encoding="utf-8") as f:
                json.dump({"classes": {"A": {"tests": 1, "seconds": 1.0}}}, f)
            timings, _, fast = ts.load_profile(",".join([paths[0], paths[2]]))
            self.assertEqual(({"A": 8.0}, {"A": 3.0}), (timings, fast))

    def test_load_profile_пропавший_профиль_даёт_предупреждение_с_перечнем_и_числом_использованных(self):
        with tempfile.TemporaryDirectory() as tmp:
            a, b = os.path.join(tmp, "a.json"), os.path.join(tmp, "b.json")
            gone = os.path.join(tmp, "gone.json")
            for p in (a, b):
                with open(p, "w", encoding="utf-8") as f:
                    json.dump({"classes": {"A": {"tests": 1, "seconds": 2.0}}}, f)
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                timings, _, _ = ts.load_profile(f"{a},{gone},{b}")
            self.assertEqual({"A": 2.0}, timings)
            self.assertIn(gone, err.getvalue())
            self.assertIn("использовано 2 из 3", err.getvalue())
            # все на месте — тишина
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                ts.load_profile(f"{a},{b}")
            self.assertEqual("", err.getvalue())

    def _main_cli(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = ts.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_cli_timings_все_заказанные_профили_пропали_exit_2_а_один_пропавший_не_ошибка(self):
        with tempfile.TemporaryDirectory() as tmp:
            lst = os.path.join(tmp, "list.txt")
            with open(lst, "w", encoding="utf-8") as f:
                f.write(LIST)
            g1, g2 = os.path.join(tmp, "g1.json"), os.path.join(tmp, "g2.json")
            code, _, err = self._main_cli("--list", lst, "--verify", "--timings", f"{g1},{g2}")
            self.assertEqual(2, code)
            self.assertIn("Ни один из профилей", err)
            self.assertIn(g1, err)
            self.assertEqual(1, len(err.strip().splitlines()))  # одно сообщение об одной причине, не два
            # холодный кэш CI: единственный профиль отсутствует — предупреждение и раскладка по хешу
            code, _, err = self._main_cli("--list", lst, "--verify", "--timings", g1)
            self.assertEqual(0, code)
            self.assertIn("использовано 0 из 1", err)

    def test_cli_parallel_ноль_и_отрицательный_отказ_без_деления_на_ноль(self):
        with tempfile.TemporaryDirectory() as tmp:
            lst = os.path.join(tmp, "list.txt")
            with open(lst, "w", encoding="utf-8") as f:
                f.write(LIST)
            for bad in ("0", "-1"):
                with self.assertRaises(SystemExit) as ctx:
                    self._main_cli("--list", lst, "--verify", "--parallel", bad)
                self.assertEqual(2, ctx.exception.code)
            self.assertEqual(0, self._main_cli("--list", lst, "--verify", "--parallel", "2")[0])

    def test_main_возвращает_PARALLEL_модуля_после_выхода(self):
        with tempfile.TemporaryDirectory() as tmp:
            lst = os.path.join(tmp, "list.txt")
            with open(lst, "w", encoding="utf-8") as f:
                f.write(LIST)
            ts.PARALLEL = 5.0
            try:
                self._main_cli("--list", lst, "--verify", "--parallel", "2")
                self.assertEqual(5.0, ts.PARALLEL)
            finally:
                ts.PARALLEL = 5.0

    def test_cli_PARALLEL_локальные_режимы_берут_LOCAL_PARALLEL_а_CI_режимы_пятёрку(self):
        seen = {}
        orig = (ts.PARALLEL, ts.run_fast, ts.run_shards)

        def stub(name):
            def run(*a, **k):
                seen.setdefault(name, ts.PARALLEL)  # запоминаем PARALLEL на момент вызова режима
                return 0
            return run
        ts.run_fast = stub("run_fast")
        ts.run_shards = stub("run")
        try:
            with tempfile.TemporaryDirectory() as tmp:
                lst = os.path.join(tmp, "list.txt")
                with open(lst, "w", encoding="utf-8") as f:
                    f.write(LIST)
                slnx = os.path.join(tmp, "x.slnx")
                with open(slnx, "w", encoding="utf-8") as f:
                    f.write("<Solution />")
                for mode, argv in (("run_fast", ["--run-fast", slnx]), ("run", ["--run", "x.csproj"])):
                    ts.PARALLEL = 5.0
                    self._main_cli(*argv)
                    self.assertEqual(ts.LOCAL_PARALLEL, seen[mode], mode)
                ts.PARALLEL = 5.0
                self._main_cli("--list", lst, "--verify")
                self.assertEqual(5.0, ts.PARALLEL)
        finally:
            ts.PARALLEL, ts.run_fast, ts.run_shards = orig


SLNX = """<Solution>
  <Project Path="App/App.csproj" />
  <Project Path="App.Tests/App.Tests.csproj" />
  <Project Path="Alpha.Tests/Alpha.Tests.csproj" />
  <Project Path="Beta.Tests/Beta.Tests.csproj" />
  <Project Path="Gamma.Tests/Gamma.Tests.csproj" />
  <Project Path="Gamma.Tests.ChildFixture/Gamma.Tests.ChildFixture.csproj" />
  <Project Path="TestKit/TestKit.csproj" />
</Solution>
"""


class ProjectShardsTests(unittest.TestCase):
    def test_тестовые_проекты_решения_без_исключённых_и_без_фикстур(self):
        self.assertEqual(["Alpha.Tests", "Beta.Tests", "Gamma.Tests"], ts.slnx_test_projects(SLNX, ["App.Tests"]))

    def test_вес_проекта_берётся_из_профиля_а_не_из_пространства_имён(self):
        timings = {"Some.Namespace.AClass": 7.0, "Alpha.Tests.Own": 1.0, "Other.BClass": 2.0}
        projects = {"Some.Namespace.AClass": "Alpha.Tests", "Other.BClass": "Beta.Tests"}
        w = ts.project_weights(["Alpha.Tests", "Beta.Tests"], timings, projects)
        # Alpha: проект из профиля (7) плюс класс без записи проекта, найденный по префиксу (1)
        self.assertEqual({"Alpha.Tests": 8.0, "Beta.Tests": 2.0}, w)

    def test_каждый_проект_ровно_в_одном_шарде(self):
        projects = ts.slnx_test_projects(SLNX)
        for n in (1, 2, 3, 5):
            self.assertEqual([], ts.verify_projects(projects, ts.project_layout(projects, n)))

    def test_раскладка_проектов_ровняет_вес(self):
        projects = ["Alpha.Tests", "Beta.Tests", "Gamma.Tests"]
        timings = {"a.A": 10.0, "b.B": 6.0, "c.C": 4.0}
        cp = {"a.A": "Alpha.Tests", "b.B": "Beta.Tests", "c.C": "Gamma.Tests"}
        shards = ts.project_layout(projects, 2, timings, cp)
        self.assertEqual([["Alpha.Tests"], ["Beta.Tests", "Gamma.Tests"]], shards)

    def test_потерянный_и_дублированный_проект_ловятся(self):
        projects = ["Alpha.Tests", "Beta.Tests"]
        self.assertTrue(any("шардов 0" in e for e in ts.verify_projects(projects, [["Alpha.Tests"], []])))
        self.assertTrue(any("шардов 2" in e for e in ts.verify_projects(projects, [["Alpha.Tests", "Beta.Tests"], ["Beta.Tests"]])))
        self.assertTrue(any("нет в решении" in e for e in ts.verify_projects(projects, [["Alpha.Tests", "Beta.Tests", "Lost.Tests"]])))

    def test_slnx_шарда_держит_нетестовые_и_только_свои_тестовые(self):
        out = ts.shard_slnx(SLNX, ["Beta.Tests"], ["App.Tests"])
        self.assertIn("App/App.csproj", out)
        self.assertIn("TestKit/TestKit.csproj", out)
        self.assertIn("Gamma.Tests.ChildFixture", out)
        self.assertIn("Beta.Tests/Beta.Tests.csproj", out)
        self.assertNotIn("Alpha.Tests/", out)
        self.assertNotIn("Gamma.Tests/Gamma.Tests.csproj", out)
        self.assertNotIn("App.Tests/", out)

    def _main(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = ts.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_cli_объединение_slnx_шардов_покрывает_все_тестовые_проекты(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "x.slnx")
            with open(path, "w", encoding="utf-8") as f:
                f.write(SLNX)
            seen = []
            for i in (1, 2, 3):
                code, out, _ = self._main("--slnx", path, "--exclude", "App.Tests", "--shard", f"{i}/3")
                self.assertEqual(0, code)
                seen += [ts.project_name(m.group(1)) for m in ts.PROJECT_LINE.finditer(out)
                         if ts.is_test_project(ts.project_name(m.group(1)))]
            self.assertEqual(["Alpha.Tests", "Beta.Tests", "Gamma.Tests"], sorted(seen))

    def test_cli_сломанная_раскладка_проектов_даёт_exit_1(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "x.slnx")
            with open(path, "w", encoding="utf-8") as f:
                f.write(SLNX)
            original = ts.project_layout
            ts.project_layout = lambda projects, n, timings=None, cp=None: [s[1:] for s in original(projects, n, timings, cp)]
            try:
                code, _, err = self._main("--slnx", path, "--shard", "1/3")
            finally:
                ts.project_layout = original
            self.assertEqual(1, code)
            self.assertIn("не равно набору тестовых проектов", err)

    def test_cli_без_тестовых_проектов_exit_2(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "x.slnx")
            with open(path, "w", encoding="utf-8") as f:
                f.write("<Solution><Project Path=\"App/App.csproj\" /></Solution>")
            self.assertEqual(2, self._main("--slnx", path, "--verify")[0])


class WaitAllTests(unittest.TestCase):
    def _codes(self, *scripts):
        with tempfile.TemporaryDirectory() as tmp:
            procs = [(subprocess.Popen([sys.executable, "-c", c]), open(os.path.join(tmp, f"{i}.log"), "w"))
                     for i, c in enumerate(scripts)]
            with contextlib.redirect_stdout(io.StringIO()):
                return ts.wait_all(procs, time.monotonic())

    def test_шард_убитый_сигналом_рядом_с_нулевым_даёт_ненулевой_код(self):
        kill = "import os, signal; os.kill(os.getpid(), signal.SIGKILL)"
        self.assertNotEqual(0, self._codes("pass", kill))

    def test_убитый_сигналом_даёт_ровно_единицу_а_не_сам_отрицательный_код(self):
        # max(failed) без фильтра c > 0 вернул бы -9: ассерт на конкретный код ловит эту мутацию
        kill = "import os, signal; os.kill(os.getpid(), signal.SIGKILL)"
        self.assertEqual(1, self._codes(kill))
        self.assertEqual(1, self._codes("pass", kill))
        self.assertEqual(3, self._codes(kill, "raise SystemExit(3)"))

    def test_все_шарды_нулевые_дают_ноль_а_ненулевой_код_сохраняется(self):
        self.assertEqual(0, self._codes("pass", "pass"))
        self.assertEqual(3, self._codes("pass", "raise SystemExit(3)"))


TRX_NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
ABORT = "import os, signal; os.kill(os.getpid(), signal.SIGABRT)"


def write_trx(directory, name, results, stamp="20261030044024"):
    """results: [(testName, outcome)] -> файл name_net10.0_stamp.trx в формате логгера VSTest."""
    rows = "".join(f'<UnitTestResult testName="{n.replace(chr(34), "&quot;")}" outcome="{o}" />' for n, o in results)
    path = os.path.join(directory, f"{name}_net10.0_{stamp}.trx")
    with open(path, "w", encoding="utf-8-sig") as f:
        f.write(f'<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="{TRX_NS}"><Results>{rows}</Results></TestRun>')
    return path


class AbortAfterGreenTests(unittest.TestCase):
    """Дефект 0f5bd660: testhost падает SIGABRT уже после полного зелёного прогона — но убитый посреди шард красный."""
    EXPECTED = {"App.Tests.A.One", "App.Tests.A.Two", "App.Tests.B.Three"}
    FULL = [("App.Tests.A.One", "Passed"), ('App.Tests.A.Two(x: 1, s: "a(b)")', "Passed"),
            ("App.Tests.A.Two(x: 2, s: \"c\")", "Passed"), ("App.Tests.B.Three", "NotExecuted")]

    def _wait(self, tmp, scripts, proofs):
        procs = [(subprocess.Popen([sys.executable, "-c", c]), open(os.path.join(tmp, f"{i}.log"), "w"))
                 for i, c in enumerate(scripts)]
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = ts.wait_all(procs, time.monotonic(), proofs)
        return code, err.getvalue()

    def _proof(self, tmp, results=None, expected=None, launched_at=0):
        if results is not None:
            write_trx(tmp, "shard1", results)
        return lambda: ts.trx_proof(tmp, "shard1", self.EXPECTED if expected is None else expected, launched_at)

    def test_сигнальный_код_и_полный_зелёный_trx_дают_зелёный_с_предупреждением(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, err = self._wait(tmp, ["pass", ABORT], [None, self._proof(tmp, self.FULL)])
        self.assertEqual(0, code)
        self.assertIn("аварийно завершился после зелёных тестов (код -6)", err)

    def test_сигнальный_код_и_неполный_trx_красный(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, err = self._wait(tmp, [ABORT], [self._proof(tmp, self.FULL[:2])])
        self.assertEqual(1, code)
        self.assertIn("в TRX нет 1 из 3 тестов шарда", err)

    def test_сигнальный_код_без_trx_красный(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, err = self._wait(tmp, [ABORT], [self._proof(tmp)])
        self.assertEqual(1, code)
        self.assertIn("свежего TRX шарда нет", err)

    def test_сигнальный_код_и_упавший_тест_в_trx_красный(self):
        with tempfile.TemporaryDirectory() as tmp:
            bad = [(self.FULL[0][0], "Failed")] + self.FULL[1:]
            code, _ = self._wait(tmp, [ABORT], [self._proof(tmp, bad)])
        self.assertEqual(1, code)

    def test_trx_прошлого_прогона_не_доказательство(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = write_trx(tmp, "shard1", self.FULL)
            os.utime(path, (1, 1))
            code, err = self._wait(tmp, [ABORT], [self._proof(tmp, launched_at=time.time() - 60)])
        self.assertEqual(1, code)
        self.assertIn("свежего TRX шарда нет", err)

    def test_trx_чужого_префикса_не_доказательство(self):
        with tempfile.TemporaryDirectory() as tmp:
            write_trx(tmp, "shard10", self.FULL)  # shard1 не должен цеплять shard10
            write_trx(tmp, "fast-rest", self.FULL)
            self.assertEqual("свежего TRX шарда нет", ts.trx_proof(tmp, "shard1", self.EXPECTED, 0))
            code, err = self._wait(tmp, [ABORT], [lambda: ts.trx_proof(tmp, "shard1", self.EXPECTED, 0)])
        self.assertEqual(1, code)
        self.assertIn("свежего TRX шарда нет", err)

    def test_допуск_зерна_mtime_одна_секунда(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = write_trx(tmp, "shard1", self.FULL)
            now = time.time()
            os.utime(path, (now - 0.5, now - 0.5))  # записан на полсекунды «раньше» запуска — в допуске
            self.assertIsNone(ts.trx_proof(tmp, "shard1", self.EXPECTED, now))
            os.utime(path, (now - 5, now - 5))  # на пять секунд — нет
            self.assertEqual("свежего TRX шарда нет", ts.trx_proof(tmp, "shard1", self.EXPECTED, now))

    def test_положительный_код_не_прощается_даже_при_полном_trx(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, _ = self._wait(tmp, ["raise SystemExit(3)"], [self._proof(tmp, self.FULL)])
        self.assertEqual(3, code)

    def test_шард_без_доказательства_rest_красный_по_сигналу(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, _ = self._wait(tmp, [ABORT, "pass"], [None, None])
        self.assertEqual(1, code)

    def test_доказательство_одного_шарда_не_спасает_соседний_убитый(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, _ = self._wait(tmp, [ABORT, ABORT], [self._proof(tmp, self.FULL), self._proof(tmp, expected=set())])
        self.assertEqual(1, code)

    def test_launch_shards_считает_ожидаемый_состав_по_классам_шарда(self):
        tests = ["App.Tests.A.One", "App.Tests.A.Two", "App.Tests.B.Three", "App.Tests.C.Four"]
        orig = ts.launch
        ts.launch = lambda args, log: (object(), None)
        try:
            with tempfile.TemporaryDirectory() as tmp:
                _, proofs, _ = ts.launch_shards("p", [["App.Tests.A", "App.Tests.B"], ["App.Tests.C"]], tmp, "shard", None, tests)
                write_trx(tmp, "shard1", self.FULL)
                write_trx(tmp, "shard2", [("App.Tests.C.Four", "Passed")])
                self.assertEqual([None, None], [p() for p in proofs])
                os.remove([os.path.join(tmp, f) for f in os.listdir(tmp) if f.startswith("shard2")][0])
                self.assertEqual("свежего TRX шарда нет", proofs[1]())
        finally:
            ts.launch = orig


class ReportTests(unittest.TestCase):
    """Г2: сводка wait_all — число тестов и стена всегда, имена упавших — по шардам с кодом выхода ≠ 0."""
    GREEN = [("App.Tests.A.One", "Passed"), ("App.Tests.A.Two", "Passed"), ("App.Tests.B.Three", "NotExecuted")]

    def _wait(self, tmp, scripts, shards):
        """shards: [(имя TRX-префикса, результаты|None)]; возвращает stdout сводки и код."""
        procs, reports = [], []
        for i, (c, (name, results)) in enumerate(zip(scripts, shards)):
            if results is not None:
                write_trx(tmp, name, results)
            procs.append((subprocess.Popen([sys.executable, "-c", c]), open(os.path.join(tmp, f"{i}.log"), "w")))
            reports.append(lambda n=name: ts.trx_report(tmp, n, 0))
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            code = ts.wait_all(procs, time.monotonic(), [None] * len(procs), reports)
        return code, out.getvalue()

    def test_зелёный_исход_печатает_число_тестов_и_стену(self):
        with tempfile.TemporaryDirectory() as tmp:
            code, out = self._wait(tmp, ["pass", "pass"], [("shard1", self.GREEN), ("shard2", [("App.Tests.C.Four", "Passed")])])
        self.assertEqual(0, code)
        self.assertRegex(out, r"Тестов: 4 \(упало 0, пропущено 1\) за стену \d+\.\d с")
        self.assertNotIn("Упавшие тесты", out)

    def test_красный_шард_печатает_fqn_упавших_а_зелёный_сосед_молчит(self):
        red = [("App.Tests.A.One", "Passed"), ("App.Tests.A.Two", "Failed"), ("App.Tests.B.Three", "Failed")]
        with tempfile.TemporaryDirectory() as tmp:
            code, out = self._wait(tmp, ["raise SystemExit(1)", "pass"], [("shard1", red), ("shard2", self.GREEN)])
        self.assertEqual(1, code)
        self.assertIn("Тестов: 6 (упало 2, пропущено 1)", out)
        self.assertIn("Упавшие тесты shard1 (код 1): результатов 2, тестов 2", out)
        self.assertIn("  App.Tests.A.Two\n", out)
        self.assertIn("  App.Tests.B.Three\n", out)
        self.assertNotIn("App.Tests.A.One", out)  # прошедший тест в сводку не попадает
        self.assertNotIn("shard2 (код", out)

    def test_строки_теории_схлопываются_в_один_fqn(self):
        red = [('App.Tests.A.Two(x: 1, s: "a(b)")', "Failed"), ("App.Tests.A.Two(x: 2, s: \"c\")", "Failed")]
        with tempfile.TemporaryDirectory() as tmp:
            _, out = self._wait(tmp, ["raise SystemExit(1)"], [("shard1", red)])
        self.assertIn("результатов 2, тестов 1", out)
        self.assertEqual(1, out.count("  App.Tests.A.Two\n"))

    def test_упавших_больше_двадцати_хвост_счётчиком(self):
        red = [(f"App.Tests.A.T{i:02}", "Failed") for i in range(25)]
        with tempfile.TemporaryDirectory() as tmp:
            _, out = self._wait(tmp, ["raise SystemExit(1)"], [("shard1", red)])
        self.assertEqual(20, sum(1 for l in out.splitlines() if l.startswith("  App.Tests.A.T")))
        self.assertIn("  App.Tests.A.T19\n", out)
        self.assertNotIn("App.Tests.A.T20", out)
        self.assertIn("… и ещё 5", out)

    def test_красный_шард_без_trx_называет_причину_а_не_молчит(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, out = self._wait(tmp, ["raise SystemExit(2)"], [("shard1", None)])
        self.assertIn("Упавшие тесты shard1 (код 2): неизвестны — свежего TRX шарда нет", out)
        self.assertIn("без TRX: shard1", out)

    def test_красный_код_при_чистом_trx_не_выдаёт_пустой_список_за_зелёный(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, out = self._wait(tmp, ["raise SystemExit(1)"], [("shard1", self.GREEN)])
        self.assertIn("в TRX провалов нет — прогон оборван", out)

    def test_старый_trx_не_считается(self):
        with tempfile.TemporaryDirectory() as tmp:
            os.utime(write_trx(tmp, "shard1", self.GREEN), (1, 1))
            report = ts.trx_report(tmp, "shard1", time.time() - 60)
        self.assertIsNone(report["total"])

    def test_префикс_shard1_не_цепляет_shard10(self):
        with tempfile.TemporaryDirectory() as tmp:
            write_trx(tmp, "shard1", self.GREEN)
            write_trx(tmp, "shard10", [("App.Tests.Z.Bad", "Failed")])
            report = ts.trx_report(tmp, "shard1", 0)
        self.assertEqual((3, []), (report["total"], report["failed"]))

    def test_rest_с_несколькими_trx_складывает_результаты(self):
        with tempfile.TemporaryDirectory() as tmp:
            write_trx(tmp, "fast-rest", self.GREEN, stamp="20261030044024")
            write_trx(tmp, "fast-rest", [("Other.Tests.X.Y", "Failed")], stamp="20261030044025")
            report = ts.trx_report(tmp, "fast-rest", 0)
        self.assertEqual((4, ["Other.Tests.X.Y"]), (report["total"], report["failed"]))

    def test_run_fast_отдаёт_wait_all_отчёты_rest_и_шардов(self):
        seen = {}
        orig = (ts.list_tests, ts.launch, ts.wait_all)
        ts.list_tests = lambda path, flt: ([f"App.Tests.C{i}.T" for i in range(4)], None)

        def fake_launch(args, log):
            # rest-процесс «пишет» TRX в папку прогона, которую ему передал run_fast
            if "trx;LogFilePrefix=fast-rest" in args:
                write_trx(args[args.index("--results-directory") + 1], "fast-rest", [("Other.Tests.X.Y", "Failed")])
            return object(), None
        ts.launch = fake_launch

        def fake_wait(procs, start, proofs=None, reports=None):
            seen["n"], seen["reports"] = len(procs), reports
            return 0
        ts.wait_all = fake_wait
        try:
            with tempfile.TemporaryDirectory() as tmp:
                slnx = os.path.join(tmp, "x.slnx")
                with open(slnx, "w", encoding="utf-8") as f:
                    f.write(RunFastTests.SLNX)
                os.makedirs(os.path.join(tmp, "r"), exist_ok=True)
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    ts.run_fast(slnx, [("b/App.Tests", 2)], os.path.join(tmp, "r"), {}, "F")
                self.assertEqual(seen["n"], len(seen["reports"]))
                self.assertEqual(["Other.Tests.X.Y"], seen["reports"][0]()["failed"])
        finally:
            ts.list_tests, ts.launch, ts.wait_all = orig

    def test_run_shards_отдаёт_wait_all_отчёты_шардов(self):
        seen = {}
        orig = (ts.list_tests, ts.launch, ts.wait_all)
        ts.list_tests = lambda path, flt: ([f"App.Tests.C{i}.T" for i in range(4)], None)
        ts.launch = lambda args, log: (object(), None)

        def fake_wait(procs, start, proofs=None, reports=None):
            seen["n"], seen["reports"] = len(procs), reports
            return 0
        ts.wait_all = fake_wait
        try:
            with tempfile.TemporaryDirectory() as tmp:
                results = os.path.join(tmp, "r")
                os.makedirs(results)
                write_trx(results, "shard1", self.GREEN)
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    code = ts.run_shards("b/App.Tests", 2, results, {}, "F")
                self.assertEqual(0, code)
                self.assertEqual(2, seen["n"])
                self.assertIsNotNone(seen["reports"])
                self.assertEqual(seen["n"], len(seen["reports"]))
                self.assertEqual(3, seen["reports"][0]()["total"])
        finally:
            ts.list_tests, ts.launch, ts.wait_all = orig


class RunFastTests(unittest.TestCase):
    """Fast предгейта: проекты --split шардами, остальные — rest-slnx; процессы подменены."""
    SLNX = """<Solution>
  <Project Path="App/App.csproj" />
  <Project Path="App.Tests/App.Tests.csproj" />
  <Project Path="Llm.Tests/Llm.Tests.csproj" />
  <Project Path="Other.Tests/Other.Tests.csproj" />
</Solution>
"""

    def _run(self, tamper=False):
        launched, rest = [], {}
        orig = (ts.list_tests, ts.launch, ts.wait_all, ts.layout)
        listing = {"b/App.Tests": [f"App.Tests.C{i}.T{j}" for i in range(6) for j in range(2)],
                   "b/Llm.Tests": [f"Llm.Tests.L{i}.T" for i in range(4)]}
        ts.list_tests = lambda path, flt: (listing[path], None)

        def fake_launch(args, log):
            launched.append(args)
            if args[0].endswith("FastRest.slnx"):
                with open(args[0], encoding="utf-8") as f:
                    rest["text"] = f.read()
            return object(), None
        ts.launch = fake_launch
        ts.wait_all = lambda procs, start, proofs=None, reports=None: 0
        if tamper:  # шард теряет класс: самопроверка обязана остановить прогон до запуска
            real = ts.layout
            ts.layout = lambda classes, n, t=None, f=None: [real(classes, n, t, f)[0][1:]] + real(classes, n, t, f)[1:]
        try:
            with tempfile.TemporaryDirectory() as tmp:
                slnx = os.path.join(tmp, "x.slnx")
                with open(slnx, "w", encoding="utf-8") as f:
                    f.write(self.SLNX)
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    code = ts.run_fast(slnx, [("b/App.Tests", 3), ("b/Llm.Tests", 2)], os.path.join(tmp, "r"), {}, "F")
                self.assertFalse(os.path.exists(os.path.join(tmp, "ClaudeHomeServer.FastRest.slnx")))
        finally:
            ts.list_tests, ts.launch, ts.wait_all, ts.layout = orig
        return code, launched, rest

    def test_parse_split(self):
        self.assertEqual([("a/B.Tests", 3), ("c/D.Tests", 2)], ts.parse_split("a/B.Tests=3,c/D.Tests=2"))
        with self.assertRaises(ValueError):
            ts.parse_split("a/B.Tests=0")
        with self.assertRaises(ValueError):
            ts.parse_split("a/B.Tests")

    def test_шарды_покрывают_проекты_а_rest_держит_остальные_тестовые_проекты(self):
        code, launched, rest = self._run()
        self.assertEqual(0, code)
        self.assertEqual(1 + 3 + 2, len(launched))  # rest, монолит 3 шарда, Llm 2 шарда
        self.assertIn("Other.Tests", rest["text"])
        self.assertNotIn("App.Tests", rest["text"])
        self.assertNotIn("Llm.Tests", rest["text"])
        self.assertIn("App/App.csproj", rest["text"])

    def test_run_fast_каталог_вместо_slnx_отказ_exit_2_без_traceback(self):
        orig = ts.run_fast
        ts.run_fast = lambda *a, **k: self.fail("run_fast не должен запускаться")
        try:
            with tempfile.TemporaryDirectory() as tmp:
                for bad in (tmp, os.path.join(tmp, "нет.slnx")):
                    out, err = io.StringIO(), io.StringIO()
                    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                        code = ts.main(["--run-fast", bad, "--split", "a/B.Tests=2"])
                    self.assertEqual(2, code)
                    self.assertIn("файл решения", err.getvalue())
                    self.assertIn(bad, err.getvalue())
        finally:
            ts.run_fast = orig

    def test_префикс_trx_fast_и_фильтр_fast_уходят_и_в_шарды_и_в_rest(self):
        # на префиксе «fast» держится fast-профиль test-profile.py; фильтр Fast обязан дойти до каждого процесса
        code, launched, _ = self._run()
        self.assertEqual(0, code)
        self.assertEqual(6, len(launched))
        for args in launched:
            prefix = next(a for a in args if a.startswith("trx;LogFilePrefix="))
            self.assertTrue(prefix[len("trx;LogFilePrefix="):].startswith("fast"), prefix)
            flt = args[args.index("--filter") + 1]
            self.assertTrue(flt == "F" or flt.endswith("&(F)"), flt)  # rest — фильтр как есть, шард — «(классы)&(F)»

    def test_каждый_прогон_пишет_в_свою_подпапку_а_старые_чистятся(self):
        with tempfile.TemporaryDirectory() as tmp:
            results = os.path.join(tmp, "r")
            os.makedirs(results)
            stamps = [f"2026110{d}-000000-{d}" for d in range(1, 8)]  # 7 старых прогонов
            for i, st in enumerate(stamps):
                path = os.path.join(results, st)
                os.makedirs(path)
                os.utime(path, (1000 + i, 1000 + i))
            os.makedirs(os.path.join(results, "чужая-папка"))  # не прогон — чистка её не трогает
            run = ts.new_run_dir(results)
            self.assertTrue(os.path.isdir(run))
            self.assertEqual(results, os.path.dirname(run))
            left = sorted(d for d in os.listdir(results) if ts.RUN_DIR.match(d))
            self.assertEqual(ts.RUN_DIRS_KEEP, len(left))
            self.assertIn(os.path.basename(run), left)
            self.assertNotIn(stamps[0], left)  # самый старый ушёл
            self.assertIn("чужая-папка", os.listdir(results))

    def test_calibrate_берёт_самую_свежую_подпапку_с_trx_а_не_смесь_прогонов(self):
        with tempfile.TemporaryDirectory() as tmp:
            old, new, empty = (os.path.join(tmp, d) for d in ("20261101-000000-1", "20261102-000000-2", "20261103-000000-3"))
            for i, d in enumerate((old, new, empty)):
                os.makedirs(d)
            write_trx(old, "fast-x-1", self.GREEN_ONE)
            write_trx(new, "fast-x-1", self.GREEN_ONE)
            for d, t in ((old, 1000), (new, 2000), (empty, 3000)):  # пустая — самая свежая, но без TRX
                os.utime(d, (t, t))
            seen = []
            orig = ts.subprocess.run
            ts.subprocess.run = lambda cmd, **k: seen.append(cmd[3]) or type("R", (), {"returncode": 1, "stdout": ""})()
            try:
                ts.calibrate(tmp)
                self.assertEqual([new], seen)
                # подпапок нет вовсе — плоский каталог, как раньше
                flat = os.path.join(tmp, "flat")
                os.makedirs(flat)
                seen.clear()
                ts.calibrate(flat)
                self.assertEqual([flat], seen)
            finally:
                ts.subprocess.run = orig

    GREEN_ONE = [("App.Tests.A.One", "Passed")]

    def test_run_fast_пишет_логи_и_trx_в_подпапку_прогона(self):
        orig = (ts.list_tests, ts.launch, ts.wait_all)
        ts.list_tests = lambda path, flt: ([f"App.Tests.C{i}.T" for i in range(4)], None)
        paths = []
        ts.launch = lambda args, log: paths.append((args[args.index("--results-directory") + 1], log)) or (object(), None)
        ts.wait_all = lambda procs, start, proofs=None, reports=None: 0
        try:
            with tempfile.TemporaryDirectory() as tmp:
                slnx = os.path.join(tmp, "x.slnx")
                with open(slnx, "w", encoding="utf-8") as f:
                    f.write(self.SLNX)
                results = os.path.join(tmp, "r")
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    ts.run_fast(slnx, [("b/App.Tests", 2)], results, {}, "F")
        finally:
            ts.list_tests, ts.launch, ts.wait_all = orig
        self.assertTrue(paths)
        dirs = {d for d, _ in paths}
        self.assertEqual(1, len(dirs))
        (run,) = dirs
        self.assertEqual(results, os.path.dirname(run))
        self.assertTrue(ts.RUN_DIR.match(os.path.basename(run)))
        self.assertTrue(all(os.path.dirname(log) == run for _, log in paths))

    def test_шард_потерял_класс_самопроверка_краснеет_до_запуска(self):
        code, launched, _ = self._run(tamper=True)
        self.assertEqual(1, code)
        self.assertEqual([], launched)


if __name__ == "__main__":
    unittest.main()
