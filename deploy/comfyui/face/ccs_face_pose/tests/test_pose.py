"""Smoke-тесты для CcsFacePose: самопроверка порядка осей, воркер (с моком InsightFace),
узел ComfyUI (с моком subprocess). Запуск одной командой из корня custom_node:

    python3 -m unittest discover -s ccs_face_pose/tests -v

Что покрыто:
- самопроверка `_verify_pose_order` на «правильном» исходнике → проходит;
- та же проверка на перепутанном комментарии → падает с понятным сообщением;
- та же проверка на отсутствии паттерна → падает;
- воркер: одно лицо, известные эмбеддинги → poses[0] = {yaw, pitch, roll} как поля;
- воркер: лиц нет → poses[0] = null;
- воркер: 2 фото, на одном лицо есть, на другом нет → [pose, null];
- воркер: 2 лица в кадре → берётся самое крупное (по bbox);
- узел: недоступный воркер → ccs_face_pose=error и не падает.
"""
import importlib
import io
import json
import os
import sys
import tempfile
import textwrap
import unittest
from unittest import mock

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
NODE = os.path.dirname(HERE)  # каталог ccs_face_pose (где __init__.py)
PARENT = os.path.dirname(NODE)  # каталог face/ (родитель для import ccs_face_pose)
# core.py не используется — порядок осей проверяется через inspect.getsource прямо в воркере
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
    """Создаёт пустой файл и возвращает путь. Узел проверяет os.path.isfile перед Popen —
    без реальных файлов тесты с подменой Popen «падают на фильтре до Popen»."""
    open(path, "w").close()
    return path


# ---------- самопроверка порядка осей ----------

_GOOD_SOURCE = textwrap.dedent('''\
    class _FakeLandmark:
        def __init__(self):
            self.require_pose = False
        def something(self):
            if self.require_pose:
                rx, ry, rz = 1.0, 2.0, 3.0
                pose = np.array( [rx, ry, rz], dtype=np.float32 )
                face['pose'] = pose #pitch, yaw, roll
            return None
''')

_BAD_COMMENT_SOURCE = textwrap.dedent('''\
    class _FakeLandmark:
        def __init__(self):
            self.require_pose = False
        def something(self):
            if self.require_pose:
                rx, ry, rz = 1.0, 2.0, 3.0
                pose = np.array( [rx, ry, rz], dtype=np.float32 )
                face['pose'] = pose #yaw, pitch, roll  # перепутано
            return None
''')

_NO_PATTERN_SOURCE = textwrap.dedent('''\
    class _FakeLandmark:
        def __init__(self):
            self.require_pose = False
''')


def _make_module(name, src):
    """Возвращает подделку модуля с указанным исходником для inspect.getsource."""
    mod = type(sys)(name)
    exec(compile(src, f"<{name}>", "exec"), mod.__dict__)
    return mod


class TestPoseOrder(unittest.TestCase):
    """Проверка _verify_pose_order на трёх исходниках: нормальный, мусорный, без паттерна."""

    def setUp(self):
        # грузим воркер без вызова main() — нам нужна функция _verify_pose_order
        self._worker = importlib.import_module("worker_pose")

    def test_verify_pose_order_accepts_correct_source(self):
        """Канонический исходник → проверка проходит, в stdout пишется order_check=ok."""
        captured = io.StringIO()
        old = sys.stdout
        sys.stdout = captured
        try:
            self._worker._verify_pose_order(_GOOD_SOURCE)
        finally:
            sys.stdout = old
        self.assertIn("order_check=ok", captured.getvalue())

    def test_verify_pose_order_rejects_swapped_comment(self):
        """Комментарий про порядок есть, но оси перепутаны → RuntimeError с понятным
        сообщением. Мутация, которую нельзя пропустить."""
        with self.assertRaises(RuntimeError) as ctx:
            self._worker._verify_pose_order(_BAD_COMMENT_SOURCE)
        self.assertIn("порядок осей", str(ctx.exception))

    def test_verify_pose_order_rejects_missing_pattern(self):
        """Паттерна np.array([rx, ry, rz]) в исходнике нет → RuntimeError, воркер падает."""
        with self.assertRaises(RuntimeError) as ctx:
            self._worker._verify_pose_order(_NO_PATTERN_SOURCE)
        self.assertIn("порядок осей", str(ctx.exception))


# ---------- worker: моки insightface и cv2, реальный _verify_pose_order ----------

class _FakeFace:
    """Подделка «лица» с bbox и pose."""

    def __init__(self, bbox, pose):
        self.bbox = bbox
        self.pose = pose


