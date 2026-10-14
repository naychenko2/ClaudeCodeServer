# coding: utf-8
"""Тесты протокола прогресса аудио-воркеров: без GPU и без ComfyUI (модули ComfyUI подменены заглушками).

Запуск: python3 deploy/comfyui/audio/test_progress.py
"""
import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import textwrap
import types
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
WORKERS = os.path.join(HERE, "workers")
sys.path.insert(0, WORKERS)

import ccs_common  # noqa: E402
import worker_rvc  # noqa: E402


class FakeBar:
    created = []

    def __init__(self, total):
        self.total = total
        self.updates = []
        FakeBar.created.append(self)

    def update_absolute(self, value, total=None):
        self.updates.append((value, total))


_tmp = tempfile.TemporaryDirectory()
INPUT_DIR = os.path.join(_tmp.name, "input")
OUTPUT_DIR = os.path.join(_tmp.name, "output")
os.makedirs(INPUT_DIR)
os.makedirs(OUTPUT_DIR)

_mm = types.ModuleType("comfy.model_management")
_mm.free_memory = lambda *a, **k: None
_mm.get_torch_device = lambda: "cpu"
_mm.unload_all_models = lambda: None
_mm.soft_empty_cache = lambda **k: None
_mm.throw_exception_if_processing_interrupted = lambda: None
_utils = types.ModuleType("comfy.utils")
_utils.ProgressBar = FakeBar
_comfy = types.ModuleType("comfy")
_comfy.model_management, _comfy.utils = _mm, _utils
_fp = types.ModuleType("folder_paths")
_fp.get_input_directory = lambda: INPUT_DIR
_fp.get_output_directory = lambda: OUTPUT_DIR
_STUBS = {"comfy": _comfy, "comfy.model_management": _mm, "comfy.utils": _utils, "folder_paths": _fp}
_saved = {}


def setUpModule():
    _saved.update({k: sys.modules.get(k) for k in _STUBS})
    sys.modules.update(_STUBS)


def tearDownModule():
    for k, v in _saved.items():
        if v is None:
            sys.modules.pop(k, None)
        else:
            sys.modules[k] = v
    _tmp.cleanup()


def load_node():
    saved = {k: sys.modules.get(k) for k in _STUBS}
    sys.modules.update(_STUBS)
    try:
        spec = importlib.util.spec_from_file_location("ccs_audio_worker", os.path.join(HERE, "ccs_audio_worker", "__init__.py"))
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        return mod
    finally:
        for k, v in saved.items():
            if v is None:
                sys.modules.pop(k, None)
            else:
                sys.modules[k] = v


node = load_node()


def emitted(*args):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        ccs_common.emit_progress(*args)
    return out.getvalue()


class EmitProgressTests(unittest.TestCase):
    def test_line_of_protocol(self):
        line = emitted(2, 5)
        self.assertTrue(line.startswith("CCS_PROGRESS "))
        self.assertEqual(json.loads(line[len("CCS_PROGRESS "):]), {"step": 2, "total": 5})

    def test_step_is_clamped_and_floored(self):
        self.assertIn('"step": 5', emitted(9, 5))
        self.assertIn('"step": 0', emitted(-3, 5))
        self.assertIn('"step": 12', emitted(12.9, 30))

    def test_garbage_is_silent(self):
        self.assertEqual(emitted(1, 0), "")
        self.assertEqual(emitted("x", 5), "")
        self.assertEqual(emitted(1, None), "")

    def test_closed_stdout_does_not_fail(self):
        closed = io.StringIO()
        closed.close()
        with contextlib.redirect_stdout(closed):
            ccs_common.emit_progress(1, 2)

    def test_node_understands_worker(self):
        p = node._Progress()
        self.assertTrue(p.feed(emitted(3, 7).rstrip("\n")))
        self.assertEqual(p.last, (3, 7))


