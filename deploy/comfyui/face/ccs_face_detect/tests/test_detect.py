"""Тесты CcsFaceDetect: порядок и пороги (core, чистый numpy), воркер (с моками
insightface/cv2), узел ComfyUI (с моком subprocess). Запуск из каталога узла:

    python3 -m unittest discover -s tests -v

Что покрыто:
- core: порядок по ключу (центр x, центр y) — слева направо, при равном x сверху вниз,
  в том числе на контрпримере к старому нетранзитивному правилу; порог score; потолок
  8 лиц по площади (оставляет крупные, потом сортирует); small на границе 64 px; рамка —
  целые пиксели внутри картинки, округление half-up;
- воркер: пустой кадр → faces: [], W/H из прочитанной картинки, параметры детектора —
  константы core, нечитаемая картинка → код 2 без CCS_RESULT;
- узел: путь вне input → error без запуска воркера, сбой воркера → error, ответ — строка
  ccs_face_detect={...} по контракту.

Мутации: сортировка по площади вместо (x, y) — краснеют все 7 тестов порядка, включая
test_nontransitive_counterexample_by_x; старый компаратор «близкие — сверху вниз» —
краснеют test_nontransitive_counterexample_by_x и test_close_centers_still_left_to_right;
снять порог small — краснеет test_small_boundary_64; round() вместо half-up — краснеет
test_bbox_rounds_half_up.
"""
import importlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from unittest import mock

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
NODE = os.path.dirname(HERE)  # каталог ccs_face_detect (где __init__.py)
PARENT = os.path.dirname(NODE)  # каталог face/ (родитель для import ccs_face_detect)
if NODE not in sys.path:
    sys.path.insert(0, NODE)
if PARENT not in sys.path:
    sys.path.insert(0, PARENT)

# __init__.py узла делает `import folder_paths` на верхнем уровне: без ComfyUI под рукой
# нет такого модуля. До импорта узла подсунем заглушку.
if "folder_paths" not in sys.modules:
    _fake_folder_paths = type(sys)("folder_paths")
    _fake_folder_paths.get_input_directory = lambda: "/tmp"
    sys.modules["folder_paths"] = _fake_folder_paths


def _touch(path):
    open(path, "w").close()
    return path


def _box(cx, cy, w, h=None):
    """Рамка по центру и размеру — так тесты читаются без арифметики в голове."""
    h = w if h is None else h
    return [cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2]


def _center(f):
    b = f["bbox"]
    return (b[0] + b[2]) / 2, (b[1] + b[3]) / 2


# ---------- core ----------

