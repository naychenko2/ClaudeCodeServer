# coding: utf-8
import contextlib
import importlib.util
import io
import json
import os
import shutil
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

    def test_пик_делится_на_шарды_адаптивного_проекта_а_не_на_все_процессы(self):
        orig = (ts.list_tests, ts.launch, ts.wait_all, ts.machine_snapshot)
        listing = {"b/App.Tests": [f"App.Tests.C{i}.T{j}" for i in range(6) for j in range(2)],
                   "b/Llm.Tests": [f"Llm.Tests.L{i}.T" for i in range(4)]}
        ts.list_tests = lambda path, flt: (listing[path], None)
        ts.launch = lambda args, log: (object(), None)
        ts.machine_snapshot = lambda: (24, 2.0, 31 * ts.GIB, None)  # 24 потока в тишине — N = потолок 3

        def fake_wait(procs, start, proofs=None, reports=None):
            ts.LAST_PEAK_BYTES = 12 * ts.GIB  # дерево целиком: 3 шарда App, 2 шарда Llm, rest — 6 процессов
            return 0

        ts.wait_all = fake_wait
        os.environ[ts.ADAPTIVE_ENV] = "b/App.Tests=3"
        try:
            with tempfile.TemporaryDirectory() as tmp:
                slnx = os.path.join(tmp, "x.slnx")
                with open(slnx, "w", encoding="utf-8") as f:
                    f.write(self.SLNX)
                results = os.path.join(tmp, "r")
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(0, ts.run_fast(slnx, [("b/App.Tests", 3), ("b/Llm.Tests", 2)], results, {}, "F"))
                self.assertEqual(4 * ts.GIB, ts.load_shard_peak(results), "12 ГиБ / 3 шарда App, а не / 6 процессов")
        finally:
            ts.list_tests, ts.launch, ts.wait_all, ts.machine_snapshot = orig
            del os.environ[ts.ADAPTIVE_ENV]

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


# Те же строки — в backend/ClaudeHomeServer.TestRuns.Tests/Services/TestShardsTests.cs (AdaptiveRows):
# (nproc, load1, MemAvailable ГиБ или None — снимка нет, запас cgroup ГиБ или None — cgroup нет,
#  пик на шард прошлого прогона ГиБ или None — записи нет, потолок, ожидаемое N)
ADAPTIVE_ROWS = [
    (24, 7.2, 31, None, None, 6, 6),      # тихо — потолок
    (24, 11.99, 31, None, None, 6, 6),    # под границей 0,5·nproc
    (24, 12.0, 31, None, None, 6, 3),     # ровно 0,5·nproc — уже не потолок (мутация порога 0,5→0,6 краснит здесь)
    (24, 14.0, 31, None, None, 6, 3),
    (24, 35.99, 31, None, None, 6, 3),    # под границей 1,5·nproc
    (24, 36.0, 31, None, None, 6, 2),     # ровно 1,5·nproc
    (24, 50.0, 31, None, None, 6, 2),
    (24, 2.0, 14.0, None, None, 6, 4),    # 14 / 3,5 = 4 ровно (мутация 3,5→3 краснит строку ниже)
    (24, 2.0, 13.99, None, None, 6, 3),
    (24, 2.0, 7.0, None, None, 6, 2),
    (24, 2.0, 6.99, None, None, 6, 1),
    (24, 2.0, 0.5, None, None, 6, 1),     # не меньше 1
    (24, 2.0, 28, 7.0, None, 6, 2),       # хост свободен, slice почти полон (02.11: 16,9 из 24) — N мал
    (24, 2.0, 28, 1.5, None, 6, 1),
    (24, 2.0, 28, 0, None, 6, 1),         # slice полон — всё равно 1, а не 0
    (24, 2.0, 7.0, 20, None, 6, 2),       # cgroup свободен, хост нет — минимум двух
    (24, 2.0, 31, 24, None, 6, 6),        # 24 / 3,5 = 6,86
    (24, 50.0, 31, 24, None, 6, 2),       # нагрузка сильнее памяти
    (24, 2.0, 31, 10, 2, 6, 4),           # прошлый пик 2 ГиБ на шард × 1,2 = 2,4; 10 / 2,4 = 4,2 (без 1,2 — 5)
    (24, 2.0, 31, 10, 5, 6, 1),           # тяжёлый прошлый прогон: 5 × 1,2 = 6 на шард
    (24, 2.0, 31, 3, 0.1, 6, 2),          # пик подозрительно мал — не ниже 1 ГиБ: 3 / 1,2 = 2,5
    (24, 2.0, 31, None, None, 4, 4),      # потолок из настроек
    (24, 2.0, 31, None, None, 1, 1),
    (24, 50.0, 31, None, None, 1, 1),     # потолок ниже правила
    (4, 0.5, 31, None, None, 6, 1),       # 4 потока в тишине — один процесс
    (8, 1.0, 31, None, None, 6, 2),       # 8 потоков в тишине — 2
    (12, 1.0, 31, None, None, 6, 3),      # 12 / 4 = 3 (мутация делителя ядер 4→3 краснит здесь: 4)
    (8, 3.9, 31, None, None, 6, 2),       # тихо по load, но ядер на два шарда
    (8, 4.0, 31, None, None, 6, 2),
    (64, 2.0, 31, None, None, 6, 6),      # 64 потока в тишине — потолок из настроек, не больше
    (64, 2.0, 31, None, None, 4, 4),
    (None, None, None, None, None, 6, 3),  # нет /proc — прежнее фиксированное
    (None, None, None, None, None, 2, 2),
]


