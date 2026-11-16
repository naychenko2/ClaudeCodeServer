"""Тесты помощника без железа и без root: ipmitool подменяется функцией.

Запуск: python3 -I deploy/ipmi-helper/test_ccs_ipmi_helper.py
"""
import importlib.util
import json
import os
import subprocess
import tempfile
import threading
import unittest
from unittest import mock

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "helper", os.path.join(_HERE, "ccs-ipmi-helper.py"))
helper = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(helper)

SAMPLE = ("CPU1 Temp        | 01h | ok  |  3.1 | 41 degrees C\n"
          "FAN1             | 41h | ok  | 29.1 | 4500 RPM\n")


def read_json(path):
    with open(path) as f:
        return json.load(f)


class CollectTests(unittest.TestCase):
    def test_payload_shape_and_verbatim_stdout(self):
        p = helper.collect(lambda: SAMPLE, now_ms=1234)
        self.assertEqual(p, {"at": 1234, "sdr": SAMPLE})

    def test_at_set_after_ipmitool_finished(self):
        order = []

        def slow():
            order.append("run")
            return SAMPLE

        with mock.patch.object(helper.time, "time", lambda: order.append("clock") or 5.0):
            p = helper.collect(slow)
        self.assertEqual(order, ["run", "clock"])
        self.assertEqual(p["at"], 5000)

    def test_failure_returns_none(self):
        def boom_rc():
            raise RuntimeError("код 1")

        def boom_timeout():
            raise subprocess.TimeoutExpired("ipmitool", 10)

        def boom_missing():
            raise FileNotFoundError("/usr/bin/ipmitool")

        for fn in (boom_rc, boom_timeout, boom_missing):
            self.assertIsNone(helper.collect(fn, now_ms=1))

    def test_oversize_returns_none(self):
        self.assertIsNone(helper.collect(lambda: "x" * (helper.MAX_FILE_BYTES + 1), now_ms=1))

    def test_below_limit_ok(self):
        p = helper.collect(lambda: "x" * 16000, now_ms=1)
        self.assertIsNotNone(p)


class RunIpmitoolTests(unittest.TestCase):
    def test_command_is_absolute_no_shell_with_explicit_device(self):
        done = subprocess.CompletedProcess([], 0, stdout=b"ok\n", stderr=b"")
        with mock.patch.object(helper.subprocess, "run", return_value=done) as m:
            out = helper.run_ipmitool()
        self.assertEqual(out, "ok\n")
        args, kwargs = m.call_args
        self.assertEqual(args[0], ["/usr/bin/ipmitool", "-I", "open", "-d", "0", "sdr", "elist", "full"])
        self.assertIs(kwargs["shell"], False)
        self.assertEqual(kwargs["timeout"], 10)

    def test_nonzero_exit_raises(self):
        done = subprocess.CompletedProcess([], 1, stdout=b"", stderr=b"no device")
        with mock.patch.object(helper.subprocess, "run", return_value=done):
            with self.assertRaises(RuntimeError):
                helper.run_ipmitool()


class WriteAtomicTests(unittest.TestCase):
    def test_writes_json_0644_and_leaves_no_tmp(self):
        with tempfile.TemporaryDirectory() as d:
            helper.write_atomic({"at": 1, "sdr": "a"}, d)
            self.assertEqual(read_json(os.path.join(d, "sdr.json")), {"at": 1, "sdr": "a"})
            self.assertEqual(os.stat(os.path.join(d, "sdr.json")).st_mode & 0o777, 0o644)
            self.assertEqual(os.listdir(d), ["sdr.json"])

    def test_tmp_name_is_per_process(self):
        with tempfile.TemporaryDirectory() as d:
            seen = []
            real_replace = os.replace
            with mock.patch.object(helper.os, "replace",
                                   lambda a, b: (seen.append(a), real_replace(a, b))):
                helper.write_atomic({"at": 1, "sdr": ""}, d)
            self.assertEqual(os.path.basename(seen[0]), "sdr.json.%d.tmp" % os.getpid())

    def test_failure_removes_tmp_and_keeps_old_file(self):
        with tempfile.TemporaryDirectory() as d:
            helper.write_atomic({"at": 1, "sdr": "old"}, d)
            with mock.patch.object(helper.os, "replace", side_effect=OSError("диск")):
                with self.assertRaises(OSError):
                    helper.write_atomic({"at": 2, "sdr": "new"}, d)
            self.assertEqual(os.listdir(d), ["sdr.json"])
            self.assertEqual(read_json(os.path.join(d, "sdr.json"))["sdr"], "old")

    def test_reader_never_sees_partial(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "sdr.json")
            helper.write_atomic({"at": 0, "sdr": "s"}, d)
            done = threading.Event()
            errors = []
            reads = [0]

            def reader():
                while not done.is_set():
                    try:
                        with open(path) as f:
                            json.loads(f.read())
                    except ValueError as e:
                        errors.append(e)
                    reads[0] += 1

            t = threading.Thread(target=reader)
            t.start()
            try:
                for i in range(500):
                    helper.write_atomic({"at": i, "sdr": "line\n" * (1 + i * 6)}, d)
            finally:
                done.set()
                t.join()
            self.assertGreater(reads[0], 0)
            self.assertEqual(errors, [])


class RunLoopTests(unittest.TestCase):
    def test_failed_run_keeps_old_file_and_loop_lives(self):
        with tempfile.TemporaryDirectory() as d:
            helper.write_atomic({"at": 1, "sdr": "old"}, d)
            stop = threading.Event()
            calls = []

            def flaky():
                calls.append(1)
                if len(calls) >= 3:
                    stop.set()
                raise RuntimeError("BMC молчит")

            helper.run(stop, run_ipmitool=flaky, out_dir=d, interval=0)
            self.assertEqual(len(calls), 3)
            self.assertEqual(read_json(os.path.join(d, "sdr.json"))["sdr"], "old")

    def test_write_error_does_not_kill_loop(self):
        with tempfile.TemporaryDirectory() as d:
            stop = threading.Event()
            calls = []

            def ok():
                calls.append(1)
                if len(calls) >= 2:
                    stop.set()
                return SAMPLE

            with mock.patch.object(helper, "write_atomic", side_effect=OSError("ro")):
                helper.run(stop, run_ipmitool=ok, out_dir=d, interval=0)
            self.assertEqual(len(calls), 2)

    def test_writes_and_updates(self):
        with tempfile.TemporaryDirectory() as d:
            stop = threading.Event()
            n = [0]

            def ok():
                n[0] += 1
                if n[0] == 2:
                    stop.set()
                return "v%d" % n[0]

            helper.run(stop, run_ipmitool=ok, out_dir=d, interval=0)
            self.assertEqual(read_json(os.path.join(d, "sdr.json"))["sdr"], "v2")

    def test_pause_after_write_not_before_next_start(self):
        # Пауза зовётся ПОСЛЕ записи: между запусками всегда есть stop.wait(interval).
        events = []

        class Stop:
            def __init__(self):
                self.n = 0

            def is_set(self):
                return self.n >= 2

            def wait(self, t):
                events.append(("wait", t))
                self.n += 1

        with tempfile.TemporaryDirectory() as d:
            helper.run(Stop(), run_ipmitool=lambda: events.append("run") or "x",
                       out_dir=d, interval=15.0)
        self.assertEqual(events, ["run", ("wait", 15.0), "run", ("wait", 15.0)])


if __name__ == "__main__":
    unittest.main()
