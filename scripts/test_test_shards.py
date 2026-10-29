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

    def test_все_шарды_нулевые_дают_ноль_а_ненулевой_код_сохраняется(self):
        self.assertEqual(0, self._codes("pass", "pass"))
        self.assertEqual(3, self._codes("pass", "raise SystemExit(3)"))


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
        ts.wait_all = lambda procs, start: 0
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

    def test_шард_потерял_класс_самопроверка_краснеет_до_запуска(self):
        code, launched, _ = self._run(tamper=True)
        self.assertEqual(1, code)
        self.assertEqual([], launched)


if __name__ == "__main__":
    unittest.main()
