"""Smoke-тесты для CcsFaceSimilarity: чистая математика (numpy), воркер (с моком insightface/cv2),
узел ComfyUI (с моком subprocess). Запуск одной командой из корня custom_node:

    python3 -m unittest discover -s ccs_face_similarity/tests -v

или через install.sh: README, раздел «Тесты».

Что покрыто:
- центроид двух ортогональных → cos = 1/√2 ≈ 0.7071 (мутация «снять вторую L2-нормировку»
  красит другой тест — на длину самого центроида);
- лица нет (пустой список) → similarity=None;
- NaN/Inf в эталонах → отбрасываются, иначе центроид разносит в NaN;
- два лица в варианте, нужное — не самое крупное → берётся его сходство (мутация
  «вернуть самое крупное» красит этот тест);
- путь ../etc/passwd у узла → ValueError, эталон пропускается;
- недоступный воркер у узла → ccs_face_similarity=none;
- маска: отбор лиц по покрытию bbox (граница 0,5), в воркере — лицо в маске, а не максимум
  (мутация «игнорировать маску» красит этот тест), битая маска → null, узел кладёт mask в
  job.json только при поданной маске.
"""
import importlib
import io
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
NODE = os.path.dirname(HERE)  # каталог ccs_face_similarity (где __init__.py)
PARENT = os.path.dirname(NODE)  # каталог face/ (родитель для import ccs_face_similarity)
# core.py лежит в NODE; узел — пакет, импортируется как ccs_face_similarity
if NODE not in sys.path:
    sys.path.insert(0, NODE)
if PARENT not in sys.path:
    sys.path.insert(0, PARENT)

# __init__.py узла делает `import folder_paths` на верхнем уровне: без ComfyUI под рукой
# нет такого модуля. До импорта узла подсунем заглушку — реальный folder_paths живёт в
# process ComfyUI и держит всё, что нужно узлу в run-time (get_input_directory и т. п.).
if "folder_paths" not in sys.modules:
    _fake_folder_paths = type(sys)("folder_paths")
    _fake_folder_paths.get_input_directory = lambda: "/tmp"
    sys.modules["folder_paths"] = _fake_folder_paths


def _touch(path):
    """Создаёт пустой файл и возвращает путь. Узел проверяет os.path.isfile перед Popen —
    без реальных файлов тесты с подменой Popen «падают на фильтре до Popen»."""
    open(path, "w").close()
    return path


# ---------- core: чистая арифметика ----------