class AdaptiveShardsTests(unittest.TestCase):
    def test_таблица_правила_общая_с_csharp(self):
        for cpus, load1, mem, cg, peak, maximum, expected in ADAPTIVE_ROWS:
            snapshot = None if cpus is None else (
                cpus, load1, int(mem * ts.GIB), None if cg is None else int(cg * ts.GIB))
            per_shard = None if peak is None else int(peak * ts.GIB)
            with self.subTest(cpus=cpus, load1=load1, mem=mem, cg=cg, peak=peak, maximum=maximum):
                self.assertEqual(expected, ts.choose_shards(snapshot, maximum, per_shard)[0])

    def test_причина_для_сводки(self):
        self.assertEqual((6, "load1 7,2 из 24, свободно 31 ГиБ"), ts.choose_shards((24, 7.2, 31 * ts.GIB + 5, None), 6))
        self.assertEqual((2, "load1 7,2 из 24, свободно 28 ГиБ, в cgroup 7 ГиБ"),
                         ts.choose_shards((24, 7.2, 28 * ts.GIB, 7 * ts.GIB + 5), 6))
        self.assertEqual((3, "нет /proc — фиксированное значение"), ts.choose_shards(None, 6))

    def test_снимок_без_proc_это_none_а_не_исключение(self):
        orig = ts.open if hasattr(ts, "open") else None
        ts.open = lambda *a, **k: (_ for _ in ()).throw(FileNotFoundError("/proc/loadavg"))
        try:
            self.assertIsNone(ts.machine_snapshot())
        finally:
            if orig is None:
                del ts.open
            else:
                ts.open = orig

    def test_явное_число_и_auto(self):
        self.assertEqual(3, ts.parse_n("3"))
        self.assertEqual("auto", ts.parse_n("auto"))
        with self.assertRaises(Exception):
            ts.parse_n("0")
        self.assertEqual(3, ts.resolve_n(3))
        self.assertEqual([("a/B.Tests", "auto"), ("c/D.Tests", 2)], ts.parse_split("a/B.Tests=auto,c/D.Tests=2"))

    def test_run_с_явным_n_не_зовёт_правило_а_auto_печатает_выбор_первой_строкой(self):
        orig = ts.list_tests, ts.machine_snapshot
        seen = []
        ts.machine_snapshot = lambda: seen.append(1) or (24, 2.0, 31 * ts.GIB, None)
        ts.list_tests = lambda project, extra: (None, "стоп после выбора N")
        try:
            out = io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(2, ts.main(["--run", "x", "-n", "3"]))
            self.assertEqual([], seen)
            self.assertEqual("", out.getvalue())
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                ts.main(["--run", "x"])
            self.assertEqual(f"Шардов: {ts.ADAPTIVE_MAX} (load1 2,0 из 24, свободно 31 ГиБ)", out.getvalue().splitlines()[0])
        finally:
            ts.list_tests, ts.machine_snapshot = orig

    def test_verify_с_auto_не_зависит_от_машины(self):
        orig = ts.machine_snapshot
        ts.machine_snapshot = lambda: self.fail("раскладка CI не должна читать нагрузку")
        try:
            with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
                f.write(LIST)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(0, ts.main(["--list", f.name, "--verify"]))
            self.assertIn(f"из {ts.ADAPTIVE_FALLBACK} шардов", out.getvalue())
        finally:
            ts.machine_snapshot = orig
            os.remove(f.name)


class CgroupFreeTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="ccs-cg-")
        self.root = os.path.join(self.dir, "cg")
        self.leaf = os.path.join(self.root, "a.slice", "a-b.slice", "run.scope")
        os.makedirs(self.leaf)
        self.cgfile = os.path.join(self.dir, "cgroup")
        self.write(self.cgfile, "0::/a.slice/a-b.slice/run.scope\n")

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    @staticmethod
    def write(path, text):
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)

    def limit(self, rel, maximum, current):
        node = os.path.join(self.root, rel)
        self.write(os.path.join(node, "memory.max"), f"{maximum}\n")
        self.write(os.path.join(node, "memory.current"), f"{current}\n")

    def free(self):
        return ts.cgroup_free(self.cgfile, self.root)

    def test_нет_файла_cgroup_или_строки_v2_это_none(self):
        self.assertIsNone(ts.cgroup_free(os.path.join(self.dir, "нет"), self.root))
        self.write(self.cgfile, "12:memory:/x\n")
        self.assertIsNone(self.free())

    def test_везде_max_или_файлов_нет_это_none(self):
        self.assertIsNone(self.free())
        self.limit("a.slice/a-b.slice/run.scope", "max", 100)
        self.limit("a.slice/a-b.slice", "max", 100)
        self.assertIsNone(self.free())

    def test_неразборчивое_значение_это_none_без_исключения(self):
        self.limit("a.slice/a-b.slice", "мусор", 1)
        self.assertIsNone(self.free())
        self.limit("a.slice/a-b.slice", 100, "")
        self.assertIsNone(self.free())

    def test_минимум_по_цепочке_scope_и_slice(self):
        self.limit("a.slice/a-b.slice/run.scope", 16 * ts.GIB, 1 * ts.GIB)
        self.limit("a.slice/a-b.slice", 24 * ts.GIB, 17 * ts.GIB)
        self.assertEqual(7 * ts.GIB, self.free(), "slice почти полон — он и ограничивает, хотя scope свободен")
        self.limit("a.slice/a-b.slice", 24 * ts.GIB, 25 * ts.GIB)
        self.assertEqual(0, self.free(), "перебор над max — ноль, а не минус")

    def test_снимок_несёт_запас_cgroup(self):
        orig = ts.cgroup_free
        ts.cgroup_free = lambda: 5 * ts.GIB
        try:
            snap = ts.machine_snapshot()
        finally:
            ts.cgroup_free = orig
        if snap is not None:  # на машине без /proc снимка нет вовсе
            self.assertEqual(5 * ts.GIB, snap[3])


class AdaptiveSplitTests(unittest.TestCase):
    SPLIT = [("backend/ClaudeHomeServer.Tests", 3), ("backend/ClaudeHomeServer.Llm.Tests", 2)]

    def test_без_переменной_split_как_есть(self):
        self.assertEqual([("backend/ClaudeHomeServer.Tests", 3, ts.ADAPTIVE_MAX),
                          ("backend/ClaudeHomeServer.Llm.Tests", 2, ts.ADAPTIVE_MAX)],
                         ts.adaptive_split(self.SPLIT, None))

    def test_переменная_делает_проект_auto_под_своим_потолком(self):
        self.assertEqual([("backend/ClaudeHomeServer.Tests", "auto", 4),
                          ("backend/ClaudeHomeServer.Llm.Tests", 2, ts.ADAPTIVE_MAX)],
                         ts.adaptive_split(self.SPLIT, "backend/ClaudeHomeServer.Tests/=4"))

    def test_неразборчивая_переменная_предупреждение_и_split_как_есть(self):
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            out = ts.adaptive_split(self.SPLIT, "мусор")
        self.assertEqual([3, 2], [n for _, n, _ in out])
        self.assertIn(ts.ADAPTIVE_ENV, err.getvalue())

    def test_run_fast_с_переменной_выбирает_n_по_правилу(self):
        orig = ts.machine_snapshot, ts.calibrate
        ts.machine_snapshot = lambda: (24, 2.0, 28 * ts.GIB, 7 * ts.GIB)
        ts.calibrate = lambda results: (_ for _ in ()).throw(RuntimeError("стоп после выбора N"))
        os.environ[ts.ADAPTIVE_ENV] = "backend/ClaudeHomeServer.Tests=4"
        try:
            out = io.StringIO()
            with contextlib.redirect_stdout(out), self.assertRaises(RuntimeError):
                ts.run_fast("x.slnx", self.SPLIT, "r", None, None)
            self.assertEqual("Шардов ClaudeHomeServer.Tests: 2 (load1 2,0 из 24, свободно 28 ГиБ, в cgroup 7 ГиБ)",
                             out.getvalue().splitlines()[0])
        finally:
            ts.machine_snapshot, ts.calibrate = orig
            del os.environ[ts.ADAPTIVE_ENV]


class ShardPeakTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="ccs-peak-")

    def tearDown(self):
        shutil.rmtree(self.dir, ignore_errors=True)

    def put(self, text):
        with open(os.path.join(self.dir, ts.SHARD_PEAK_FILE), "w", encoding="utf-8") as f:
            f.write(text)

    def test_записи_нет_или_мусор_это_none(self):
        self.assertIsNone(ts.load_shard_peak(self.dir))
        for text in ("мусор", "[]", '{"per_shard_bytes": "1"}', '{"per_shard_bytes": true}',
                     '{"per_shard_bytes": 0}', '{"per_shard_bytes": 300000000000000}'):
            self.put(text)
            with self.subTest(text=text):
                self.assertIsNone(ts.load_shard_peak(self.dir))

    def test_запись_пика_на_шард_и_чтение(self):
        ts.save_shard_peak(self.dir, 12 * ts.GIB, 4)
        self.assertEqual(3 * ts.GIB, ts.load_shard_peak(self.dir))

    def test_один_процесс_не_пишется(self):
        ts.save_shard_peak(self.dir, 6 * ts.GIB, 1)
        ts.save_shard_peak(self.dir, 0, 3)
        self.assertFalse(os.path.exists(os.path.join(self.dir, ts.SHARD_PEAK_FILE)))

    def test_память_на_шард_запасное_множитель_и_пол(self):
        self.assertEqual(ts.ADAPTIVE_SHARD_MEM, ts.shard_mem(None))
        self.assertEqual(int(2 * ts.GIB * 1.2), ts.shard_mem(2 * ts.GIB))
        self.assertEqual(int(1 * ts.GIB * 1.2), ts.shard_mem(1))

    def test_auto_берёт_прошлый_пик_из_results(self):
        ts.save_shard_peak(self.dir, 10 * ts.GIB, 2)  # 5 ГиБ на шард × 1,2 = 6
        orig = ts.machine_snapshot
        ts.machine_snapshot = lambda: (24, 2.0, 31 * ts.GIB, 13 * ts.GIB)
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(2, ts.resolve_n("auto", results=self.dir))
                self.assertEqual(3, ts.resolve_n("auto"), "без записи — запасные 3,5 ГиБ: 13 / 3,5")
        finally:
            ts.machine_snapshot = orig

    def test_run_shards_пишет_пик_после_прогона(self):
        orig = ts.list_tests, ts.launch_shards, ts.wait_all
        ts.list_tests = lambda project, extra: (["A.X.t1", "B.Y.t2", "C.Z.t3"], None)
        ts.launch_shards = lambda project, shards, results, prefix, extra, tests: ([object()] * len(shards), [], [])

        def fake_wait(procs, start, proofs=None, reports=None):
            ts.LAST_PEAK_BYTES = 9 * ts.GIB
            return 0

        ts.wait_all = fake_wait
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, ts.run_shards("x", 3, self.dir, None, None))
            self.assertEqual(3 * ts.GIB, ts.load_shard_peak(self.dir))
        finally:
            ts.list_tests, ts.launch_shards, ts.wait_all = orig


class PregateConfigCompatTests(unittest.TestCase):
    """Конфиг предгейта берётся с СЕРВЕРА, а скрипт — из дерева ветки: --split обязан остаться понятен скрипту
    старых деревьев (только числа), адаптивность — переменной окружения, которую старый скрипт не видит."""

    def check(self):
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "backend", "ClaudeHomeServer",
                            "appsettings.json")
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)["Pregate"]["Checks"]["быстрые тесты"]

    def test_split_только_числа_адаптивность_в_env(self):
        check = self.check()
        split = check["Args"][check["Args"].index("--split") + 1]
        for item in split.split(","):
            self.assertRegex(item, r"=\d+$", "старый parse_split бросает ValueError на всём, кроме числа")
        self.assertIn("backend/ClaudeHomeServer.Tests=3", split.split(","), "старый скрипт — 3 шарда монолита")
        self.assertEqual({ts.ADAPTIVE_ENV: "backend/ClaudeHomeServer.Tests=4"}, check["Env"])


def write_trx_full(directory, name, results, stamp="20261103120000"):
    """results: [(testName, outcome, сборка|None)] -> TRX с TestDefinitions (storage сборки), как пишет логгер VSTest."""
    defs, rows = [], []
    for i, (n, o, asm) in enumerate(results):
        tid = f"id-{i}"
        if asm:
            defs.append(f'<UnitTest id="{tid}" name="{n}" storage="/x/bin/{asm}.dll" />')
        rows.append(f'<UnitTestResult testId="{tid}" testName="{n}" outcome="{o}" />')
    path = os.path.join(directory, f"{name}_net10.0_{stamp}.trx")
    with open(path, "w", encoding="utf-8-sig") as f:
        f.write(f'<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="{TRX_NS}">'
                f'<TestDefinitions>{"".join(defs)}</TestDefinitions><Results>{"".join(rows)}</Results></TestRun>')
    return path


class FakeProc:
    def __init__(self, rc):
        self.returncode = rc

    def wait(self):
        return self.returncode


def filter_fqns(flt):
    return [t[len("FullyQualifiedName="):] for t in flt.split("|")]


