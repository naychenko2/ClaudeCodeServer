# coding: utf-8
"""Тесты защит воркера LoRA и узла: без GPU и без ComfyUI (тяжёлые модули подменены заглушками)."""
import importlib.util
import os
import sys
import tempfile
import types
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))


def _load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


w = _load("worker_lora", os.path.join(HERE, "worker_lora.py"))

# узел импортирует модули ComfyUI — подставляем пустые заглушки только на время загрузки
_stubs = {"comfy": types.ModuleType("comfy"), "comfy.model_management": types.ModuleType("mm"),
          "folder_paths": types.ModuleType("folder_paths")}
_saved = {k: sys.modules.get(k) for k in _stubs}
sys.modules.update(_stubs)
try:
    node = _load("ccs_audio_worker", os.path.join(HERE, "..", "audio", "ccs_audio_worker", "__init__.py"))
finally:
    for k, v in _saved.items():
        if v is None:
            sys.modules.pop(k, None)
        else:
            sys.modules[k] = v

OWNER = "a" * 32
RUN = "b" * 12


class FakeJob:
    def __init__(self, input_dir, out_dir, loras_root, params):
        self.input_dir, self.out_dir, self.loras_root, self.params = input_dir, out_dir, loras_root, params


class InsideTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = os.path.join(self.tmp.name, "root")
        os.makedirs(os.path.join(self.root, "sub"))
        self.outside = os.path.join(self.tmp.name, "outside")
        os.makedirs(self.outside)

    def tearDown(self):
        self.tmp.cleanup()

    def test_inside_and_outside(self):
        self.assertTrue(w._inside(os.path.join(self.root, "sub"), self.root))
        self.assertFalse(w._inside(self.root, self.root))
        self.assertFalse(w._inside(self.outside, self.root))
        self.assertFalse(w._inside(self.root + "-evil", self.root))

    def test_dotdot(self):
        self.assertFalse(w._inside(os.path.join(self.root, "sub", "..", "..", "outside"), self.root))

    def test_symlink_escape(self):
        link = os.path.join(self.root, "link")
        os.symlink(self.outside, link)
        self.assertFalse(w._inside(os.path.join(link, "x"), self.root))


class TailNoPathsTests(unittest.TestCase):
    def test_path_replaced_progress_kept(self):
        lines = ["CCS_RESULT {}", "ошибка в /home/an/ai-data/loras/ccs/x/file.py тут",
                 "name:  87%|####| 1750/2000 [..]", "Step 1750"]
        out = node._tail_no_paths(lines)
        self.assertIn("<path>", out)
        self.assertNotIn("/home/an", out)
        self.assertNotIn("CCS_RESULT", out)
        self.assertIn("1750/2000", out)
        self.assertIn("Step 1750", out)


class RunDirsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        t = self.tmp.name
        self.inp = os.path.join(t, "input")
        self.loras = os.path.join(t, "loras")
        self.in_dir = os.path.join(self.inp, "ccs", OWNER, RUN)
        self.out_dir = os.path.join(self.loras, "ccs", OWNER, f"hero-{RUN}")
        os.makedirs(self.in_dir)
        os.makedirs(self.out_dir)
        self.params = {"owner": OWNER, "slug": "hero", "run_id": RUN}

    def tearDown(self):
        self.tmp.cleanup()

    def job(self, out_dir=None):
        return FakeJob(self.inp, out_dir or self.out_dir, self.loras, dict(self.params))

    def test_ok(self):
        self.assertEqual(w._run_dirs(self.job()), (OWNER, "hero", RUN, self.in_dir))

    def test_out_dir_outside_loras_root(self):
        bad = os.path.join(self.tmp.name, "elsewhere", "ccs", OWNER, f"hero-{RUN}")
        os.makedirs(bad)
        with self.assertRaises(SystemExit):
            w._run_dirs(self.job(bad))

    def test_out_dir_symlink(self):
        shutil_target = os.path.join(self.loras, "real")
        os.makedirs(shutil_target)
        os.rmdir(self.out_dir)
        os.symlink(shutil_target, self.out_dir)
        with self.assertRaises(SystemExit):
            w._run_dirs(self.job())

    def test_in_dir_symlink(self):
        os.rmdir(self.in_dir)
        os.symlink(self.tmp.name, self.in_dir)
        with self.assertRaises(SystemExit):
            w._run_dirs(self.job())


class CheckRegexTests(unittest.TestCase):
    def rejects(self, value, regex):
        with self.assertRaises(SystemExit):
            w._check(value, regex, "x")

    def test_owner(self):
        self.assertEqual(w._check(OWNER, w._RE_OWNER, "o"), OWNER)
        for bad in ("a" * 31, "a" * 33, "A" * 32, OWNER + "\n", None, 5):
            self.rejects(bad, w._RE_OWNER)

    def test_run_id(self):
        self.assertEqual(w._check(RUN, w._RE_RUN, "r"), RUN)
        for bad in ("b" * 11, "b" * 13, RUN + "\n", "../" + "b" * 9):
            self.rejects(bad, w._RE_RUN)

    def test_trigger(self):
        self.assertEqual(w._check("hero_x", w._RE_TRIGGER, "t"), "hero_x")
        for bad in ("hero_x\n", "Hero", "ab", "1abc", "a" * 25, "a b c"):
            self.rejects(bad, w._RE_TRIGGER)

    def test_photo_names(self):
        self.assertTrue(w._RE_PHOTO.match("0123456789abcdef.jpg"))
        for bad in ("0123456789abcdef.jpg\n", "../0123456789abcdef.jpg", "0123456789abcdef.gif",
                    "0123456789abcde.jpg", "0123456789abcdef.jpg.exe"):
            self.assertIsNone(w._RE_PHOTO.match(bad), bad)



class PlanStepsTests(unittest.TestCase):
    """Тестовый потолок шагов не должен доставаться настоящему прогону (инцидент 10.10:
    прогон владельца, вставший в очередь в окно проверки, обучился 100 шагов вместо 2000)."""

    STANDARD = {"steps": 2000, "checkpoint": 1750}
    TEST, REAL = "0" * 28 + "beef", "61a7aec3aa8d44fc86dc4022a09010be"

    def test_no_cap(self):
        self.assertEqual(w._plan_steps(self.STANDARD, self.REAL, {}), (2000, 1750))

    def test_cap_applies_to_test_owner(self):
        env = {"CCS_LORA_MAX_STEPS": "100", "CCS_LORA_TEST_OWNER": self.TEST}
        self.assertEqual(w._plan_steps(self.STANDARD, self.TEST, env), (100, None))

    def test_cap_never_applies_to_real_owner(self):
        env = {"CCS_LORA_MAX_STEPS": "100", "CCS_LORA_TEST_OWNER": self.TEST}
        self.assertEqual(w._plan_steps(self.STANDARD, self.REAL, env), (2000, 1750))

    def test_cap_without_test_owner_ignored(self):
        self.assertEqual(w._plan_steps(self.STANDARD, self.REAL, {"CCS_LORA_MAX_STEPS": "100"}), (2000, 1750))


if __name__ == "__main__":
    unittest.main()