class TestCore(unittest.TestCase):
    W, H = 2000, 2000

    def _arrange(self, raw):
        from core import arrange_faces
        return arrange_faces(raw, self.W, self.H)

    def test_left_to_right_by_center_x(self):
        """Порядок — по центру x, а не по площади и не по порядку детектора: крупное лицо
        справа получает номер 2, мелкое слева — 1."""
        raw = [
            (_box(1500, 500, 400), 0.9),  # справа, крупное
            (_box(300, 900, 100), 0.9),   # слева, мелкое
            (_box(900, 200, 200), 0.9),   # посередине
        ]
        centers = [(f["bbox"][0] + f["bbox"][2]) / 2 for f in self._arrange(raw)]
        self.assertEqual(centers, [300, 900, 1500])

    def test_close_centers_still_left_to_right(self):
        """Близкие по x центры не переходят на «сверху вниз»: нижнее, но чуть левее лицо —
        первое. Правило «близкие — сверху вниз» снято: оно нетранзитивно."""
        raw = [
            (_box(550, 400, 200), 0.9),   # выше, правее на 30 px
            (_box(520, 1200, 200), 0.9),  # ниже, левее
        ]
        faces = self._arrange(raw)
        self.assertEqual([_center(f) for f in faces], [(520, 1200), (550, 400)])

    def test_equal_x_tie_break_top_to_bottom(self):
        """Равные центры по x — сверху вниз по центру y, независимо от порядка детектора."""
        raw = [
            (_box(600, 1500, 200), 0.9),
            (_box(600, 300, 200), 0.9),
            (_box(600, 900, 200), 0.9),
        ]
        faces = self._arrange(raw)
        self.assertEqual([_center(f) for f in faces], [(600, 300), (600, 900), (600, 1500)])

    def test_nontransitive_counterexample_by_x(self):
        """Контрпример Глеба к старому правилу: три лица шириной 400, пары A↔B и B↔C
        решались по y, а A↔C — по x, и сортировка давала [C, B, A]. Теперь строго по x
        при любом порядке детектора. Сдвиг +1000 — чтобы рамки не обрезались краем
        картинки; взаимное расположение то же."""
        d = 1000
        a, b, c = _box(d + 100, d + 400, 400), _box(d + 200, d + 200, 400), _box(d + 300, d + 100, 400)
        expected = [(d + 100, d + 400), (d + 200, d + 200), (d + 300, d + 100)]
        for order in ([a, b, c], [c, b, a], [b, c, a], [c, a, b]):
            faces = self._arrange([(bb, 0.9) for bb in order])
            self.assertEqual([_center(f) for f in faces], expected)

    def test_far_centers_stay_left_to_right(self):
        """Центры дальше половины ширины меньшего — снова слева направо, хотя левое ниже."""
        raw = [
            (_box(700, 400, 200), 0.9),
            (_box(500, 1200, 200), 0.9),  # левее на 200 px (> 100)
        ]
        faces = self._arrange(raw)
        self.assertEqual([(f["bbox"][0] + f["bbox"][2]) / 2 for f in faces], [500, 700])

    def test_score_threshold(self):
        """score < 0,5 отбрасывается, ровно 0,5 остаётся."""
        raw = [
            (_box(300, 300, 100), 0.49),
            (_box(600, 300, 100), 0.5),
            (_box(900, 300, 100), 0.9),
        ]
        faces = self._arrange(raw)
        self.assertEqual([f["score"] for f in faces], [0.5, 0.9])

    def test_cap_keeps_eight_largest_then_sorts(self):
        """10 лиц: два самых мелких отбрасываются, оставшиеся 8 — слева направо, а не по
        площади. Самое мелкое стоит левее всех — значит, отбор идёт по площади, не по x."""
        raw = [(_box(100 + i * 180, 500, 100 + i * 10), 0.9) for i in range(10)]
        raw.reverse()  # детектор отдаёт в своём порядке
        faces = self._arrange(raw)
        self.assertEqual(len(faces), 8)
        centers = [(f["bbox"][0] + f["bbox"][2]) / 2 for f in faces]
        self.assertEqual(centers, [100 + i * 180 for i in range(2, 10)])

    def test_small_boundary_64(self):
        """Короткая сторона 63 px — small, 64 — нет; считается именно короткая сторона."""
        raw = [
            ([100, 100, 163, 300], 0.9),   # 63 × 200
            ([500, 100, 564, 164], 0.9),   # 64 × 64
            ([900, 100, 1100, 163], 0.9),  # 200 × 63
        ]
        faces = self._arrange(raw)
        self.assertEqual([f["small"] for f in faces], [True, False, True])

    def test_bbox_is_int_and_clipped(self):
        """Рамка — целые пиксели внутри картинки, даже если детектор вылез за край."""
        from core import arrange_faces
        faces = arrange_faces([(np.array([-12.6, 10.4, 330.5, 260.7], dtype=np.float32), 0.8)],
                              320, 240)
        self.assertEqual(faces[0]["bbox"], [0, 10, 320, 240])
        self.assertTrue(all(type(v) is int for v in faces[0]["bbox"]))

    def test_bbox_rounds_half_up(self):
        """x.5 округляется вверх, а не к чётному: 0.5 → 1, 2.5 → 3 (round() дал бы 0 и 2)."""
        from core import arrange_faces
        faces = arrange_faces([([0.5, 2.5, 10.5, 12.5], 0.9)], 100, 100)
        self.assertEqual(faces[0]["bbox"], [1, 3, 11, 13])

    def test_same_input_same_output(self):
        """Детерминизм: тот же вход — тот же ответ, вплоть до сериализации."""
        raw = [(_box(500 + 13 * i, 300 + 400 * (i % 3), 120), 0.6 + i * 0.01) for i in range(9)]
        a = json.dumps(self._arrange(raw))
        b = json.dumps(self._arrange([(list(bb), s) for bb, s in raw]))
        self.assertEqual(a, b)


# ---------- worker: моки insightface и cv2 ----------

class _FakeFace:
    def __init__(self, bbox, det_score):
        self.bbox = np.asarray(bbox, dtype=np.float32)
        self.det_score = np.float32(det_score)


class _FakeApp:
    """Подделка FaceAnalysis: запоминает параметры и отдаёт заданные лица."""
    instances = []

    def __init__(self, faces, kwargs):
        self._faces = faces
        self.kwargs = kwargs
        self.prepared = None
        _FakeApp.instances.append(self)

    def prepare(self, ctx_id, det_thresh=0.5, det_size=(640, 640)):
        self.prepared = {"ctx_id": ctx_id, "det_thresh": det_thresh, "det_size": det_size}

    def get(self, bgr):
        return list(self._faces)


class _FakeCv2:
    IMREAD_COLOR = 1
    shape = (480, 640, 3)

    @staticmethod
    def imread(path, flags):
        if not os.path.isfile(path) or os.path.getsize(path) == 0:
            return None
        return np.zeros(_FakeCv2.shape, dtype=np.uint8)