def _faces_from_pose_value(val):
    """Нормализует значение mapping[path] в список _FakeFace.
    None        → пустой список («лиц нет»).
    np.ndarray  → один face с дефолтным bbox 100x100 и pose=val (именованные поля).
    list        → несколько лиц, каждое с дефолтным bbox; для теста «крупное vs нужное»."""
    if val is None:
        return []
    default_bbox = np.asarray([0, 0, 100, 100])
    if isinstance(val, np.ndarray):
        return [_FakeFace(bbox=default_bbox, pose=val)]
    if isinstance(val, list):
        faces = []
        for item in val:
            if isinstance(item, tuple):
                bbox, pose = item
            else:
                bbox, pose = default_bbox, item
            faces.append(_FakeFace(bbox=np.asarray(bbox), pose=pose))
        return faces
    raise TypeError(f"неизвестный тип значения mapping: {type(val).__name__}")


class _FakeApp:
    """Подделка FaceAnalysis: возвращает заранее заданные pose по имени файла."""

    def __init__(self, mapping):
        # mapping: {"/abs/path.png": np.array|None|list}
        self._mapping = mapping

    def prepare(self, ctx_id, det_size):
        return None

    def get(self, bgr):
        return _faces_from_pose_value(self._mapping.get(_FakeApp._current_path))


_FakeApp._current_path = None


class _FakeCv2:
    """Подделка cv2: imread возвращает фиктивный BGR-массив."""

    @staticmethod
    def imread(path, flags):
        if not os.path.isfile(path):
            return None
        return np.full((128, 128, 3), 64, dtype=np.uint8)

    IMREAD_COLOR = 1


def _install_fakes(mapping, good_source=_GOOD_SOURCE):
    """Подменить sys.modules: insightface / insightface.app / cv2 / landmark.
    Также мокаем inspect.getsource в worker_pose, чтобы _verify_pose_order получала
    нужный source независимо от того, какой поддельный модуль лежит в sys.modules."""
    fake_insightface = type(sys)("insightface")
    fake_app_mod = type(sys)("insightface.app")
    fake_landmark_mod = type(sys)("insightface.model_zoo.landmark")  # пустышка, source мокается
    fake_pkg = type(sys)("insightface.model_zoo")
    fake_pkg.landmark = fake_landmark_mod

    class _FaceAnalysis:
        def __new__(cls, name, root, providers, allowed_modules=None):
            return _FakeApp(mapping)

    fake_app_mod.FaceAnalysis = _FaceAnalysis
    fake_insightface.app = fake_app_mod

    cleanup = {
        "insightface": sys.modules.get("insightface"),
        "insightface.app": sys.modules.get("insightface.app"),
        "insightface.model_zoo": sys.modules.get("insightface.model_zoo"),
        "insightface.model_zoo.landmark": sys.modules.get("insightface.model_zoo.landmark"),
        "cv2": sys.modules.get("cv2"),
        "_FakeApp._current_path": _FakeApp._current_path,
    }
    sys.modules["insightface"] = fake_insightface
    sys.modules["insightface.app"] = fake_app_mod
    sys.modules["insightface.model_zoo"] = fake_pkg
    sys.modules["insightface.model_zoo.landmark"] = fake_landmark_mod
    sys.modules["cv2"] = _FakeCv2

    # Мокаем inspect.getsource в воркере, чтобы source = good_source независимо от модуля
    worker = importlib.import_module("worker_pose")
    real_getsource = worker.inspect.getsource

    def fake_getsource(obj):
        return good_source

    worker.inspect.getsource = fake_getsource
    cleanup["worker.inspect.getsource"] = real_getsource
    return cleanup


def _restore_fakes(cleanup):
    for k, v in cleanup.items():
        if k.startswith("_"):
            setattr(_FakeApp, k, v)
        elif k == "worker.inspect.getsource":
            # восстанавливаем в воркере
            mod = sys.modules.get("worker_pose")
            if mod is not None:
                mod.inspect.getsource = v
        elif v is None:
            sys.modules.pop(k, None)
        else:
            sys.modules[k] = v


