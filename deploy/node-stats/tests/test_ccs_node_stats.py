"""Тесты агента ccs-node-stats без железа: /sys и /proc — во временной папке, nvidia-smi — фейк.

Запуск: python3 deploy/node-stats/tests/test_ccs_node_stats.py
"""
import http.client
import importlib.util
import json
import os
import socket
import stat
import tempfile
import threading
import time
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "agent", os.path.join(_HERE, "..", "ccs_node_stats.py"))
agent = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(agent)

TOP_KEYS = {"v", "ageMs", "clockMs", "files", "links", "gpuCsv", "gddr6", "statvfs"}


def write(root, path, text):
    full = os.path.join(root, path.lstrip("/"))
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w") as f:
        f.write(text)
    return full


def fake_smi(directory, body, name="nvidia-smi"):
    path = os.path.join(directory, name)
    with open(path, "w") as f:
        f.write("#!/bin/sh\n" + body)
    os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR)
    return path


def fake_statvfs(path):
    class St:
        f_frsize = 4096
        f_blocks = 1000
        f_bfree = 250
    return St()


class TmpRoot(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = os.path.join(self._tmp.name, "root")
        os.makedirs(self.root)
        self.bin = os.path.join(self._tmp.name, "bin")
        os.makedirs(self.bin)

    def env(self, smi=None, timeout=agent.SMI_TIMEOUT_S):
        return agent.Env(root=self.root, nvidia_smi=smi, smi_timeout=timeout, statvfs=fake_statvfs)


class HwmonTests(TmpRoot):
    def test_hwmon_files_by_adr_pattern(self):
        h = "/sys/class/hwmon/hwmon2"
        for name, text in (("name", "k10temp\n"), ("temp1_input", "54250\n"), ("temp1_label", "Tctl\n"),
                           ("temp1_max", "90000\n"), ("temp1_crit", "100000\n"),
                           ("fan1_input", "1200\n"), ("fan1_label", "chipset\n"),
                           # не из перечня — отдавать нельзя
                           ("temp1_offset", "0\n"), ("temp1_input_x", "1\n"), ("tempX_input", "1\n"),
                           ("temp_input", "1\n"), ("power1_input", "5\n"), ("uevent", "x\n"),
                           ("fan1_target", "7\n")):
            write(self.root, h + "/" + name, text)
        s = agent.build_slice(self.env())
        got = {k for k in s["files"] if k.startswith(h + "/")}
        self.assertEqual(got, {h + "/" + n for n in (
            "name", "temp1_input", "temp1_label", "temp1_max", "temp1_crit", "fan1_input", "fan1_label")})
        self.assertEqual(s["files"][h + "/temp1_input"], "54250\n")

    def test_hwmon_without_name_skipped(self):
        write(self.root, "/sys/class/hwmon/hwmon0/temp1_input", "1000\n")
        write(self.root, "/sys/class/hwmon/hwmon1/name", "nvme\n")
        write(self.root, "/sys/class/hwmon/hwmon1/temp1_input", "2000\n")
        s = agent.build_slice(self.env())
        self.assertNotIn("/sys/class/hwmon/hwmon0/temp1_input", s["files"])
        self.assertIn("/sys/class/hwmon/hwmon1/temp1_input", s["files"])

    def test_hwmon_dirs_capped_at_64_in_numeric_order(self):
        for n in range(70):
            write(self.root, "/sys/class/hwmon/hwmon%d/name" % n, "x\n")
        s = agent.build_slice(self.env())
        names = {k for k in s["files"] if k.endswith("/name")}
        self.assertEqual(len(names), 64)
        self.assertIn("/sys/class/hwmon/hwmon9/name", names)      # 9 < 10 числом, а не строкой
        self.assertIn("/sys/class/hwmon/hwmon63/name", names)
        self.assertNotIn("/sys/class/hwmon/hwmon64/name", names)

    def test_device_link_verbatim_only_for_symlink(self):
        write(self.root, "/sys/class/hwmon/hwmon4/name", "nvme\n")
        os.symlink("../../nvme0", self.root + "/sys/class/hwmon/hwmon4/device")
        write(self.root, "/sys/class/hwmon/hwmon5/name", "k10temp\n")
        os.makedirs(self.root + "/sys/class/hwmon/hwmon5/device")  # каталог, не ссылка
        s = agent.build_slice(self.env())
        self.assertEqual(s["links"], {"/sys/class/hwmon/hwmon4/device": "../../nvme0"})

    def test_unreadable_and_oversized_files_skipped(self):
        h = "/sys/class/hwmon/hwmon0"
        write(self.root, h + "/name", "chip\n")
        os.makedirs(self.root + h + "/temp1_input")               # open() даёт IsADirectoryError
        write(self.root, h + "/temp2_input", "9" * (agent.MAX_TEXT + 1))  # длиннее потолка
        write(self.root, h + "/temp3_input", "9" * agent.MAX_TEXT)        # ровно по потолку
        s = agent.build_slice(self.env())
        self.assertNotIn(h + "/temp1_input", s["files"])
        self.assertNotIn(h + "/temp2_input", s["files"])
        self.assertIn(h + "/temp3_input", s["files"])
        self.assertEqual(s["files"][h + "/name"], "chip\n")

    def test_files_capped_at_4096(self):
        for d in range(60):
            for t in range(1, 80):
                write(self.root, "/sys/class/hwmon/hwmon%d/temp%d_input" % (d, t), "1\n")
            write(self.root, "/sys/class/hwmon/hwmon%d/name" % d, "x\n")
        s = agent.build_slice(self.env())
        self.assertLessEqual(len(s["files"]), agent.MAX_FILES)

    def test_no_sysfs_gives_empty(self):
        s = agent.build_slice(self.env())
        self.assertEqual(s["links"], {})
        self.assertEqual([k for k in s["files"] if k.startswith("/sys/")], [])


class ProcTests(TmpRoot):
    def test_proc_files(self):
        write(self.root, "/proc/meminfo", "MemTotal:  1 kB\n")
        write(self.root, "/proc/uptime", "123.4 567.8\n")
        write(self.root, "/proc/stat", "cpu  1 2 3 4\ncpu0 1 1 1 1\nintr 5\n")
        s = agent.build_slice(self.env())
        self.assertEqual(s["files"]["/proc/meminfo"], "MemTotal:  1 kB\n")
        self.assertEqual(s["files"]["/proc/uptime"], "123.4 567.8\n")
        self.assertEqual(s["files"]["/proc/stat"], "cpu  1 2 3 4\n")

    def test_stat_without_trailing_newline_gets_one(self):
        write(self.root, "/proc/stat", "cpu  1 2 3 4")
        self.assertEqual(agent.build_slice(self.env())["files"]["/proc/stat"], "cpu  1 2 3 4\n")

    def test_mountinfo_filtered_to_dev_sources(self):
        write(self.root, "/proc/self/mountinfo",
              "29 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw\n"
              "30 29 0:25 / /sys rw - sysfs sysfs rw\n"
              "31 29 0:26 / /var/lib/docker/overlay2/x rw - overlay overlay rw,lowerdir=/a\n"
              "32 29 8:1 / /mnt/my\\040disk rw master:2 shared:5 - ext4 /dev/sda1 rw\n"
              "33 29 0:27 / /dev/shm rw - tmpfs tmpfs rw\n")
        s = agent.build_slice(self.env())
        lines = s["files"]["/proc/self/mountinfo"].splitlines()
        self.assertEqual(len(lines), 2)
        self.assertTrue(lines[0].startswith("29 1 "))
        self.assertTrue(lines[1].startswith("32 29 "))
        self.assertEqual([m["mount"] for m in s["statvfs"]], ["/", "/mnt/my\\040disk"])

    def test_statvfs_math_and_dedup(self):
        write(self.root, "/proc/self/mountinfo",
              "29 1 259:2 / / rw - ext4 /dev/a rw\n"
              "40 29 259:2 /usr /usr ro - ext4 /dev/a rw\n"
              "41 29 259:2 /usr /usr ro - ext4 /dev/a rw\n")
        s = agent.build_slice(self.env())
        self.assertEqual(s["statvfs"], [{"mount": "/", "total": 4096000, "free": 1024000},
                                        {"mount": "/usr", "total": 4096000, "free": 1024000}])

    def test_statvfs_unescaped_path_and_failure_skipped(self):
        seen = []

        def st(path):
            seen.append(path)
            if path == "/bad":
                raise OSError("нет")
            return fake_statvfs(path)

        write(self.root, "/proc/self/mountinfo",
              "1 1 1:1 / /mnt/my\\040disk rw - ext4 /dev/a rw\n2 1 1:2 / /bad rw - ext4 /dev/b rw\n")
        env = agent.Env(root=self.root, statvfs=st)
        s = agent.build_slice(env)
        self.assertEqual(seen, ["/mnt/my disk", "/bad"])
        self.assertEqual([m["mount"] for m in s["statvfs"]], ["/mnt/my\\040disk"])

    def test_mountinfo_over_64k_after_filter_dropped_whole(self):
        line = "1 1 1:1 / /m/%s rw - ext4 /dev/a rw\n"
        write(self.root, "/proc/self/mountinfo", "".join(line % ("x" * 200 + str(i)) for i in range(400)))
        s = agent.build_slice(self.env())
        self.assertNotIn("/proc/self/mountinfo", s["files"])
        self.assertEqual(s["statvfs"], [])

    def test_statvfs_capped_at_64(self):
        write(self.root, "/proc/self/mountinfo",
              "".join("%d 1 1:1 / /m%d rw - ext4 /dev/a rw\n" % (i, i) for i in range(100)))
        self.assertEqual(len(agent.build_slice(self.env())["statvfs"]), 64)


class SmiTests(TmpRoot):
    def test_exact_arguments_and_no_shell(self):
        args_file = os.path.join(self.bin, "args.txt")
        smi = fake_smi(self.bin, 'for a in "$@"; do echo "$a" >> %s; done\necho "0, GPU-1, RTX"\n' % args_file)
        s = agent.build_slice(self.env(smi))
        self.assertEqual(s["gpuCsv"], "0, GPU-1, RTX\n")
        with open(args_file) as f:
            argv = f.read().splitlines()
        self.assertEqual(argv, [
            "--query-gpu=index,uuid,name,utilization.gpu,utilization.memory,memory.used,memory.total,"
            "temperature.gpu,fan.speed,clocks.sm,clocks.max.sm,clocks.mem,pstate,pcie.link.gen.current,"
            "pcie.link.width.current,power.draw,power.limit,clocks_event_reasons.sw_power_cap,"
            "clocks_event_reasons.hw_thermal_slowdown,clocks_event_reasons.sw_thermal_slowdown,pci.bus_id",
            "--format=csv,noheader,nounits"])
        self.assertFalse(any("compute-apps" in a for a in argv))

    def test_query_matches_backend_gpusampler(self):
        # Сверка с источником правды: тот же запрос, что в C#-GpuSampler.QueryArgs.
        cs = os.path.join(_HERE, "..", "..", "..", "backend", "ClaudeHomeServer.Llm", "LocalStats", "GpuSampler.cs")
        if not os.path.exists(cs):
            self.skipTest("исходники бэкенда недоступны")
        with open(cs, encoding="utf-8") as f:
            text = f.read()
        start = text.index("QueryArgs =")
        chunk = text[start:text.index(";", start)]
        import re
        pieces = re.findall(r'"([^"]*)"', chunk)
        self.assertEqual("".join(pieces), agent.SMI_QUERY + " " + agent.SMI_FORMAT)

    def test_nonzero_exit_gives_null(self):
        smi = fake_smi(self.bin, "echo ошибка\nexit 9\n")
        self.assertIsNone(agent.build_slice(self.env(smi))["gpuCsv"])

    def test_missing_binary_gives_null(self):
        self.assertIsNone(agent.build_slice(self.env(os.path.join(self.bin, "нет-такого")))["gpuCsv"])
        self.assertIsNone(agent.build_slice(self.env(None))["gpuCsv"])

    def test_hung_nvidia_smi(self):
        smi = fake_smi(self.bin, "sleep 5\necho 0, GPU-1\n")
        t0 = time.monotonic()
        s = agent.build_slice(self.env(smi))
        elapsed = time.monotonic() - t0
        self.assertIsNone(s["gpuCsv"])
        self.assertLessEqual(elapsed, 1.5)
        self.assertEqual(set(s) | {"ageMs"}, TOP_KEYS)  # остальной срез собран

    def test_rows_capped_and_long_rows_dropped(self):
        rows = "".join("%d, GPU-%d\n" % (i, i) for i in range(20))
        smi = fake_smi(self.bin, "cat <<'EOF'\n%sEOF\n" % rows)
        out = agent.build_slice(self.env(smi))["gpuCsv"].splitlines()
        self.assertEqual(len(out), 16)
        self.assertEqual(out[0], "0, GPU-0")
        smi2 = fake_smi(self.bin, "echo 0, ok\necho %s\n" % ("x" * 5000), name="smi2")
        self.assertEqual(agent.build_slice(self.env(smi2))["gpuCsv"], "0, ok\n")


class Gddr6Tests(TmpRoot):
    def test_present_verbatim(self):
        write(self.root, "/run/ccs-gddr6/temps.json", '{"at": 1, "gpus": []}\n')
        self.assertEqual(agent.build_slice(self.env())["gddr6"], '{"at": 1, "gpus": []}\n')

    def test_absent_and_oversized_null(self):
        self.assertIsNone(agent.build_slice(self.env())["gddr6"])
        write(self.root, "/run/ccs-gddr6/temps.json", "x" * (agent.MAX_GDDR6 + 1))
        self.assertIsNone(agent.build_slice(self.env())["gddr6"])


class SliceShapeTests(TmpRoot):
    def test_top_level_keys_exact(self):
        s = agent.build_slice(self.env())
        store = agent.SliceStore()
        store.put(s)
        data, code = agent.render_body(store)
        self.assertEqual(code, 200)
        body = json.loads(data)
        self.assertEqual(set(body), TOP_KEYS)
        self.assertEqual(body["v"], 1)
        self.assertEqual(list(body)[:2], ["v", "ageMs"])
        self.assertIsInstance(body["clockMs"], int)
        self.assertLess(abs(body["clockMs"] - time.time() * 1000), 5000)

    def test_age_is_monotonic_since_ready(self):
        store = agent.SliceStore()
        store.put({"v": 1, "x": 1}, ready_at=100.0)
        data, _ = agent.render_body(store, now=107.5)
        self.assertEqual(json.loads(data)["ageMs"], 7500)

    def test_oversized_body_is_500(self):
        store = agent.SliceStore()
        store.put({"v": 1, "files": {"/a": "x" * (agent.MAX_BODY + 10)}})
        self.assertEqual(agent.render_body(store), (None, 500))

    def test_no_slice_is_503(self):
        self.assertEqual(agent.render_body(agent.SliceStore()), (None, 503))


class CollectorLoopTests(TmpRoot):
    def test_loop_survives_failures_and_keeps_last_slice(self):
        store = agent.SliceStore()
        stop = threading.Event()
        calls = []
        done = threading.Event()

        def build(env):
            calls.append(1)
            if len(calls) == 2:
                raise RuntimeError("сбой сборки")
            if len(calls) >= 4:
                done.set()
            return {"v": 1, "n": len(calls)}

        t = threading.Thread(target=agent.run_collector, args=(store, self.env(), 0.01, stop, build))
        t.start()
        self.assertTrue(done.wait(5))
        stop.set()
        t.join(2)
        self.assertFalse(t.is_alive())
        self.assertGreaterEqual(len(calls), 4)
        self.assertGreaterEqual(store.get()[0]["n"], 4)


class ServerTests(TmpRoot):
    def start(self, allow=("127.0.0.1",), store=None, handler_timeout=None):
        store = store or agent.SliceStore()
        handler = agent.make_handler(store, frozenset(__import__("ipaddress").ip_address(a) for a in allow))
        if handler_timeout is not None:
            handler.timeout = handler_timeout
        server = agent.HTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(lambda: (server.shutdown(), server.server_close()))
        return server.server_address[1], store

    def request(self, port, method="GET", path="/v1/slice"):
        c = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
        try:
            c.request(method, path)
            r = c.getresponse()
            return r.status, r.read(), r
        finally:
            c.close()

    def test_503_until_first_slice_then_200(self):
        port, store = self.start()
        self.assertEqual(self.request(port)[0], 503)
        store.put(agent.build_slice(self.env()))
        status, body, resp = self.request(port)
        self.assertEqual(status, 200)
        self.assertEqual(resp.getheader("Content-Type"), "application/json; charset=utf-8")
        self.assertEqual(set(json.loads(body)), TOP_KEYS)

    def test_age_grows_without_new_slice(self):
        port, store = self.start()
        store.put({"v": 1}, ready_at=time.monotonic() - 12.0)
        age = json.loads(self.request(port)[1])["ageMs"]
        self.assertGreaterEqual(age, 12000)

    def test_unknown_path_404_and_query_ignored(self):
        port, store = self.start()
        store.put({"v": 1})
        self.assertEqual(self.request(port, path="/")[0], 404)
        self.assertEqual(self.request(port, path="/v1/slice/x")[0], 404)
        self.assertEqual(self.request(port, path="/v1/slice?a=1&path=/etc/shadow")[0], 200)

    def test_other_methods_405(self):
        port, store = self.start()
        store.put({"v": 1})
        for m in ("POST", "PUT", "DELETE", "PATCH", "OPTIONS"):
            status, _, resp = self.request(port, m)
            self.assertEqual(status, 405, m)
            self.assertEqual(resp.getheader("Allow"), "GET")

    def test_ip_outside_allow_list_403_without_body(self):
        port, store = self.start(allow=("10.9.9.9",))
        store.put({"v": 1})
        for path in ("/v1/slice", "/nope"):
            status, body, _ = self.request(port, path=path)
            self.assertEqual((status, body), (403, b""))

    def test_no_cors_headers(self):
        port, store = self.start()
        store.put({"v": 1})
        _, _, resp = self.request(port)
        self.assertIsNone(resp.getheader("Access-Control-Allow-Origin"))

    def test_slow_client_does_not_block_server(self):
        port, store = self.start(handler_timeout=0.3)
        store.put({"v": 1})
        hog = socket.create_connection(("127.0.0.1", port))
        self.addCleanup(hog.close)
        t0 = time.monotonic()
        self.assertEqual(self.request(port)[0], 200)   # ждёт, пока истечёт тайм-аут молчуна
        self.assertLess(time.monotonic() - t0, 3)

    def test_two_concurrent_requests_both_served(self):
        port, store = self.start()
        store.put({"v": 1})
        results = []
        ts = [threading.Thread(target=lambda: results.append(self.request(port)[0])) for _ in range(2)]
        for t in ts:
            t.start()
        for t in ts:
            t.join(5)
        self.assertEqual(results, [200, 200])


class ConfigTests(TmpRoot):
    def cfg(self, obj):
        path = os.path.join(self.root, "cfg.json")
        with open(path, "w") as f:
            f.write(obj if isinstance(obj, str) else json.dumps(obj))
        return path

    def test_valid(self):
        host, port, allow = agent.load_config(self.cfg({"listen": "10.0.0.11:9105", "allow": ["10.0.0.5"]}))
        self.assertEqual((host, port), ("10.0.0.11", 9105))
        self.assertEqual({str(a) for a in allow}, {"10.0.0.5"})

    def test_refuses_to_start_without_address_or_allow_list(self):
        bad = [
            {"allow": ["10.0.0.5"]},
            {"listen": "10.0.0.11:9105"},
            {"listen": "10.0.0.11:9105", "allow": []},
            {"listen": "0.0.0.0:9105", "allow": ["10.0.0.5"]},
            {"listen": "10.0.0.11", "allow": ["10.0.0.5"]},
            {"listen": "10.0.0.11:0", "allow": ["10.0.0.5"]},
            {"listen": "10.0.0.11:9105", "allow": ["не-ip"]},
            [],
            "{не json",
        ]
        for obj in bad:
            with self.assertRaises(agent.ConfigError, msg=repr(obj)):
                agent.load_config(self.cfg(obj))
        with self.assertRaises(agent.ConfigError):
            agent.load_config(os.path.join(self.root, "нет.json"))


if __name__ == "__main__":
    unittest.main()
