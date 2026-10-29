# coding: utf-8
import contextlib
import importlib.util
import io
import json
import os
import tempfile
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
            ts.layout = lambda classes, n, timings=None: [s[1:] for s in original(classes, n, timings)]
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


if __name__ == "__main__":
    unittest.main()