class NodeProgressTests(unittest.TestCase):
    def setUp(self):
        FakeBar.created.clear()

    def test_feed_consumes_only_progress_lines(self):
        p = node._Progress()
        self.assertFalse(p.feed("обычная строка лога"))
        self.assertFalse(p.feed('CCS_RESULT {"files": []}'))
        self.assertTrue(p.feed("CCS_PROGRESS не json"))
        self.assertTrue(p.feed('CCS_PROGRESS {"step": 1}'))
        self.assertTrue(p.feed('CCS_PROGRESS {"step": 1, "total": 0}'))
        self.assertIsNone(p.last)
        self.assertTrue(p.feed('CCS_PROGRESS {"step": 4, "total": 10}'))
        self.assertEqual(p.last, (4, 10))

    def test_report_without_progress_creates_no_bar(self):
        node._Progress().report()
        self.assertEqual(FakeBar.created, [])

    def test_report_updates_once_per_step(self):
        p = node._Progress()
        p.feed('CCS_PROGRESS {"step": 1, "total": 3}')
        p.report()
        p.report()
        p.feed('CCS_PROGRESS {"step": 2, "total": 3}')
        p.report()
        self.assertEqual(len(FakeBar.created), 1)
        self.assertEqual(FakeBar.created[0].updates, [(1, 3), (2, 3)])


FAKE_WORKER = """
import sys, time
sys.path.insert(0, {workers!r})
from ccs_common import emit_progress, load_job
job = load_job()
for k in range(1, 4) if job.params.get("progress") else []:
    emit_progress(k, 3)
    time.sleep(0.7)
print("обычная строка", flush=True)
with open(job.out("speech.wav"), "w") as f:
    f.write("x")
job.done()
"""


class RunWithWorkerTests(unittest.TestCase):
    """Узел целиком: настоящий процесс воркера, заглушки только у ComfyUI."""

    def setUp(self):
        FakeBar.created.clear()
        self.dir = tempfile.TemporaryDirectory()
        script = os.path.join(self.dir.name, "worker_fake.py")
        with open(script, "w", encoding="utf-8") as f:
            f.write(FAKE_WORKER.format(workers=WORKERS))
        self._config = node._config
        node._config = lambda: {"families": {"tts": {"python": sys.executable, "script": script}}}

    def tearDown(self):
        node._config = self._config
        self.dir.cleanup()

    def run_node(self, params):
        return node.CcsAudioWorker().run("tts", json.dumps(params), "", "t", 1)

    def test_steps_reach_progress_bar(self):
        res = self.run_node({"progress": True})
        self.assertEqual(res["ui"]["audio"][0]["filename"], "t_speech.wav")
        self.assertEqual(len(FakeBar.created), 1)
        updates = FakeBar.created[0].updates
        # опрос раз в 0,5 с может пропустить шаг, но последний доходит всегда
        self.assertEqual(updates[-1], (3, 3))
        self.assertEqual(updates, sorted(set(updates)))

    def test_worker_without_progress_works_as_before(self):
        res = self.run_node({})
        self.assertEqual(res["ui"]["audio"][0]["filename"], "t_speech.wav")
        self.assertIn("ccs_stats", res["ui"])
        self.assertEqual(FakeBar.created, [])


class RvcEpochTests(unittest.TestCase):
    def test_epoch_record_of_applio(self):
        f = worker_rvc.epoch_done
        self.assertEqual(f("v", "v | epoch=1 | step=40 | 0:00:31"), 1)
        self.assertEqual(f("v", "v | epoch=12 | step=480 | 0:00:30 | lowest_value=21.3 (epoch 9 and step 360)"), 12)
        self.assertIsNone(f("v", "v | epoch=12 | step=470 | batch=30/40"))
        self.assertIsNone(f("v", "vv | epoch=12 | step=480 | 0:00:30"))
        self.assertIsNone(f("v", "Starting training..."))

    def test_run_forwards_lines_and_fails_like_before(self):
        saved = worker_rvc.CORE
        seen = []
        try:
            worker_rvc.CORE = [sys.executable, "-c", textwrap.dedent("""
                import sys
                print("v | epoch=1 | step=4 | 0:00:01", flush=True)
                sys.exit(int(sys.argv[1]))
            """)]
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                worker_rvc.run(["0"], seen.append)
            self.assertEqual(seen, ["v | epoch=1 | step=4 | 0:00:01\n"])
            self.assertIn("epoch=1", out.getvalue())
            with contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(worker_rvc.subprocess.CalledProcessError):
                    worker_rvc.run(["3"], seen.append)
        finally:
            worker_rvc.CORE = saved


if __name__ == "__main__":
    unittest.main()