def _install_fakes(faces):
    fake_insightface = type(sys)("insightface")
    fake_app_mod = type(sys)("insightface.app")

    def _face_analysis(**kwargs):
        return _FakeApp(faces, kwargs)

    fake_app_mod.FaceAnalysis = _face_analysis
    fake_insightface.app = fake_app_mod
    saved = {k: sys.modules.get(k) for k in ("insightface", "insightface.app", "cv2")}
    sys.modules["insightface"] = fake_insightface
    sys.modules["insightface.app"] = fake_app_mod
    sys.modules["cv2"] = _FakeCv2
    return saved


def _restore_fakes(saved):
    for k, v in saved.items():
        if v is None:
            sys.modules.pop(k, None)
        else:
            sys.modules[k] = v


class TestWorker(unittest.TestCase):
    def setUp(self):
        _FakeApp.instances = []
        _FakeCv2.shape = (480, 640, 3)

    def _run_worker(self, faces, image_bytes=b"\x89PNG\r\n\x1a\n"):
        tmp = tempfile.mkdtemp(prefix="ccs-face-detect-test-")
        try:
            img = os.path.join(tmp, "img.png")
            with open(img, "wb") as f:
                f.write(image_bytes)
            job_path = os.path.join(tmp, "job.json")
            with open(job_path, "w", encoding="utf-8") as f:
                json.dump({"image": img, "face_models": tmp}, f)

            old_argv, old_stdout = sys.argv, sys.stdout
            sys.argv = ["worker_detect.py", job_path]
            saved = _install_fakes(faces)
            captured = io.StringIO()
            sys.stdout = captured
            try:
                mod = importlib.import_module("worker_detect")
                mod.main()
                rc = 0
            except SystemExit as e:
                rc = int(e.code) if e.code is not None else 0
            finally:
                sys.stdout = old_stdout
                sys.argv = old_argv
                _restore_fakes(saved)

            output = captured.getvalue()
            result = None
            for line in reversed(output.strip().splitlines()):
                if line.startswith("CCS_RESULT "):
                    result = json.loads(line[len("CCS_RESULT "):])
                    break
            return result, output, rc
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_empty_frame_returns_empty_faces(self):
        """Лиц нет → faces: [] и код 0: это «лиц нет», а не ошибка."""
        result, output, rc = self._run_worker(faces=[])
        self.assertEqual(rc, 0, msg=output)
        self.assertIsNotNone(result, msg=output)
        self.assertEqual(result["faces"], [])
        self.assertEqual((result["width"], result["height"]), (640, 480))

    def test_width_height_from_read_image(self):
        """W/H — размер того, что прочитал cv2 (H, W из shape), а не det_size и не что-то
        ещё; рамки — в порядке core и обрезаны этим размером."""
        _FakeCv2.shape = (1080, 720, 3)
        faces = [_FakeFace([500, 100, 700, 300], 0.9),
                 _FakeFace([10, 600, 210, 1200], 0.8),
                 _FakeFace([300, 300, 340, 340], 0.3)]
        result, output, rc = self._run_worker(faces=faces)
        self.assertEqual(rc, 0, msg=output)
        self.assertEqual((result["width"], result["height"]), (720, 1080))
        self.assertEqual([f["bbox"] for f in result["faces"]],
                         [[10, 600, 210, 1080], [500, 100, 700, 300]])
        self.assertEqual(set(result["faces"][0].keys()), {"bbox", "score", "small"})

    def test_detector_uses_core_constants(self):
        """Только модуль detection, CPU, det_size и порог — константы core (детерминизм)."""
        import core
        self._run_worker(faces=[])
        app = _FakeApp.instances[-1]
        self.assertEqual(app.kwargs["allowed_modules"], ["detection"])
        self.assertEqual(app.kwargs["providers"], ["CPUExecutionProvider"])
        self.assertEqual(app.prepared["det_size"], (core.DET_SIZE, core.DET_SIZE))
        self.assertEqual(app.prepared["det_thresh"], core.MIN_SCORE)

    def test_unreadable_image_is_error(self):
        """cv2 не прочитал картинку → код 2 и без CCS_RESULT: узел вернёт error."""
        result, output, rc = self._run_worker(faces=[], image_bytes=b"")
        self.assertNotEqual(rc, 0)
        self.assertIsNone(result)
        self.assertIn("не прочитал", output)


# ---------- node: моки subprocess.Popen ----------

class _FakePopen:
    def __init__(self, stdout_text, returncode=0):
        self._stdout = io.StringIO(stdout_text)
        self.returncode = returncode
        self.pid = 12345

    def poll(self):
        return self.returncode

    @property
    def stdout(self):
        return self._stdout


