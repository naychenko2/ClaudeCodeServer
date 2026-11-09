"""Тесты помощника без железа: /sys и /dev/mem подменяются временными файлами.

Запуск: python3 -m unittest deploy/gddr6-helper/test_ccs_gddr6_helper.py
"""
import importlib.util
import json
import os
import struct
import tempfile
import threading
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "helper", os.path.join(_HERE, "ccs-gddr6-helper.py"))
helper = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(helper)


def read_json(path):
    with open(path) as f:
        return json.load(f)


def make_sysfs(root, bus_id, vendor, device, resource_first):
    d = os.path.join(root, bus_id)
    os.makedirs(d)
    for name, text in (("vendor", "0x%04x\n" % vendor), ("device", "0x%04x\n" % device),
                       ("resource", resource_first + "\n0x0 0x0 0x0\n")):
        with open(os.path.join(d, name), "w") as f:
            f.write(text)


def make_fake_mem(path, regs):
    """Файл-подмена /dev/mem: {физический адрес: значение u32}."""
    size = max(regs) + 8192
    with open(path, "wb") as f:
        f.truncate(size)
        for addr, val in regs.items():
            f.seek(addr)
            f.write(struct.pack("<I", val))


class DecodeTests(unittest.TestCase):
    def test_mem_temp_known_pairs(self):
        # (raw & 0xfff) // 0x20: 68 °C -> 0x880, 82 °C -> 0xA40; старшие биты отбрасываются
        self.assertEqual(helper.decode_mem_temp(0x880), 68)
        self.assertEqual(helper.decode_mem_temp(0xA40), 82)
        self.assertEqual(helper.decode_mem_temp(0xFFFFF880), 68)
        self.assertEqual(helper.decode_mem_temp(0x88F), 68)

    def test_hotspot_known_pairs(self):
        # (raw >> 8) & 0xff
        self.assertEqual(helper.decode_hotspot(0x4B00), 75)
        self.assertEqual(helper.decode_hotspot(0xAB4C12), 0x4C)
        self.assertEqual(helper.decode_hotspot(0x4C00), 76)

    def test_sanitize_range(self):
        self.assertIsNone(helper.sanitize(0))
        self.assertIsNone(helper.sanitize(151))
        self.assertIsNone(helper.sanitize(None))
        self.assertEqual(helper.sanitize(1), 1)
        self.assertEqual(helper.sanitize(150), 150)


class ResourceTests(unittest.TestCase):
    def test_parse_bar0(self):
        self.assertEqual(helper.parse_bar0(
            "0x00000000fb000000 0x00000000fbffffff 0x0000000000040200\n0x0 0x0 0x0\n"), 0xFB000000)

    def test_parse_bar0_unassigned(self):
        self.assertIsNone(helper.parse_bar0("0x0000000000000000 0x0000000000000000 0x0\n"))

    def test_parse_bar0_garbage(self):
        with self.assertRaises(ValueError):
            helper.parse_bar0("мусор мусор\n")


class FindGpusTests(unittest.TestCase):
    def test_only_whitelisted_nvidia(self):
        with tempfile.TemporaryDirectory() as root:
            make_sysfs(root, "0000:0b:00.0", 0x10DE, 0x2204, "0x00000000fb000000 0xfbffffff 0x40200")
            make_sysfs(root, "0000:0c:00.0", 0x10DE, 0x2204, "0x00000000f9000000 0xf9ffffff 0x40200")
            make_sysfs(root, "0000:0d:00.0", 0x10DE, 0x2684, "0x00000000f8000000 0xf8ffffff 0x40200")
            make_sysfs(root, "0000:00:02.0", 0x8086, 0x2204, "0x00000000f7000000 0xf7ffffff 0x40200")
            self.assertEqual(helper.find_gpus(root), [
                ("0000:0b:00.0", 0x2204, 0xFB000000),
                ("0000:0c:00.0", 0x2204, 0xF9000000),
            ])

    def test_broken_device_is_skipped(self):
        with tempfile.TemporaryDirectory() as root:
            make_sysfs(root, "0000:0b:00.0", 0x10DE, 0x2204, "мусор")
            make_sysfs(root, "0000:0c:00.0", 0x10DE, 0x2204, "0x0 0x0 0x0")
            make_sysfs(root, "0000:0d:00.0", 0x10DE, 0x2204, "0x00000000f9000000 0xf9ffffff 0x40200")
            self.assertEqual(helper.find_gpus(root), [("0000:0d:00.0", 0x2204, 0xF9000000)])


class CollectTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.sysfs = os.path.join(self.tmp.name, "sys")
        os.makedirs(self.sysfs)
        self.mem = os.path.join(self.tmp.name, "mem")

    def test_reads_two_cards(self):
        # BAR0 в подмене малые, чтобы файл был небольшим
        make_sysfs(self.sysfs, "0000:0b:00.0", 0x10DE, 0x2204, "0x0000000000010000 0x0 0x0")
        make_sysfs(self.sysfs, "0000:0c:00.0", 0x10DE, 0x2204, "0x0000000000030000 0x0 0x0")
        make_fake_mem(self.mem, {
            0x10000 + helper.MEM_TEMP_OFFSET: 0x880, 0x10000 + helper.HOTSPOT_OFFSET: 0x4B00,
            0x30000 + helper.MEM_TEMP_OFFSET: 0xA40, 0x30000 + helper.HOTSPOT_OFFSET: 0x4C00,
        })
        fd = os.open(self.mem, os.O_RDONLY)
        self.addCleanup(os.close, fd)
        data = helper.collect(fd, self.sysfs, now_ms=1234)
        self.assertEqual(data, {"v": 1, "at": 1234, "gpus": [
            {"busId": "0000:0b:00.0", "deviceId": "0x2204", "memTempC": 68, "hotspotTempC": 75},
            {"busId": "0000:0c:00.0", "deviceId": "0x2204", "memTempC": 82, "hotspotTempC": 76},
        ]})

    def test_garbage_value_is_null_and_does_not_affect_other_card(self):
        make_sysfs(self.sysfs, "0000:0b:00.0", 0x10DE, 0x2204, "0x0000000000010000 0x0 0x0")
        make_sysfs(self.sysfs, "0000:0c:00.0", 0x10DE, 0x2204, "0x0000000000030000 0x0 0x0")
        make_fake_mem(self.mem, {
            0x10000 + helper.MEM_TEMP_OFFSET: 0xFFFFFFFF,  # (0xfff // 0x20) = 127 — в допустимом диапазоне
            0x10000 + helper.HOTSPOT_OFFSET: 0x0000,       # 0 °C — вне диапазона, должно стать null
            0x30000 + helper.MEM_TEMP_OFFSET: 0x880, 0x30000 + helper.HOTSPOT_OFFSET: 0x4B00,
        })
        fd = os.open(self.mem, os.O_RDONLY)
        self.addCleanup(os.close, fd)
        gpus = helper.collect(fd, self.sysfs)["gpus"]
        self.assertEqual(gpus[0]["memTempC"], 127)
        self.assertIsNone(gpus[0]["hotspotTempC"])
        self.assertEqual((gpus[1]["memTempC"], gpus[1]["hotspotTempC"]), (68, 75))

    def test_unreadable_register_is_null(self):
        # BAR0 за концом файла-подмены: mmap падает, служба живёт, значения null
        make_sysfs(self.sysfs, "0000:0b:00.0", 0x10DE, 0x2204, "0x0000000040000000 0x0 0x0")
        make_fake_mem(self.mem, {0x1000: 1})
        fd = os.open(self.mem, os.O_RDONLY)
        self.addCleanup(os.close, fd)
        gpus = helper.collect(fd, self.sysfs)["gpus"]
        self.assertEqual(gpus[0]["memTempC"], None)
        self.assertEqual(gpus[0]["hotspotTempC"], None)

    def test_no_mem_device_gives_nulls(self):
        make_sysfs(self.sysfs, "0000:0b:00.0", 0x10DE, 0x2204, "0x0000000000010000 0x0 0x0")
        gpus = helper.collect(None, self.sysfs)["gpus"]
        self.assertEqual((gpus[0]["memTempC"], gpus[0]["hotspotTempC"]), (None, None))


class WriteTests(unittest.TestCase):
    def test_atomic_write_mode_and_content(self):
        with tempfile.TemporaryDirectory() as out:
            helper.write_atomic({"v": 1, "at": 5, "gpus": []}, out)
            path = os.path.join(out, "temps.json")
            self.assertEqual(os.stat(path).st_mode & 0o777, 0o644)
            self.assertEqual(read_json(path), {"v": 1, "at": 5, "gpus": []})
            self.assertEqual(os.listdir(out), ["temps.json"])  # временного файла не осталось

    def test_run_stops_on_event(self):
        with tempfile.TemporaryDirectory() as root:
            sysfs, out = os.path.join(root, "sys"), os.path.join(root, "out")
            os.makedirs(sysfs)
            os.makedirs(out)
            stop = threading.Event()
            t = threading.Thread(target=helper.run, args=(stop, os.path.join(root, "нет-mem"), sysfs, out, 0.05))
            t.start()
            for _ in range(100):
                if os.path.exists(os.path.join(out, "temps.json")):
                    break
                stop.wait(0.05)
            stop.set()
            t.join(2)
            self.assertFalse(t.is_alive())
            self.assertEqual(read_json(os.path.join(out, "temps.json"))["gpus"], [])


if __name__ == "__main__":
    unittest.main()