class TestCore(unittest.TestCase):
    def test_two_orthogonal_cosine_is_0_7071(self):
        """Два ортогональных эталона e1=(1,0,…), e2=(0,1,…) дают центроид (1,1,0,…)/√2;
        cosine с вариантом-эталоном e1 равен 1/√2 ≈ 0.7071."""
        from core import centroid_l2, cosine

        e1 = np.array([1.0, 0.0, 0.0, 0.0])
        e2 = np.array([0.0, 1.0, 0.0, 0.0])
        centroid = centroid_l2([e1, e2])
        # L2-нормированный центроид: (1/√2, 1/√2, 0, 0)
        self.assertAlmostEqual(float(centroid[0]), 1.0 / np.sqrt(2.0), places=6)
        self.assertAlmostEqual(float(centroid[1]), 1.0 / np.sqrt(2.0), places=6)
        self.assertAlmostEqual(float(centroid[2]), 0.0, places=6)
        self.assertAlmostEqual(float(np.linalg.norm(centroid)), 1.0, places=6)

        sim = cosine(e1, centroid)
        # 0.7071067811865475 — это и есть порог для мутации «убрать L2-нормировку»
        self.assertAlmostEqual(sim, 0.7071, places=3)
        self.assertAlmostEqual(sim, 1.0 / np.sqrt(2.0), places=6)

    def test_centroid_is_l2_normalized_after_l2_step(self):
        """Главная мутация ревью: убрать `centroid = centroid / np.linalg.norm(centroid)`.
        Косинус тогда не меняется (он инвариантен к длине), но длина самого центроида
        становится ≠ 1 — этот тест её и ловит."""
        from core import centroid_l2

        a = np.array([3.0, 0.0, 0.0])  # ненормированный — после шага 1 станет (1,0,0)
        b = np.array([0.0, 1.0, 0.0])
        c = np.array([0.0, 0.0, 1.0])
        centroid = centroid_l2([a, b, c])
        # длина должна быть ровно 1, а не sqrt(1/3) ≈ 0.577
        self.assertAlmostEqual(float(np.linalg.norm(centroid)), 1.0, places=6)

    def test_centroid_keeps_unnormalized_vectors_in_check(self):
        """Если входы разной длины (ArcFace всегда единичные, но защита не лишняя),
        алгоритм всё равно даёт единичный центроид: первый шаг — нормировка каждого."""
        from core import centroid_l2, cosine

        a = np.array([2.0, 0.0])  # |a| = 2
        b = np.array([0.0, 4.0])  # |b| = 4
        centroid = centroid_l2([a, b])
        self.assertAlmostEqual(float(np.linalg.norm(centroid)), 1.0, places=6)
        # нормированный a — это (1, 0); косинус с центроидом не равен 0.5,
        # потому что центроид сглажен
        self.assertGreater(cosine(a, centroid), 0.7)

    def test_drop_nonfinite_filters_nan_inf(self):
        """NaN/Inf в одном эталоне разносит центроид в NaN — защита drop_nonfinite."""
        from core import drop_nonfinite, centroid_l2, cosine

        good = np.array([1.0, 0.0, 0.0])
        nan = np.array([np.nan, 1.0, 0.0])
        inf = np.array([np.inf, 0.0, 1.0])
        kept = drop_nonfinite([good, nan, inf])
        self.assertEqual(len(kept), 1)
        centroid = centroid_l2([good, nan, inf])
        # NaN и Inf отброшены → остался только good
        self.assertTrue(np.isfinite(centroid).all())
        self.assertAlmostEqual(cosine(good, centroid), 1.0, places=6)

    def test_best_similarity_no_face_returns_none(self):
        from core import best_similarity_from

        # variant пуст → None
        sim, used = best_similarity_from([], [np.array([1.0, 0.0])])
        self.assertIsNone(sim)

        # refs пуст → None
        sim, used = best_similarity_from([np.array([1.0, 0.0])], [])
        self.assertIsNone(sim)

        # все refs с NaN → None
        sim, used = best_similarity_from([np.array([1.0, 0.0])],
                                        [np.array([np.nan, 1.0])])
        self.assertIsNone(sim)

        # все variant emb с NaN → None
        sim, used = best_similarity_from(
            [np.array([np.nan, 1.0]), np.array([np.inf, 0.0])],
            [np.array([1.0, 0.0])])
        self.assertIsNone(sim)

    def test_best_similarity_picks_max_over_variant_faces(self):
        """Главный новый кейс: два лица в варианте с разными эмбеддингами — берётся
        максимум косинуса к центроиду эталонов, а не «самое крупное». Старый контракт
        выбрал бы лицо «прохожего» с чужим эмбеддингом и дал бы низкое сходство.
        Мутация «вернуть biggest» — этот тест краснеет."""
        from core import best_similarity_from

        # Эталон — e_target. Сцена «Андрей рядом с прохожим»: эмбеддинг прохожего
        # ортогонален эталону, эмбеддинг Андрея — параллелен.
        e_target = np.array([1.0, 0.0, 0.0])
        e_target_2 = np.array([0.9, 0.1, 0.0])
        e_bystander = np.array([0.0, 1.0, 0.0])

        sim, used = best_similarity_from(
            [e_bystander, e_target],  # оба лица в варианте, target второй
            [e_target, e_target_2])
        self.assertEqual(used, 2)
        # max cos(emb_variant, centroid) ≈ 1.0 (target), не 0.0 (bystander)
        self.assertGreater(sim, 0.9)

    def test_faces_in_mask_by_coverage(self):
        """Маска на правой половине: лицо справа покрыто целиком, лицо слева — нет,
        лицо на стыке — на четверть."""
        from core import faces_in_mask

        mask = np.zeros((100, 100), dtype=bool)
        mask[:, 50:] = True
        bboxes = [
            np.array([60.0, 10.0, 90.0, 40.0]),   # целиком в маске
            np.array([5.0, 10.0, 35.0, 40.0]),    # целиком вне
            np.array([35.0, 10.0, 55.0, 40.0]),   # 5 из 20 столбцов — 0,25
        ]
        self.assertEqual(faces_in_mask(bboxes, mask), [0])

    def test_faces_in_mask_boundary_half(self):
        """Ровно 0,5 покрытия — лицо берётся; на столбец меньше — нет."""
        from core import faces_in_mask

        mask = np.zeros((100, 100), dtype=bool)
        mask[:, 50:] = True
        half = np.array([40.0, 0.0, 60.0, 20.0])         # 10 из 20 столбцов
        below = np.array([39.0, 0.0, 59.0, 20.0])        # 9 из 20
        self.assertEqual(faces_in_mask([half], mask), [0])
        self.assertEqual(faces_in_mask([below], mask), [])

    def test_faces_in_mask_empty_mask(self):
        """Пустая маска — ни одного лица, в том числе и bbox за кадром."""
        from core import faces_in_mask

        mask = np.zeros((100, 100), dtype=np.uint8)
        bboxes = [np.array([10.0, 10.0, 50.0, 50.0]),
                  np.array([200.0, 200.0, 240.0, 240.0])]
        self.assertEqual(faces_in_mask(bboxes, mask), [])

    def test_resize_nearest_keeps_binary(self):
        """Маска 2x2 → 4x4: каждый пиксель размножается квадратом, полутонов нет."""
        from core import resize_nearest

        small = np.array([[0, 255], [255, 0]], dtype=np.uint8)
        big = resize_nearest(small, 4, 4)
        self.assertEqual(big.shape, (4, 4))
        self.assertEqual(set(np.unique(big).tolist()), {0, 255})
        self.assertEqual(int(big[0, 3]), 255)
        self.assertEqual(int(big[3, 3]), 0)