class RerunTests(unittest.TestCase):
    """Перезапуск упавших в локальных --run/--run-fast: флак зелёный, но виден; упавший дважды и аборт — красные."""
    EXPECTED = {"App.Tests.A.One", "App.Tests.A.Two", "App.Tests.B.Three"}

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.launched = []
        self._orig = ts.launch
        ts.launch = self.fake_launch
        self.second = {}  # FQN -> исход второго запуска; нет в словаре — тест во втором запуске не нашёлся

    def tearDown(self):
        ts.launch = self._orig
        shutil.rmtree(self.tmp, ignore_errors=True)

    def fake_launch(self, args, log_path):
        flt = args[args.index("--filter") + 1]
        directory = args[args.index("--results-directory") + 1]
        prefix = next(a for a in args if a.startswith("trx;LogFilePrefix="))[len("trx;LogFilePrefix="):]
        fqns = filter_fqns(flt)
        self.launched.append((args[0], fqns))
        rows = [(f, self.second[f], None) for f in fqns if f in self.second]
        write_trx_full(directory, prefix, rows)
        return FakeProc(1 if any(o != "Passed" for _, o, _ in rows) else 0), open(log_path, "w")

    def shard(self, code, results, expected=None, target="b/App.Tests", log_text="", proof=None, name="shard1"):
        """(процесс, доказательство, отчёт) шарда первого прогона с TRX results."""
        run = tempfile.mkdtemp(dir=self.tmp)  # своя папка: TRX одного префикса у разных случаев не затирают друг друга
        write_trx_full(run, name, results)
        log = os.path.join(run, f"{name}.log")
        with open(log, "w", encoding="utf-8") as f:
            f.write(log_text)
        report = lambda: {**ts.trx_report(run, name, 0), "target": target,
                          "expected": self.EXPECTED if expected is None else expected, "log": log}
        return (FakeProc(code), None), proof, report

    def rerun(self, *shards, code=1, rerun_max=ts.RERUN_MAX, targets=None):
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
            got = ts.rerun_failed(code, [s[0] for s in shards], [s[1] for s in shards], [s[2] for s in shards],
                                  self.tmp, targets or {"App.Tests": "b/App.Tests"}, rerun_max)
        return got, out.getvalue()

    def flaky_json(self):
        with open(os.path.join(self.tmp, ts.FLAKY_FILE), encoding="utf-8") as f:
            return json.load(f)

    RED = [("App.Tests.A.One", "Failed", "App.Tests"), ("App.Tests.A.Two", "Passed", "App.Tests"),
           ("App.Tests.B.Three", "Passed", "App.Tests")]

    def test_упал_и_прошёл_со_второго_раза_зелёный_с_блоком_флаки_и_flaky_json(self):
        self.second = {"App.Tests.A.One": "Passed"}
        code, out = self.rerun(self.shard(1, self.RED))
        self.assertEqual(0, code)
        self.assertEqual([("b/App.Tests", ["App.Tests.A.One"])], self.launched)
        self.assertIn("Флаки (прошли со второго раза): 1\n  App.Tests.A.One\n", out)
        self.assertNotIn("Упали дважды", out)
        data = self.flaky_json()
        self.assertEqual([], data["failed_twice"])
        (entry,) = data["flaky"]
        self.assertEqual(("App.Tests.A.One", "App.Tests.A", "App.Tests"), (entry["fqn"], entry["class"], entry["assembly"]))
        self.assertRegex(entry["time"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d")

    def test_упал_дважды_красный_и_флаки_отдельно(self):
        red = [("App.Tests.A.One", "Failed", None), ("App.Tests.A.Two", "Failed", None),
               ("App.Tests.B.Three", "Passed", None)]
        self.second = {"App.Tests.A.One": "Failed", "App.Tests.A.Two": "Passed"}
        code, out = self.rerun(self.shard(1, red))
        self.assertEqual(1, code)
        self.assertIn("Упали дважды: 1\n  App.Tests.A.One\n", out)
        self.assertIn("Флаки (прошли со второго раза): 1\n  App.Tests.A.Two\n", out)
        data = self.flaky_json()
        self.assertEqual(["App.Tests.A.One"], data["failed_twice"])
        self.assertEqual(["App.Tests.A.Two"], [e["fqn"] for e in data["flaky"]])
        self.assertEqual("App.Tests", data["flaky"][0]["assembly"], "сборка без storage — по проекту шарда")

    def test_во_втором_запуске_теста_нет_это_не_зелёный(self):
        self.second = {}  # фильтр не нашёл тест: прохождение не доказано
        code, out = self.rerun(self.shard(1, self.RED))
        self.assertEqual(1, code)
        self.assertIn("Упали дважды: 1", out)

    def test_положительный_код_второго_запуска_не_прощается(self):
        self.second = {"App.Tests.A.One": "Passed"}
        orig = self.fake_launch

        def launch(args, log_path):
            _, log = orig(args, log_path)
            return FakeProc(1), log
        ts.launch = launch
        code, out = self.rerun(self.shard(1, self.RED))
        self.assertEqual(1, code)
        self.assertIn("Упали дважды: 1", out)

    def test_упавших_больше_rerun_max_без_перезапуска_красный(self):
        many = [(f"App.Tests.A.T{i:02}", "Failed", None) for i in range(ts.RERUN_MAX + 1)]
        self.second = {f: "Passed" for f, _, _ in many}
        code, out = self.rerun(self.shard(1, many, expected=set()))
        self.assertEqual(1, code)
        self.assertEqual([], self.launched)
        self.assertIn(f"Перезапуск пропущен: упало {ts.RERUN_MAX + 1} > RERUN_MAX ({ts.RERUN_MAX})", out)
        self.assertFalse(os.path.exists(os.path.join(self.tmp, ts.FLAKY_FILE)))

    def test_ровно_rerun_max_упавших_перезапускаются(self):
        many = [(f"App.Tests.A.T{i:02}", "Failed", None) for i in range(ts.RERUN_MAX)]
        self.second = {f: "Passed" for f, _, _ in many}
        code, out = self.rerun(self.shard(1, many, expected=set()))
        self.assertEqual(0, code)
        self.assertEqual(ts.RERUN_MAX, sum(len(f) for _, f in self.launched))
        self.assertIn(f"Флаки (прошли со второго раза): {ts.RERUN_MAX}", out)

    def test_аборт_шарда_перезапуском_не_маскируется(self):
        self.second = {"App.Tests.A.One": "Passed"}
        green = [(n, "Passed", None) for n in sorted(self.EXPECTED)]
        cases = {
            "сигнальный код": [self.shard(-6, self.RED)],
            "TRX без провалов": [self.shard(1, green)],
            "TRX нет": [((FakeProc(1), None), None, lambda: {**ts.trx_report(self.tmp, "нет", 0), "target": "b/App.Tests"})],
            "TRX неполный": [self.shard(1, self.RED[:2])],
            "обрыв в логе": [self.shard(1, self.RED, log_text="The active test run was aborted. Reason: Test host process crashed")],
            "рядом с флаком оборванный сосед": [self.shard(1, self.RED), self.shard(-9, [], name="shard2")],
        }
        for label, shards in cases.items():
            with self.subTest(label):
                self.launched.clear()
                code, out = self.rerun(*shards)
                self.assertEqual(1, code)
                self.assertEqual([], self.launched)
                self.assertIn("Перезапуск пропущен:", out)
                self.assertIn("не маскирует обрыв", out)

    def test_прощённый_по_доказательству_шард_не_мешает_перезапуску_соседа(self):
        self.second = {"App.Tests.A.One": "Passed"}
        forgiven = self.shard(-6, [(n, "Passed", None) for n in sorted(self.EXPECTED)], name="shard2",
                              proof=lambda: None)
        code, _ = self.rerun(self.shard(1, self.RED), forgiven)
        self.assertEqual(0, code)
        self.assertEqual(1, len(self.launched))

    def test_rest_перезапускается_в_проекте_своей_сборки(self):
        self.second = {"Other.Tests.X.Y": "Passed"}
        rest = self.shard(1, [("Other.Tests.X.Y", "Failed", "Other.Tests"), ("Third.Tests.Z.W", "Passed", "Third.Tests")],
                          expected=set(), target=None, name="fast-rest")
        code, _ = self.rerun(rest, targets={"App.Tests": "b/App.Tests", "Other.Tests": "b/Other.Tests/Other.Tests.csproj"})
        self.assertEqual(0, code)
        self.assertEqual([("b/Other.Tests/Other.Tests.csproj", ["Other.Tests.X.Y"])], self.launched)

    def test_проект_не_найден_тест_упал_дважды_без_запуска(self):
        rest = self.shard(1, [("Nowhere.Tests.X.Y", "Failed", None)], expected=set(), target=None, name="fast-rest")
        code, out = self.rerun(rest)
        self.assertEqual(1, code)
        self.assertEqual([], self.launched)
        self.assertIn("Nowhere.Tests.X.Y (проект не найден — не перезапускался)", out)


# 20 длинных русских имён: класс и метод — по 150+ символов кириллицы, FQN ≈ 400 символов (≈ 750 байт UTF-8)
LONG_RU = [f"ClaudeHomeServer.Tests.Services.Длинное.Пространство.Имён.{'Очень' * 20}Класс{i:02}Tests."
           f"{'метод_с_длинным_русским_именем_' * 7}{i:02}" for i in range(20)]


class RerunFilterTests(unittest.TestCase):
    def test_20_длинных_русских_имён_режутся_под_лимит_и_покрывают_всё(self):
        chunks = ts.rerun_chunks(LONG_RU)
        self.assertGreater(len(chunks), 1, "8000 символов на 20 имён по ≈ 400 не помещаются в один фильтр")
        self.assertEqual(LONG_RU, [f for c in chunks for f in c])
        for c in chunks:
            flt = ts.fqn_filter(c)
            self.assertLessEqual(len(flt), ts.RERUN_FILTER_CHARS)
            # вся командная строка dotnet test укладывается в предел Windows 32 767 с запасом под путь проекта
            cmd = ["dotnet", "test", "/" + "п" * 200 + "/ClaudeHomeServer.Tests", "--no-build", "--filter", flt,
                   "--logger", "trx;LogFilePrefix=rerun-99", "--results-directory", "/" + "р" * 300]
            self.assertLess(len(subprocess.list2cmdline(cmd)), 32767)

    def test_фильтр_доходит_до_процесса_целиком(self):
        for c in ts.rerun_chunks(LONG_RU):
            flt = ts.fqn_filter(c)
            got = subprocess.run([sys.executable, "-I", "-c", "import sys; sys.stdout.buffer.write(sys.argv[1].encode())", flt],
                                 capture_output=True, check=True).stdout.decode("utf-8")
            self.assertEqual(flt, got)
            self.assertEqual(c, filter_fqns(got))

    def test_фильтр_по_точному_имени_с_экранированием(self):
        self.assertEqual("FullyQualifiedName=A.B+C.D|FullyQualifiedName=A.B.E", ts.fqn_filter(["A.B+C.D", "A.B.E"]))
        self.assertEqual("FullyQualifiedName=A.B\\=C", ts.fqn_filter(["A.B=C"]))

    def test_имя_длиннее_лимита_отдельным_куском(self):
        self.assertEqual([["a"], ["b" * 50], ["c"]], ts.rerun_chunks(["a", "b" * 50, "c"], limit=30))


class RerunModesTests(unittest.TestCase):
    """Включение перезапуска: --run/--run-fast по умолчанию, --no-rerun и CCS_TEST_SHARDS_RERUN=0 выключают,
    CI-режимы (--shard, --verify, --slnx) его не делают вовсе."""

    def capture(self, argv, env=None):
        seen = {}
        orig = (ts.run_fast, ts.run_shards, ts.rerun_failed)
        def stub(name):
            def run(*a, **k):
                seen[name] = a[6]  # rerun_max — седьмой позиционный аргумент run_fast/run_shards
                return 0
            return run
        ts.run_fast, ts.run_shards = stub("run_fast"), stub("run")
        ts.rerun_failed = lambda *a, **k: self.fail("перезапуск в CI-режиме")
        old = os.environ.get(ts.RERUN_ENV)
        if env is not None:
            os.environ[ts.RERUN_ENV] = env
        try:
            with tempfile.TemporaryDirectory() as tmp:
                slnx = os.path.join(tmp, "x.slnx")
                with open(slnx, "w", encoding="utf-8") as f:
                    f.write("<Solution />")
                argv = [slnx if a == "SLNX" else a for a in argv]
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    code = ts.main(argv)
        finally:
            ts.run_fast, ts.run_shards, ts.rerun_failed = orig
            if old is None:
                os.environ.pop(ts.RERUN_ENV, None)
            else:
                os.environ[ts.RERUN_ENV] = old
        return code, seen

    def test_по_умолчанию_включён_с_порогом_20(self):
        self.assertEqual(20, ts.RERUN_MAX)
        self.assertEqual({"run_fast": 20}, self.capture(["--run-fast", "SLNX"])[1])
        self.assertEqual({"run": 20}, self.capture(["--run", "x.csproj", "-n", "2"])[1])
        self.assertEqual({"run": 5}, self.capture(["--run", "x.csproj", "-n", "2", "--rerun-max", "5"])[1])

    def test_no_rerun_и_переменная_выключают(self):
        self.assertEqual({"run_fast": None}, self.capture(["--run-fast", "SLNX", "--no-rerun"])[1])
        self.assertEqual({"run": None}, self.capture(["--run", "x.csproj", "-n", "2", "--no-rerun"])[1])
        self.assertEqual({"run_fast": None}, self.capture(["--run-fast", "SLNX"], env="0")[1])
        self.assertEqual({"run": None}, self.capture(["--run", "x.csproj", "-n", "2"], env="0")[1])
        self.assertEqual({"run_fast": 20}, self.capture(["--run-fast", "SLNX"], env="1")[1])

    def test_ci_режимы_перезапуск_не_делают(self):
        with tempfile.TemporaryDirectory() as tmp:
            lst = os.path.join(tmp, "list.txt")
            with open(lst, "w", encoding="utf-8") as f:
                f.write(LIST)
            for argv in (["--list", lst, "--shard", "1/2"], ["--list", lst, "--verify"]):
                with self.subTest(argv[2]):
                    self.assertEqual((0, {}), self.capture(argv))
        slnx = """<Solution>\n  <Project Path="A.Tests/A.Tests.csproj" />\n</Solution>\n"""
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "s.slnx")
            with open(path, "w", encoding="utf-8") as f:
                f.write(slnx)
            self.assertEqual((0, {}), self.capture(["--slnx", path, "--shard", "1/1"]))

    def test_run_shards_без_порога_не_перезапускает(self):
        orig = (ts.list_tests, ts.launch, ts.wait_all, ts.rerun_failed)
        ts.list_tests = lambda path, flt: ([f"App.Tests.C{i}.T" for i in range(4)], None)
        ts.launch = lambda args, log: (FakeProc(1), None)
        ts.wait_all = lambda procs, start, proofs=None, reports=None: 1
        calls = []
        ts.rerun_failed = lambda *a: calls.append(a) or 0
        try:
            with tempfile.TemporaryDirectory() as tmp:
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(1, ts.run_shards("b/App.Tests", 2, tmp, {}, None, rerun_max=None))
                    self.assertEqual([], calls)
                    self.assertEqual(0, ts.run_shards("b/App.Tests", 2, tmp, {}, None))
                    self.assertEqual(1, len(calls))
        finally:
            ts.list_tests, ts.launch, ts.wait_all, ts.rerun_failed = orig


class RunFastRerunTests(unittest.TestCase):
    """Сквозной --run-fast с настоящим wait_all: процессы — python с нужным кодом, TRX пишет подменённый launch."""
    SLNX = RunFastTests.SLNX
    LISTING = [f"App.Tests.C{i}.T" for i in range(4)]

    def run_fast(self, first, second, stale=False):
        launched = []
        orig = (ts.list_tests, ts.launch)
        ts.list_tests = lambda path, flt: (self.LISTING, None)

        def fake_launch(args, log_path):
            flt = args[args.index("--filter") + 1]
            directory = args[args.index("--results-directory") + 1]
            prefix = next(a for a in args if a.startswith("trx;LogFilePrefix="))[len("trx;LogFilePrefix="):]
            if prefix.startswith("rerun-"):
                # перезапуск последовательный: предыдущий запуск обязан уже завершиться
                self.assertTrue(all(p.poll() is not None for _, _, p in launched if _ == "rerun"))
                rows = [(f, second.get(f, "Failed"), None) for f in filter_fqns(flt)]
                kind = "rerun"
            elif prefix == "fast-rest":
                rows = [("Other.Tests.X.Y", first.get("Other.Tests.X.Y", "Passed"), "Other.Tests")]
                kind = "rest"
            else:
                rows = [(t, first.get(t, "Passed"), None) for t in self.LISTING if f"~{ts.class_of(t)}." in flt]
                kind = "shard"
            write_trx_full(directory, prefix, rows)
            rc = 1 if any(o != "Passed" for _, o, _ in rows) else 0
            proc = subprocess.Popen([sys.executable, "-c", f"raise SystemExit({rc})"])
            launched.append((kind, args[0], proc))
            return proc, open(log_path, "w")
        ts.launch = fake_launch
        try:
            with tempfile.TemporaryDirectory() as tmp:
                slnx = os.path.join(tmp, "x.slnx")
                with open(slnx, "w", encoding="utf-8") as f:
                    f.write(self.SLNX)
                results = os.path.join(tmp, "r")
                os.makedirs(results)
                if stale:
                    with open(os.path.join(results, ts.FLAKY_FILE), "w", encoding="utf-8") as f:
                        f.write('{"flaky": [{"fqn": "Old.Flaky"}]}')
                out = io.StringIO()
                with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
                    code = ts.run_fast(slnx, [("b/App.Tests", 2)], results, {}, "F")
                flaky_path = os.path.join(results, ts.FLAKY_FILE)
                flaky = json.load(open(flaky_path, encoding="utf-8")) if os.path.exists(flaky_path) else None
                reruns = [(target) for kind, target, _ in launched if kind == "rerun"]
                return code, out.getvalue(), flaky, reruns, tmp
        finally:
            ts.list_tests, ts.launch = orig

    def test_флаки_шарда_и_rest_прошли_со_второго_раза_exit_0(self):
        first = {"App.Tests.C1.T": "Failed", "Other.Tests.X.Y": "Failed"}
        second = {"App.Tests.C1.T": "Passed", "Other.Tests.X.Y": "Passed"}
        code, out, flaky, reruns, tmp = self.run_fast(first, second)
        self.assertEqual(0, code)
        self.assertIn("Флаки (прошли со второго раза): 2", out)
        self.assertEqual({"App.Tests.C1.T", "Other.Tests.X.Y"}, {e["fqn"] for e in flaky["flaky"]})
        self.assertEqual({"App.Tests", "Other.Tests"}, {e["assembly"] for e in flaky["flaky"]})
        self.assertEqual(sorted(["b/App.Tests", os.path.join(tmp, "Other.Tests/Other.Tests.csproj")]), sorted(reruns))

    def test_упавший_дважды_красный(self):
        first = {"App.Tests.C1.T": "Failed", "Other.Tests.X.Y": "Failed"}
        second = {"App.Tests.C1.T": "Passed", "Other.Tests.X.Y": "Failed"}
        code, out, flaky, _, _ = self.run_fast(first, second)
        self.assertNotEqual(0, code)
        self.assertIn("Упали дважды: 1\n  Other.Tests.X.Y\n", out)
        self.assertEqual(["Other.Tests.X.Y"], flaky["failed_twice"])
        self.assertEqual(["App.Tests.C1.T"], [e["fqn"] for e in flaky["flaky"]])

    def test_зелёный_прогон_без_перезапуска_и_старый_flaky_json_удалён(self):
        code, out, flaky, reruns, _ = self.run_fast({}, {}, stale=True)
        self.assertEqual(0, code)
        self.assertEqual([], reruns)
        self.assertIsNone(flaky)
        self.assertNotIn("Перезапуск", out)


if __name__ == "__main__":
    unittest.main()
