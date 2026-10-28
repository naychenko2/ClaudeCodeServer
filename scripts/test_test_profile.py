# coding: utf-8
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import tempfile
import unittest

_here = os.path.dirname(__file__)
_spec = importlib.util.spec_from_file_location("tp", os.path.join(_here, "test-profile.py"))
tp = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(tp)

FIXTURE = os.path.join(_here, "fixtures", "test-profile", "mini.trx")


class TestProfileTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.src = os.path.join(self.tmp.name, "src")
        os.makedirs(self.src)
        self._cs("Hosted.cs", "class HostedTests { var f = new TestWebApplicationFactory(); class Nested {} }")
        self._cs("Plain.cs", "public class PlainTests { }")

    def tearDown(self):
        self.tmp.cleanup()

    def _cs(self, name, text):
        with open(os.path.join(self.src, name), "w", encoding="utf-8") as f:
            f.write(text)

    def _report(self):
        results, skipped = tp.read_trx(FIXTURE)
        host, known = tp.host_classes(self.src)
        return tp.build_report(results, skipped, host, known), results, skipped

    def test_parse_duration(self):
        self.assertAlmostEqual(3725.5, tp.parse_duration("01:02:05.5000000"))
        self.assertEqual(0.0, tp.parse_duration(None))
        self.assertEqual(0.0, tp.parse_duration("мусор"))

    def test_trx_не_выполненные_входят_в_результаты_и_считаются_отдельно(self):
        results, skipped = tp.read_trx(FIXTURE)
        self.assertEqual(5, len(results))
        self.assertEqual(1, skipped)

    def test_границы_срезов(self):
        idx = [tp.bucket_of(s) for s in (0.0, 0.0999, 0.1, 0.999, 1.0, 4.999, 5.0, 29.9, 30.0, 999)]
        self.assertEqual([0, 0, 1, 1, 2, 2, 3, 3, 4, 4], idx)

    def test_гистограмма_пропущенный_тест_попадает_в_первый_срез(self):
        report, _, _ = self._report()
        expected = {"< 0,1 с": "2 тестов", "0,1–1 с": "1 тестов", "1–5 с": "1 тестов", "5–30 с": "1 тестов"}
        for label, cnt in expected.items():
            line = next(l for l in report.splitlines() if l.strip().startswith(label))
            self.assertIn(cnt, line)

    def test_доля_хостовых_включая_вложенный_класс(self):
        report, _, _ = self._report()
        # HostedTests.Slow (3 с) и HostedTests+Nested.Heavy (10 с) — хостовые: 2 из 5 тестов, 13 из 13,51 с
        self.assertIn("Хостовые (TestWebApplicationFactory в .cs класса): 2 тестов (40,0%), 13,00 с (96,2% времени)", report)

    def test_классы_вне_исходников_считаются_нехостовыми(self):
        os.remove(os.path.join(self.src, "Hosted.cs"))
        report, _, _ = self._report()
        self.assertIn(": 0 тестов (0,0%)", report)
        self.assertIn("Классы не найдены в исходниках (считаются нехостовыми): 2 тестов", report)

    def test_полное_имя_теста_в_testName_укорачивается(self):
        trx = os.path.join(self.tmp.name, "full.trx")
        with open(FIXTURE, encoding="utf-8") as f:
            text = f.read().replace('testName="Fast"', 'testName="Mini.Plain.PlainTests.Fast"')
        with open(trx, "w", encoding="utf-8") as f:
            f.write(text)
        results, _ = tp.read_trx(trx)
        self.assertIn(("Mini.Plain.PlainTests", "Fast", 0.01), results)

    def test_топ_тестов_по_убыванию(self):
        report, _, _ = self._report()
        order = [report.index(n) for n in ("Nested.Heavy", "HostedTests.Slow", "PlainTests.Mid", "PlainTests.Fast")]
        self.assertEqual(sorted(order), order)

    def test_папка_артефакта_ищет_trx_рекурсивно(self):
        sub = os.path.join(self.tmp.name, "artifacts", "deep")
        os.makedirs(sub)
        with open(FIXTURE, encoding="utf-8") as f, open(os.path.join(sub, "x.trx"), "w", encoding="utf-8") as g:
            g.write(f.read())
        self.assertEqual([os.path.join(sub, "x.trx")], tp.find_trx(os.path.join(self.tmp.name, "artifacts")))

    def test_main_без_trx_возвращает_2(self):
        self.assertEqual(2, tp.main([self.tmp.name, "--src", self.src]))

    def _digests(self):
        files = [os.path.join(self.src, n) for n in sorted(os.listdir(self.src))] + [FIXTURE]
        out = {}
        for f in files:
            with open(f, "rb") as fh:
                out[f] = hashlib.md5(fh.read()).hexdigest()
        return out

    def _main(self, *argv):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = tp.main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_скрипт_только_читает(self):
        # Сравнивается содержимое, а не только состав файлов: дописывание в .cs тоже ловится.
        before = self._digests()
        for mode in ([], ["--json"], ["--check"]):
            self._main(FIXTURE, "--src", self.src, *mode)
        self.assertEqual(before, self._digests())

    def test_шапка_подписывает_сумму_как_не_wall_clock(self):
        report, _, _ = self._report()
        self.assertIn("сумма длительностей тестов (не wall-clock): 13,51 с", report)

    def test_доля_неизвестных_классов_в_процентах(self):
        os.remove(os.path.join(self.src, "Hosted.cs"))
        report, _, _ = self._report()
        self.assertIn("2 тестов (40,0%)", report.splitlines()[-1])

    def test_json_длительность_каждого_класса(self):
        code, out, _ = self._main(FIXTURE, "--json")
        self.assertEqual(0, code)
        data = json.loads(out)
        self.assertEqual(5, data["tests"])
        self.assertEqual({"tests": 3, "seconds": 0.51}, data["classes"]["Mini.Plain.PlainTests"])
        self.assertEqual({"tests": 1, "seconds": 3.0}, data["classes"]["Mini.Host.HostedTests"])
        self.assertEqual({"tests": 1, "seconds": 10.0}, data["classes"]["Mini.Host.HostedTests+Nested"])

    def test_check_тест_дольше_порога_даёт_exit_1(self):
        code, out, err = self._main(FIXTURE, "--check")
        self.assertEqual(1, code)
        self.assertIn("Mini.Host.HostedTests+Nested.Heavy", out)
        self.assertIn("Mini.Host.HostedTests.Slow", out)
        self.assertNotIn("PlainTests.Mid", out)
        self.assertIn("2 тестов дольше 2,0 с", err)

    def test_check_граница_порога_не_нарушение(self):
        self.assertEqual(0, self._main(FIXTURE, "--check", "--threshold", "10")[0])
        self.assertEqual(1, self._main(FIXTURE, "--check", "--threshold", "9.99")[0])

    def test_битый_trx_пропускается_с_предупреждением(self):
        art = os.path.join(self.tmp.name, "art")
        os.makedirs(art)
        with open(FIXTURE, encoding="utf-8") as f, open(os.path.join(art, "a.trx"), "w", encoding="utf-8") as g:
            g.write(f.read())
        with open(os.path.join(art, "b.trx"), "w", encoding="utf-8") as g:
            g.write("not xml")
        code, out, err = self._main(art, "--json")
        self.assertEqual(0, code)
        self.assertEqual(5, json.loads(out)["tests"])
        self.assertIn("пропущен нечитаемый TRX", err)

    def test_все_trx_битые_exit_2(self):
        bad = os.path.join(self.tmp.name, "b.trx")
        with open(bad, "w", encoding="utf-8") as g:
            g.write("not xml")
        self.assertEqual(2, self._main(bad)[0])

    def test_несколько_trx_в_папке_суммируются(self):
        art = os.path.join(self.tmp.name, "art2")
        os.makedirs(art)
        for n in ("a.trx", "b.trx"):
            with open(FIXTURE, encoding="utf-8") as f, open(os.path.join(art, n), "w", encoding="utf-8") as g:
                g.write(f.read())
        data = json.loads(self._main(art, "--json")[1])
        self.assertEqual(10, data["tests"])
        self.assertEqual(6, data["classes"]["Mini.Plain.PlainTests"]["tests"])


if __name__ == "__main__":
    unittest.main()