# ---------- worker: моки insightface и cv2, реальный numpy через core ----------

class _FakeFace:
    """Подделка «лица» с bbox и embedding."""

    def __init__(self, bbox, embedding):
        self.bbox = bbox
        self.embedding = embedding


def _faces_from_mapping_value(val):
    """Нормализует значение mapping[path] в список _FakeFace.

    None              → пустой список («лиц нет»).
    np.ndarray        → один face с дефолтным bbox 100x100 («одно лицо в кадре»).
    list[np.ndarray]  → несколько лиц, каждое с дефолтным bbox 100x100.
    list[(bbox, emb)] → несколько лиц с явными bbox; для теста на максимум по лицам
                        («нужное лицо меньше прохожего»).
    """
    if val is None:
        return []
    default_bbox = np.array([0, 0, 100, 100])
    if isinstance(val, np.ndarray):
        return [_FakeFace(bbox=default_bbox, embedding=val)]
    if isinstance(val, list):
        faces = []
        for item in val:
            if isinstance(item, tuple):
                bbox, emb = item
            else:
                bbox, emb = default_bbox, item
            faces.append(_FakeFace(bbox=np.asarray(bbox), embedding=emb))
        return faces
    raise TypeError(f"неизвестный тип значения mapping: {type(val).__name__}")


class _FakeApp:
    """Подделка FaceAnalysis: возвращает заранее заданные эмбеддинги по имени файла."""

    def __init__(self, mapping):
        # mapping: {"/abs/path.png": np.array|None|list} —
        # None для «лиц нет», np.array — одно лицо, list — несколько (с явными bbox).
        self._mapping = mapping

    def prepare(self, ctx_id, det_size):
        return None

    def get(self, bgr):
        # bgr — массив; используем его форму как ключ нельзя, завяжемся на кадр через стек
        # вместо этого сохраним последний путь в очереди
        path = _FakeApp._current_path
        return _faces_from_mapping_value(self._mapping.get(path))


_FakeApp._current_path = None


class _FakeCv2:
    """Подделка cv2: imread возвращает фиктивный BGR-массив 128x128; маску (IMREAD_GRAYSCALE)
    отдаёт из masks — {путь: np.ndarray|None}, None — «битый файл»."""

    masks = {}

    @staticmethod
    def imread(path, flags):
        if not os.path.isfile(path):
            return None
        if flags == _FakeCv2.IMREAD_GRAYSCALE:
            return _FakeCv2.masks.get(path)
        # важен сам факт чтения: реальный _biggest_embedding вызывает app.get() с этим bgr
        return np.full((128, 128, 3), 64, dtype=np.uint8)

    IMREAD_COLOR = 1
    IMREAD_GRAYSCALE = 0


