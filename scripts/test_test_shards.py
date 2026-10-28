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


if __name__ == "__main__":
    unittest.main()