class TestWorker(unittest.TestCase):
    def _run_worker(self, images_pose, good_source=_GOOD_SOURCE):
        """Гоняет worker_pose.main() с моками. images_pose — список кортежей
        (имя_файла, np.array|None|list) — это все картинки (variant + references) в
        порядке, как их положит узел. Возвращает распарсенный CCS_RESULT."""
        tmp = tempfile.mkdtemp(prefix="ccs-face-pose-test-")
        try:
            paths = []
            mapping = {}
            for name, pose in images_pose:
                p = os.path.join(tmp, name)
                with open(p, "wb") as f:
                    f.write(b"\x89PNG\r\n\x1a\n")
                mapping[p] = pose
                paths.append(p)

            job = {"images": paths, "face_models": tmp, "det_size": 640}
            job_path = os.path.join(tmp, "job.json")
            with open(job_path, "w", encoding="utf-8") as f:
                json.dump(job, f)

            old_argv = sys.argv
            sys.argv = ["worker_pose.py", job_path]

            original_imread = _FakeCv2.imread

            def imread_with_path(path, flags):
                _FakeApp._current_path = path
                return original_imread(path, flags)

            _FakeCv2.imread = staticmethod(imread_with_path)
            cleanup = _install_fakes(mapping, good_source=good_source)

            captured = io.StringIO()
            old_stdout = sys.stdout
            sys.stdout = captured
            try:
                try:
                    mod = importlib.import_module("worker_pose")
                    mod.main()
                    rc = 0
                except SystemExit as e:
                    rc = int(e.code) if e.code is not None else 0
                except RuntimeError as e:
                    # _verify_pose_order и другие внутренние ошибки воркера:
                    # узел трактует ненулевой код как error, не падает в графе
                    print(f"воркер упал: {type(e).__name__}: {e}", flush=True)
                    rc = 2
            finally:
                sys.stdout = old_stdout
                _restore_fakes(cleanup)
                _FakeCv2.imread = original_imread
                sys.argv = old_argv

            output = captured.getvalue()
            result = None
            for line in reversed(output.strip().splitlines()):
                if line.startswith("CCS_RESULT "):
                    result = json.loads(line[len("CCS_RESULT "):])
                    break
            return result, output, rc
        finally:
            import shutil
            shutil.rmtree(tmp, ignore_errors=True)

    def test_worker_one_image_returns_named_pose_fields(self):
        """Одна картинка с известным pose (pitch, yaw, roll) → poses[0] имеет те же
        три поля. Воркер не различает variant vs references — все картинки одинаковые."""
        pose = np.array([15.0, -20.0, 5.0])  # [pitch, yaw, roll] — порядок канонический
        result, output, rc = self._run_worker(
            images_pose=[("v.png", pose)],
        )
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertEqual(len(result["poses"]), 1)
        p = result["poses"][0]
        self.assertIsNotNone(p)
        self.assertAlmostEqual(p["yaw"], -20.0, places=6)
        self.assertAlmostEqual(p["pitch"], 15.0, places=6)
        self.assertAlmostEqual(p["roll"], 5.0, places=6)
        self.assertEqual(set(p.keys()), {"yaw", "pitch", "roll"})

    def test_worker_no_face_returns_null(self):
        """Лиц нет → poses[0] = null. Узел не падает, граф завершается успешно."""
        result, output, rc = self._run_worker(
            images_pose=[("v.png", None)],
        )
        self.assertIsNotNone(result)
        self.assertEqual(len(result["poses"]), 1)
        self.assertIsNone(result["poses"][0])
        self.assertEqual(result["stats"]["used"], 0)

    def test_worker_mixed_faces_and_no_faces_in_order(self):
        """Несколько файлов с лицами/без: в poses нули/null на тех же индексах, что в images."""
        pose = np.array([1.0, 2.0, 3.0])
        result, output, rc = self._run_worker(
            images_pose=[
                ("a.png", pose),
                ("b.png", None),
                ("c.png", pose),
            ],
        )
        self.assertIsNotNone(result)
        self.assertEqual(len(result["poses"]), 3)
        self.assertIsNotNone(result["poses"][0])
        self.assertIsNone(result["poses"][1])
        self.assertIsNotNone(result["poses"][2])
        self.assertEqual(result["stats"]["used"], 2)
        self.assertEqual(result["stats"]["total"], 3)

    def test_worker_picks_biggest_face_when_many(self):
        """Групповое фото (2 лица): для ракурса берётся самое крупное по bbox. Иначе
        на эталоне с двумя лицами поза чужого лица уехала бы в паспорт фото."""
        faces = [
            (np.asarray([0, 0, 60, 60]), np.array([1.0, 1.0, 1.0])),    # мелкое, чужое
            (np.asarray([0, 0, 300, 300]), np.array([7.0, 8.0, 9.0])),  # крупное, нужное
        ]
        result, output, rc = self._run_worker(
            images_pose=[("g.png", faces)],
        )
        self.assertIsNotNone(result)
        p = result["poses"][0]
        self.assertAlmostEqual(p["yaw"], 8.0, places=6)
        self.assertAlmostEqual(p["pitch"], 7.0, places=6)
        self.assertAlmostEqual(p["roll"], 9.0, places=6)

    def test_worker_panics_on_swapped_axes(self):
        """Если в исходнике landmark.py оси перепутаны, воркер падает до загрузки модели —
        график не выдаёт «неверные числа» без видимой ошибки."""
        result, output, rc = self._run_worker(
            images_pose=[("v.png", np.array([1.0, 2.0, 3.0]))],
            good_source=_BAD_COMMENT_SOURCE,
        )
        # main() падает с RuntimeError, rc != 0, CCS_RESULT отсутствует
        self.assertNotEqual(rc, 0)
        self.assertIsNone(result)
        self.assertIn("порядок осей", output)


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

    def wait(self, timeout=None):
        return self.returncode

    def kill(self):
        pass