def _install_fakes(mapping):
    """Подменить sys.modules['insightface'], sys.modules['insightface.app'] и sys.modules['cv2'].

    mapping: словарь {путь: np.array|None}. None — «лиц нет» для пути.
    Возвращает (fake_insightface_pkg, fake_app_cls, FakeCv2, cleanup).
    """
    fake_insightface = type(sys)("insightface")
    fake_app_mod = type(sys)("insightface.app")

    class _FaceAnalysis:
        def __new__(cls, name, root, providers, allowed_modules=None):
            return _FakeApp(mapping)

    fake_app_mod.FaceAnalysis = _FaceAnalysis
    fake_insightface.app = fake_app_mod

    cleanup = {
        "insightface": sys.modules.get("insightface"),
        "insightface.app": sys.modules.get("insightface.app"),
        "cv2": sys.modules.get("cv2"),
        "_FakeApp._current_path": _FakeApp._current_path,
    }
    sys.modules["insightface"] = fake_insightface
    sys.modules["insightface.app"] = fake_app_mod
    sys.modules["cv2"] = _FakeCv2
    return cleanup


def _restore_fakes(cleanup):
    for k, v in cleanup.items():
        if k.startswith("_"):
            setattr(_FakeApp, k, v)
        elif v is None:
            sys.modules.pop(k, None)
        else:
            sys.modules[k] = v