class TestNode(unittest.TestCase):
    def setUp(self):
        self._node = importlib.import_module("ccs_face_detect")
        self._tmp = tempfile.mkdtemp(prefix="ccs-face-detect-node-")
        self._in_root = os.path.join(self._tmp, "input")
        os.makedirs(self._in_root)
        with open(os.path.join(self._in_root, "pic.png"), "wb") as f:
            f.write(b"fake png")
        self._family = {"python": _touch(os.path.join(self._tmp, "_p")),
                        "script": _touch(os.path.join(self._tmp, "_w.py"))}

    def tearDown(self):
        shutil.rmtree(self._tmp, ignore_errors=True)

    def _run(self, references, popen):
        with mock.patch.object(self._node, "_config",
                               return_value={"families": {"detect": self._family}}), \
             mock.patch.object(self._node.folder_paths, "get_input_directory",
                               return_value=self._in_root), \
             mock.patch.object(self._node.subprocess, "Popen", popen), \
             mock.patch.object(self._node.time, "sleep", lambda _x: None):
            return self._node.CcsFaceDetect().run(references=references)

    def test_output_string_format(self):
        """Ответ воркера → одна строка ccs_face_detect={...} с width/height/faces, лица —
        объекты {bbox, score, small}; служебные stats в выход не едут."""
        payload = {"width": 640, "height": 480,
                   "faces": [{"bbox": [10, 20, 110, 140], "score": 0.87, "small": False}],
                   "stats": {"load_s": 2.1}}
        out = self._run("pic.png", mock.Mock(
            return_value=_FakePopen(f"лог\nCCS_RESULT {json.dumps(payload)}\n")))
        self.assertEqual(out["ui"]["text"], [
            'ccs_face_detect={"width":640,"height":480,'
            '"faces":[{"bbox":[10,20,110,140],"score":0.87,"small":false}]}'])

    def test_no_faces_is_not_error(self):
        payload = {"width": 640, "height": 480, "faces": []}
        out = self._run("pic.png", mock.Mock(
            return_value=_FakePopen(f"CCS_RESULT {json.dumps(payload)}\n")))
        self.assertEqual(out["ui"]["text"],
                         ['ccs_face_detect={"width":640,"height":480,"faces":[]}'])

    def test_safe_input_rejects_outside_input(self):
        """Путь вне input/ (.., абсолютный) → error, воркер даже не запускается."""
        outside = os.path.join(self._tmp, "outside.png")
        _touch(outside)
        for ref in ("../outside.png", outside, "nope.png", "pic.txt"):
            popen = mock.Mock()
            out = self._run(ref, popen)
            self.assertEqual(out["ui"]["text"], ["ccs_face_detect=error"], msg=ref)
            popen.assert_not_called()

    def test_more_than_one_name_is_error(self):
        popen = mock.Mock()
        out = self._run("pic.png\npic.png", popen)
        self.assertEqual(out["ui"]["text"], ["ccs_face_detect=error"])
        popen.assert_not_called()

    def test_worker_failure_is_error(self):
        """Ненулевой код, нет CCS_RESULT, неожиданная форма ответа, сбой запуска —
        везде error, исключение наружу не летит."""
        bad_shapes = [
            {"width": 640, "height": 480, "faces": [{"bbox": [1, 2, 3], "score": 0.9,
                                                     "small": False}]},
            {"width": 640, "height": 480, "faces": [[1, 2, 3, 4]]},
            {"width": "640", "height": 480, "faces": []},
            {"height": 480, "faces": []},
        ]
        cases = [_FakePopen("Traceback...\n", returncode=2),
                 _FakePopen("никакого результата\n")]
        cases += [_FakePopen(f"CCS_RESULT {json.dumps(p)}\n") for p in bad_shapes]
        for proc in cases:
            out = self._run("pic.png", mock.Mock(return_value=proc))
            self.assertEqual(out["ui"]["text"], ["ccs_face_detect=error"])
        out = self._run("pic.png", mock.Mock(side_effect=OSError("нет интерпретатора")))
        self.assertEqual(out["ui"]["text"], ["ccs_face_detect=error"])

    def test_missing_detect_family_is_error(self):
        with mock.patch.object(self._node, "_config",
                               return_value={"families": {"pose": self._family}}):
            out = self._node.CcsFaceDetect().run(references="pic.png")
        self.assertEqual(out["ui"]["text"], ["ccs_face_detect=error"])

    def test_job_carries_real_path_inside_input(self):
        """В job.json едет реальный путь внутри input/, а не то, что прислали."""
        jobs = []

        def spy(argv, **kw):
            with open(argv[2], encoding="utf-8") as f:
                jobs.append(json.load(f))
            return _FakePopen('CCS_RESULT {"width":1,"height":1,"faces":[]}\n')

        self._run("pic.png", spy)
        self.assertEqual(jobs[0]["image"],
                         os.path.realpath(os.path.join(self._in_root, "pic.png")))


if __name__ == "__main__":
    unittest.main(verbosity=2)