class _FakeFolderPaths:
    def __init__(self, root):
        self._root = root

    def get_input_directory(self):
        return self._root


class TestNode(unittest.TestCase):
    def _setup_input(self, tmp):
        in_root = os.path.join(tmp, "input")
        os.makedirs(in_root, exist_ok=True)
        ref_path = os.path.join(in_root, "test_ref.png")
        with open(ref_path, "wb") as f:
            f.write(b"fake png")
        return in_root, ref_path

    def _fake_image(self):
        try:
            import torch
            return torch.zeros(1, 4, 4, 3, dtype=torch.float32)
        except ImportError:
            return np.zeros((1, 4, 4, 3), dtype=np.float32)

    def setUp(self):
        self._node_module = importlib.import_module("ccs_face_pose")

    def _patch_config_and_input(self, in_root, family=None, workers=None):
        if family is None:
            python_path = os.path.join(in_root, "_fake_python")
            script_path = os.path.join(in_root, "_fake_worker.py")
            family = {"python": python_path, "script": script_path}
        if workers is None:
            workers = {"families": {"pose": family}}
        return (
            mock.patch.object(self._node_module, "_config", return_value=workers),
            mock.patch.object(self._node_module.folder_paths, "get_input_directory",
                              return_value=in_root),
        )

    def test_node_missing_worker_path_returns_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            in_root, _ = self._setup_input(tmp)
            family = {"python": "/no/such/python",
                      "script": "/no/such/script.py"}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            with cfg_patch, fp_patch:
                node = self._node_module.CcsFacePose()
                out = node.run(references="test_ref.png", image=self._fake_image())
        self.assertEqual(out["ui"]["text"], ["ccs_face_pose=error"])

    def test_node_missing_pose_family_returns_error(self):
        """workers.json без семейства pose — узел должен вернуть error, а не падать."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, _ = self._setup_input(tmp)
            workers = {"families": {"similarity": {"python": "/x", "script": "/y"}}}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, workers=workers)
            with cfg_patch, fp_patch:
                node = self._node_module.CcsFacePose()
                out = node.run(references="test_ref.png", image=self._fake_image())
        self.assertEqual(out["ui"]["text"], ["ccs_face_pose=error"])

    def test_node_worker_returns_named_pose_per_reference(self):
        """Воркер вернул poses = [variant, ref1] с двумя позами → ui.text содержит JSON
        с ref_path в качестве ключа и {yaw, pitch, roll} как значением. variant из
        текста исключён: он служебный, в карточке графа виден через SaveImage."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, ref_path = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            payload = json.dumps({
                "poses": [
                    {"yaw": -20.0, "pitch": 15.0, "roll": 5.0},   # variant
                    {"yaw": 0.5, "pitch": -3.0, "roll": 0.0},    # ref1
                ],
                "stats": {"used": 2, "total": 2},
            })
            fake_proc = _FakePopen(f"noise log\nCCS_RESULT {payload}\n")
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen",
                                   return_value=fake_proc), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFacePose()
                out = node.run(references="test_ref.png", image=self._fake_image())
        text = out["ui"]["text"][0]
        self.assertTrue(text.startswith("ccs_face_pose="))
        data = json.loads(text[len("ccs_face_pose="):])
        # в JSON только ref (variant не идёт в выдачу)
        self.assertEqual(len(data), 1)
        self.assertIn(ref_path, data)
        v = data[ref_path]
        self.assertEqual(set(v.keys()), {"yaw", "pitch", "roll"})
        # ref_path получил позу ИЗ ВТОРОГО элемента poses (poses[1] — ref)
        self.assertAlmostEqual(v["yaw"], 0.5, places=6)
        self.assertAlmostEqual(v["pitch"], -3.0, places=6)
        self.assertAlmostEqual(v["roll"], 0.0, places=6)

    def test_node_worker_returns_null_pose_keeps_null(self):
        """Воркер вернул null в позе (лицо не найдено) → в JSON остаётся запись с null."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, ref_path = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            # variant + ref1, обе null
            payload = json.dumps({
                "poses": [None, None],
                "stats": {"used": 0, "total": 2},
            })
            fake_proc = _FakePopen(f"CCS_RESULT {payload}\n")
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen",
                                   return_value=fake_proc), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFacePose()
                out = node.run(references="test_ref.png", image=self._fake_image())
        text = out["ui"]["text"][0]
        data = json.loads(text[len("ccs_face_pose="):])
        self.assertEqual(len(data), 1)
        self.assertEqual(data[ref_path], {"yaw": None, "pitch": None, "roll": None})

    def test_node_uses_references_only_when_no_references(self):
        """Узел без references (только variant) → в ui.text пустой JSON {}."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, _ = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            payload = json.dumps({
                "poses": [{"yaw": 1.0, "pitch": 2.0, "roll": 3.0}],
                "stats": {"used": 1, "total": 1},
            })
            fake_proc = _FakePopen(f"CCS_RESULT {payload}\n")
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen",
                                   return_value=fake_proc), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFacePose()
                out = node.run(references="", image=self._fake_image())
        text = out["ui"]["text"][0]
        data = json.loads(text[len("ccs_face_pose="):])
        self.assertEqual(data, {})

    def test_node_no_image_uses_only_references(self):
        """Самостоятельный вызов (image=None, как у ComfyWorkflows.FacePose):
        воркер получает только references, в JSON — запись на каждый ref."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, ref_path = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            # воркер отдал позы только для references (image=None не добавлял variant)
            payload = json.dumps({
                "poses": [{"yaw": 11.0, "pitch": 12.0, "roll": 13.0}],
                "stats": {"used": 1, "total": 1},
            })
            fake_proc = _FakePopen(f"CCS_RESULT {payload}\n")
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen",
                                   return_value=fake_proc), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFacePose()
                out = node.run(references="test_ref.png", image=None)
        text = out["ui"]["text"][0]
        data = json.loads(text[len("ccs_face_pose="):])
        self.assertEqual(len(data), 1)
        self.assertAlmostEqual(data[ref_path]["yaw"], 11.0, places=6)
        # _save_variant_png не должен вызываться, когда image=None
        # (это побочный эффект mock.patch.object как side_effect; но мы только
        # проверяем выдачу; реальное поведение проверяется на стенде)

    def test_node_worker_nonzero_returncode_returns_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            in_root, _ = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            fake_proc = _FakePopen("Traceback...\nError", returncode=1)
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen",
                                   return_value=fake_proc), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFacePose()
                out = node.run(references="test_ref.png", image=self._fake_image())
        self.assertEqual(out["ui"]["text"], ["ccs_face_pose=error"])

    def test_node_safe_input_rejects_path_traversal(self):
        """Путь с `..` в references → ValueError, эталон пропускается, но Popen всё равно
        вызывается с пустым images (только variant). Узел не падает на одной битой строке."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, _ = self._setup_input(tmp)
            popen_calls = []
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}

            class _SpyPopen:
                def __init__(self, *a, **kw):
                    # argv: [python, script, job_path, ...]
                    job_path = a[0][2] if a and a[0] and len(a[0]) > 2 else None
                    job_payload = None
                    if job_path and os.path.isfile(job_path):
                        with open(job_path, encoding="utf-8") as f:
                            job_payload = json.load(f)
                    popen_calls.append((a, kw, job_payload))
                    self.pid = 1
                    self.returncode = 0
                    self._stdout = io.StringIO('CCS_RESULT {"poses":[null]}\n')

                def poll(self):
                    return self.returncode

                @property
                def stdout(self):
                    return self._stdout

                def wait(self, timeout=None):
                    return self.returncode

                def kill(self):
                    pass

            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen", _SpyPopen), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFacePose()
                out = node.run(references="../../etc/passwd", image=self._fake_image())
        self.assertTrue(popen_calls, "Popen не вызван — узел должен звать воркера даже при пустых refs")
        # Шпион _SpyPopen запомнил job.json к моменту своей инициализации — узел его
        # удаляет в `finally`, поэтому читать файл уже поздно; в шпионе — копия.
        job_payload = popen_calls[0][2]
        self.assertEqual(len(job_payload["images"]), 1,
                         msg="path-traversal не должен попасть в images; только variant")


if __name__ == "__main__":
    unittest.main(verbosity=2)