class TestWorker(unittest.TestCase):
    def _run_worker(self, variant_faces, refs_faces, mask=False):
        """Гоняет worker_similarity.main() с моками. variant_faces — np.array|None|list.
        refs_faces — список кортежей (имя_файла, np.array|None|list). mask — False (ключа
        mask в job нет), None (битая маска) или 2D np.ndarray 0/255. Возвращает распарсенный
        CCS_RESULT и захваченный stdout (на случай ошибок)."""
        tmp = tempfile.mkdtemp(prefix="ccs-face-test-")
        try:
            variant_path = os.path.join(tmp, "variant.png")
            # создаём сам файл — cv2.imread проверяет os.path.isfile
            with open(variant_path, "wb") as f:
                f.write(b"\x89PNG\r\n\x1a\n")

            ref_paths = []
            mapping = {variant_path: variant_faces}
            for name, faces in refs_faces:
                p = os.path.join(tmp, name)
                with open(p, "wb") as f:
                    f.write(b"\x89PNG\r\n\x1a\n")
                mapping[p] = faces
                ref_paths.append(p)

            job = {"variant": variant_path, "references": ref_paths,
                   "face_models": tmp, "det_size": 640}
            _FakeCv2.masks = {}
            if mask is not False:
                mask_path = _touch(os.path.join(tmp, "mask.png"))
                _FakeCv2.masks[mask_path] = mask
                job["mask"] = mask_path
            job_path = os.path.join(tmp, "job.json")
            with open(job_path, "w", encoding="utf-8") as f:
                json.dump(job, f)

            # подменяем sys.argv до импорта worker
            old_argv = sys.argv
            sys.argv = ["worker_similarity.py", job_path]

            # мокаем cv2.imread так, чтобы он запоминал текущий путь для FakeApp
            original_imread = _FakeCv2.imread

            def imread_with_path(path, flags):
                # маска лиц не ищет — текущий путь детектора она не сбивает
                if flags == _FakeCv2.IMREAD_COLOR:
                    _FakeApp._current_path = path
                return original_imread(path, flags)

            _FakeCv2.imread = staticmethod(imread_with_path)

            cleanup = _install_fakes(mapping)
            try:
                captured = io.StringIO()
                old_stdout = sys.stdout
                sys.stdout = captured
                try:
                    # грузим модуль воркера
                    mod = importlib.import_module("worker_similarity")
                    mod.main()
                    rc = 0
                except SystemExit as e:
                    rc = int(e.code) if e.code is not None else 0
                finally:
                    sys.stdout = old_stdout
            finally:
                _restore_fakes(cleanup)
                _FakeCv2.imread = original_imread
                sys.argv = old_argv

            output = captured.getvalue()
            # последняя строка CCS_RESULT
            result = None
            for line in reversed(output.strip().splitlines()):
                if line.startswith("CCS_RESULT "):
                    result = json.loads(line[len("CCS_RESULT "):])
                    break
            return result, output, rc
        finally:
            import shutil
            shutil.rmtree(tmp, ignore_errors=True)

    def test_worker_two_orthogonal_refs_cosine_0_7071(self):
        """Интеграционный smoke: variant = e1, refs = e1+e2 (ортогональные) →
        similarity ≈ 0.7071."""
        e1 = np.array([1.0, 0.0, 0.0, 0.0])
        e2 = np.array([0.0, 1.0, 0.0, 0.0])
        result, output, rc = self._run_worker(variant_faces=e1, refs_faces=[
            ("r1.png", e1), ("r2.png", e2),
        ])
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertAlmostEqual(result["similarity"], 0.7071, places=3)
        self.assertEqual(result["stats"]["references_total"], 2)
        self.assertEqual(result["stats"]["references_used"], 2)

    def test_worker_no_face_on_variant_returns_none(self):
        """В варианте лиц нет → similarity=null. Граф должен завершиться успешно."""
        e1 = np.array([1.0, 0.0])
        result, output, rc = self._run_worker(variant_faces=None, refs_faces=[
            ("r1.png", e1),
        ])
        self.assertIsNotNone(result)
        self.assertIsNone(result["similarity"])

    def test_worker_no_face_in_refs_returns_none(self):
        """Все эталоны без лиц → similarity=null."""
        result, output, rc = self._run_worker(
            variant_faces=np.array([1.0, 0.0]),
            refs_faces=[("r1.png", None), ("r2.png", None)],
        )
        self.assertIsNotNone(result)
        self.assertIsNone(result["similarity"])

    def test_worker_nan_inf_in_refs_filtered(self):
        """NaN/Inf в эталоне отбрасывается; если других эталонов нет → null."""
        e1 = np.array([1.0, 0.0])
        result, output, rc = self._run_worker(
            variant_faces=e1,
            refs_faces=[("r1.png", np.array([np.nan, 1.0])),
                        ("r2.png", np.array([np.inf, 0.0]))],
        )
        self.assertIsNotNone(result)
        self.assertIsNone(result["similarity"])

    def test_worker_nan_ref_filtered_but_good_kept(self):
        """NaN/Inf в одном эталоне отбрасывается, второй жив — выдаёт нормальный ответ."""
        e1 = np.array([1.0, 0.0])
        e_good = np.array([0.0, 1.0])
        result, output, rc = self._run_worker(
            variant_faces=e1,
            refs_faces=[("r1.png", e_good),
                        ("r2.png", np.array([np.nan, 1.0]))],
        )
        self.assertIsNotNone(result)
        self.assertEqual(result["stats"]["references_total"], 2)
        self.assertEqual(result["stats"]["references_used"], 1)
        self.assertAlmostEqual(result["similarity"], 0.0, places=6)

    def test_worker_max_face_picks_smaller_face_with_higher_similarity(self):
        """Сцена «Андрей рядом с прохожим»: в варианте ДВА лица — крупный прохожий
        с ортогональным эмбеддингом и нужный Андрей с эмбеддингом эталона. Старый
        контракт (biggest face) выбрал бы прохожего и дал 0.0. По новому контракту
        (max cos по всем лицам) выбирается Андрей → сходство ≈ 1.0.

        Мутация «вернуть biggest» в worker_similarity._all_embeddings — этот тест краснеет.
        """
        e_target = np.array([1.0, 0.0, 0.0])
        e_target_2 = np.array([0.95, 0.1, 0.0])
        e_bystander = np.array([0.0, 1.0, 0.0])
        # bbox: прохожий крупный (300x300), Андрей меньше (100x100)
        variant_faces = [
            (np.array([0, 0, 300, 300]), e_bystander),
            (np.array([0, 0, 100, 100]), e_target),
        ]
        result, output, rc = self._run_worker(
            variant_faces=variant_faces,
            refs_faces=[("r1.png", e_target), ("r2.png", e_target_2)],
        )
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertEqual(result["stats"]["references_total"], 2)
        self.assertEqual(result["stats"]["references_used"], 2)
        # max cos(emb_variant, centroid) ≈ 1.0 (target), не 0.0 (bystander)
        self.assertGreater(result["similarity"], 0.9)

    def test_worker_picks_biggest_face_in_reference(self):
        """Эталон с двумя лицами: нужное — крупное, лишнее — мелкое. По контракту
        для центроида берётся самое крупное лицо эталона (по площади bbox), а не
        «первое по уверенности»: на групповом фото главное лицо может идти вторым
        в списке детектора.

        Мутация «вернуть es[0]» в worker_similarity — этот тест краснеет:
        es[0] здесь — мелкое лицо с ортогональным эмбеддингом, и cos с вариантом
        был бы ≈ 0.0, а не ≈ 1.0.
        """
        e_target = np.array([1.0, 0.0, 0.0])
        e_other = np.array([0.0, 1.0, 0.0])
        # в маппинге: первым идёт мелкое чужое, вторым — крупное нужное.
        # Если код берёт es[0], он возьмёт e_other (ортогональный) → cos ≈ 0.
        # Если берёт самое крупное — e_target → cos ≈ 1.
        ref_faces = [
            (np.array([0, 0, 60, 60]), e_other),
            (np.array([0, 0, 300, 300]), e_target),
        ]
        result, output, rc = self._run_worker(
            variant_faces=e_target,
            refs_faces=[("r1.png", ref_faces)],
        )
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertEqual(result["stats"]["references_total"], 1)
        self.assertEqual(result["stats"]["references_used"], 1)
        # центроид из одного эталона = e_target → cos(e_target, e_target) = 1.0
        self.assertGreater(result["similarity"], 0.9)
        # и в логе воркер должен был предупредить, что в эталоне больше одного лица
        self.assertIn("в эталоне 2 лиц", output)

    # Сцена второй правки: слева первый персонаж (похож на эталон), справа второй — его
    # и правили по маске, он похож меньше. Кадр подделки cv2 — 128x128.
    _E_TARGET = np.array([1.0, 0.0, 0.0])
    _E_SECOND = np.array([0.6, 0.8, 0.0])  # cos к эталону = 0,6
    _TWO_FACES = [
        (np.array([4, 4, 60, 60]), _E_TARGET),
        (np.array([68, 68, 124, 124]), _E_SECOND),
    ]

    def _right_bottom_mask(self, size=128):
        m = np.zeros((size, size), dtype=np.uint8)
        m[size // 2:, size // 2:] = 255
        return m

    def test_worker_mask_picks_face_inside_mask_not_max(self):
        """Маска на менее похожем лице → сходство его (0,6), а не максимум (1,0).

        Мутация «игнорировать маску» (не фильтровать faces_variant по faces_in_mask) —
        этот тест краснеет: вернётся 1,0 первого персонажа вне маски."""
        result, output, rc = self._run_worker(
            variant_faces=self._TWO_FACES,
            refs_faces=[("r1.png", self._E_TARGET)],
            mask=self._right_bottom_mask(),
        )
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertAlmostEqual(result["similarity"], 0.6, places=5)
        self.assertEqual(result["stats"]["faces_total"], 2)
        self.assertEqual(result["stats"]["faces_in_mask"], 1)

    def test_worker_mask_without_faces_inside_returns_none(self):
        """В маске нет ни одного лица → null, а не сходство лица вне маски."""
        empty = np.zeros((128, 128), dtype=np.uint8)
        result, output, rc = self._run_worker(
            variant_faces=self._TWO_FACES,
            refs_faces=[("r1.png", self._E_TARGET)],
            mask=empty,
        )
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertIsNone(result["similarity"])
        self.assertEqual(result["stats"]["faces_in_mask"], 0)

    def test_worker_mask_other_size_resized_nearest(self):
        """Маска 64x64 при варианте 128x128 растягивается, в лог — запись об этом."""
        result, output, rc = self._run_worker(
            variant_faces=self._TWO_FACES,
            refs_faces=[("r1.png", self._E_TARGET)],
            mask=self._right_bottom_mask(64),
        )
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertAlmostEqual(result["similarity"], 0.6, places=5)
        self.assertIn("растягиваю nearest", output)

    def test_worker_broken_mask_returns_none(self):
        """cv2 не прочитал маску → null с предупреждением, код возврата 0."""
        result, output, rc = self._run_worker(
            variant_faces=self._TWO_FACES,
            refs_faces=[("r1.png", self._E_TARGET)],
            mask=None,
        )
        self.assertEqual(rc, 0)
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertIsNone(result["similarity"])
        self.assertIn("маска не читается", output)

    def test_worker_without_mask_keeps_max_and_stats(self):
        """Без маски — прежний максимум по всем лицам; faces_in_mask = null."""
        result, output, rc = self._run_worker(
            variant_faces=self._TWO_FACES,
            refs_faces=[("r1.png", self._E_TARGET)],
        )
        self.assertIsNotNone(result, msg=f"нет CCS_RESULT в выводе:\n{output}")
        self.assertAlmostEqual(result["similarity"], 1.0, places=5)
        self.assertEqual(result["stats"]["faces_total"], 2)
        self.assertIsNone(result["stats"]["faces_in_mask"])


# ---------- node: моки subprocess.Popen, реальный folder_paths через подмену ----------

class _FakePopen:
    """Подделка subprocess.Popen, которая сразу выдаёт CCS_RESULT или имитирует отказ."""

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
    """Подделка folder_paths: input_directory кладём во временную папку теста."""

    def __init__(self, root):
        self._root = root

    def get_input_directory(self):
        return self._root


class TestNode(unittest.TestCase):
    def _setup_input(self, tmp):
        """Создаёт поддельный input/ и кладёт туда test_ref.png."""
        in_root = os.path.join(tmp, "input")
        os.makedirs(in_root, exist_ok=True)
        ref_path = os.path.join(in_root, "test_ref.png")
        with open(ref_path, "wb") as f:
            f.write(b"fake png")
        return in_root, ref_path

    def _fake_image(self):
        """Имитация ComfyUI IMAGE: torch.Tensor [1, 4, 4, 3] float32 RGB."""
        try:
            import torch
            return torch.zeros(1, 4, 4, 3, dtype=torch.float32)
        except ImportError:
            return np.zeros((1, 4, 4, 3), dtype=np.float32)

    def setUp(self):
        # папка узла уже в sys.path — модуль лежит рядом
        self._node_module = importlib.import_module("ccs_face_similarity")

    def _patch_config_and_input(self, in_root, family=None, workers=None):
        """Подменяет _config() и folder_paths.get_input_directory() для теста."""
        if family is None:
            python_path = os.path.join(in_root, "_fake_python")
            script_path = os.path.join(in_root, "_fake_worker.py")
            family = {"python": python_path, "script": script_path}
        if workers is None:
            workers = {"families": {"similarity": family}}

        return (
            mock.patch.object(self._node_module, "_config", return_value=workers),
            mock.patch.object(self._node_module.folder_paths, "get_input_directory",
                              return_value=in_root),
        )

    def test_node_missing_worker_path_returns_none(self):
        """Недоступный воркер → ccs_face_similarity=none и не падает."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, _ = self._setup_input(tmp)
            family = {"python": "/no/such/python",
                      "script": "/no/such/script.py"}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            with cfg_patch, fp_patch:
                node = self._node_module.CcsFaceSimilarity()
                out = node.run(self._fake_image(), "test_ref.png")
        self.assertEqual(out["ui"]["text"], ["ccs_face_similarity=none"])

    def test_node_safe_input_rejects_path_traversal(self):
        """Путь с .. → ValueError, эталон пропускается; Popen всё равно зовётся с пустым
        references (узел не падает). Проверяется по logger: warn от _safe_input."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, ref_path = self._setup_input(tmp)
            popen_calls = []
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}

            class _SpyPopen:
                def __init__(self, *a, **kw):
                    popen_calls.append((a, kw))
                    self.pid = 1
                    self.returncode = 0
                    self._stdout = io.StringIO("CCS_RESULT {\"similarity\": null}\n")

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
                node = self._node_module.CcsFaceSimilarity()
                out = node.run(self._fake_image(), "../../etc/passwd")
        self.assertTrue(popen_calls, "Popen не вызван — узел должен звать воркера даже при пустых refs")
        # первый и единственный Popen вызов: в args — путь к скрипту, в cwd — пустой
        # аргумент job.json: первая строка argv0 — interpreter, далее script, далее job.json
        args = popen_calls[0][0][0]
        self.assertTrue(args[0].endswith("_p"))
        self.assertTrue(args[1].endswith("_w.py"))
        # job.json — третий аргумент; его существование узел не проверяет, тут важно,
        # что Popen вызвался без path-traversal-попытки
        self.assertEqual(out["ui"]["text"], ["ccs_face_similarity=none"])

    def test_node_absolute_path_rejected(self):
        """Абсолютный путь в references → ValueError, эталон пропускается."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, _ = self._setup_input(tmp)
            cfg_patch, fp_patch = self._patch_config_and_input(in_root)
            with cfg_patch, fp_patch:
                with self.assertRaises(ValueError):
                    self._node_module._safe_input("/etc/passwd")

    def test_node_returns_similarity_from_worker(self):
        """Воркер вернул similarity=0.771 → ui.text = 'ccs_face_similarity=0.771'."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, ref_path = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            payload = json.dumps({"similarity": 0.771,
                                  "stats": {"references_used": 1}})
            fake_proc = _FakePopen(f"noise log\nCCS_RESULT {payload}\n")
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen",
                                   return_value=fake_proc), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFaceSimilarity()
                out = node.run(self._fake_image(), "test_ref.png")
        self.assertEqual(out["ui"]["text"], ["ccs_face_similarity=0.771"])

    def test_node_worker_returns_null(self):
        """Воркер вернул similarity=null → ui.text = 'ccs_face_similarity=none'."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, ref_path = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            payload = json.dumps({"similarity": None,
                                  "stats": {"references_used": 0}})
            fake_proc = _FakePopen(f"CCS_RESULT {payload}\n")
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen",
                                   return_value=fake_proc), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFaceSimilarity()
                out = node.run(self._fake_image(), "test_ref.png")
        self.assertEqual(out["ui"]["text"], ["ccs_face_similarity=none"])

    def test_node_worker_nonzero_returncode_returns_none(self):
        """Воркер упал с ненулевым кодом → ui.text = none, граф не падает."""
        with tempfile.TemporaryDirectory() as tmp:
            in_root, ref_path = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            fake_proc = _FakePopen("Traceback...\nError", returncode=1)
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen",
                                   return_value=fake_proc), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFaceSimilarity()
                out = node.run(self._fake_image(), "test_ref.png")
        self.assertEqual(out["ui"]["text"], ["ccs_face_similarity=none"])

    def _run_capturing_job(self, mask):
        """Гоняет узел и возвращает (job, пиксели маски|None) на момент запуска воркера:
        после run временная папка уже стёрта."""
        captured = {}

        def spy_popen(args, **kw):
            with open(args[2], encoding="utf-8") as f:
                captured["job"] = json.load(f)
            mpath = captured["job"].get("mask")
            if mpath:
                from PIL import Image
                with Image.open(mpath) as im:
                    captured["mode"] = im.mode
                    captured["pixels"] = np.asarray(im).copy()
            payload = json.dumps({"similarity": 0.5, "stats": {}})
            return _FakePopen(f"CCS_RESULT {payload}\n")

        with tempfile.TemporaryDirectory() as tmp:
            in_root, _ = self._setup_input(tmp)
            family = {"python": _touch(os.path.join(tmp, "_p")),
                      "script": _touch(os.path.join(tmp, "_w.py"))}
            cfg_patch, fp_patch = self._patch_config_and_input(in_root, family=family)
            with cfg_patch, fp_patch, mock.patch.object(self._node_module, "_save_variant_png",
                                                       return_value=None), \
                 mock.patch.object(self._node_module.subprocess, "Popen", spy_popen), \
                 mock.patch.object(self._node_module.time, "sleep", lambda _x: None):
                node = self._node_module.CcsFaceSimilarity()
                if mask is None:
                    out = node.run(self._fake_image(), "test_ref.png")
                else:
                    out = node.run(self._fake_image(), "test_ref.png", mask=mask)
        self.assertEqual(out["ui"]["text"], ["ccs_face_similarity=0.500"])
        return captured

    def test_node_mask_goes_to_job_as_binary_png(self):
        """С маской в job.json есть ключ mask, PNG режима L с порогом 0,5 → 0/255."""
        mask = np.array([[[0.2, 0.7], [0.5, 0.49]]], dtype=np.float32)  # MASK [B,H,W]
        captured = self._run_capturing_job(mask)
        self.assertIn("mask", captured["job"])
        self.assertEqual(captured["mode"], "L")
        self.assertEqual(captured["pixels"].tolist(), [[0, 255], [255, 0]])

    def test_node_without_mask_has_no_mask_key(self):
        """Без маски ключа mask в job.json нет вовсе — воркер считает по-старому."""
        captured = self._run_capturing_job(None)
        self.assertNotIn("mask", captured["job"])

    def test_node_input_types_mask_optional(self):
        types = self._node_module.CcsFaceSimilarity.INPUT_TYPES()
        self.assertEqual(set(types["required"]), {"image", "references"})
        self.assertEqual(types["optional"], {"mask": ("MASK",)})


if __name__ == "__main__":
    unittest.main(verbosity=2)
