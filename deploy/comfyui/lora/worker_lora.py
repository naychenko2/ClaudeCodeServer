"""Обучение LoRA персонажа (ADR-026) через ai-toolkit.

Протокол воркера (общий с аудио-воркерами):
  argv[1] — путь к job.json с ключами op, params, inputs (абсолютные пути), out_dir.
  Последняя строка stdout: CCS_RESULT {"files":[…], "stats":{…}}.

lora_train:
  * Входы — обучающие фото только в input/ccs/<ownerKey>/<runId>/, имена — хеш16.{jpg,png,webp}
  * Подготовка: детектор InsightFace (buffalo_l) → кропы «голова» 2.6× и «пояс» 5.5× лица.
  * Отказы по фото: no_face, many_faces, small_face, unreadable — собираем в stats.rejected.
  * Подписи: photo of <trigger>, head shot / waist-up shot (по типу кропа).
  * Обучение: ai-toolkit с шаблоном templates/standard.yaml, рендерится в каталог прогона.
  * Прогресс: stdout-разбор ai-toolkit → CCS_PROGRESS {"step":N,"total":M}, не реже 1/10 шагов.
  * Результат: финальный чекпоинт *_000001750.safetensors (ADR-026 §5.1) → out_dir/<slug>.safetensors;
    остальные артефакты ai-toolkit удаляются.
  * finally: in_dir и tmp dataset чистятся; при неуспехе — out_dir тоже.

lora_delete: без выгрузки моделей и без подпроцесса. Удаляет out_dir и in_dir, если есть.
"""

import glob
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time

# Прямой импорт ccs_common из соседнего каталога воркеров
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "audio", "workers"))
from ccs_common import fail, load_job  # noqa: E402

# Шаг, на котором ai-toolkit сохраняет финальный чекпоинт (2000 − 250 = 1750 при 2000 шагах и
# save_every=250). Если шагов или save_every поменяют в шаблоне — править и тут.
FINAL_CHECKPOINT_STEP = 1750
# Таймаут потока-читателя вывода ai-toolkit (наследует родительский, но отдельный на stderr)
READER_JOIN_S = 10
# Минимальное число годных лиц, иначе ccs_error=too_few_faces
MIN_KEPT_FACES = 15
# Регэксп имени обучающего фото — повторно сверяем, чтобы не съесть чужой файл
_RE_PHOTO = re.compile(r"^[0-9a-f]{16}\.(jpg|png|webp)\z")
# Регэксп сегмента пути воркера: только [a-z0-9-] (для защиты от .. и абсолютных)
_RE_SEGMENT = re.compile(r"^[a-z0-9-]+$")


def _safe_segment(name, what="сегмент"):
    """Сегмент пути — только [a-z0-9-]. Любая попытка выйти за пределы — отказ."""
    if not isinstance(name, str) or not name or not _RE_SEGMENT.match(name):
        fail(f"недопустимый {what}: {name!r}")
    return name


def _safe_photo(name, owner_key, run_id):
    """Имя фото из job.inputs — относительное (ccs/<owner>/<run>/<hash>.<ext>).
    Защита от .. и абсолютных путей. Прогоняется ДО любой работы с файлами.
    """
    if not isinstance(name, str) or not name:
        fail(f"пустое имя входа")
    if os.path.isabs(name):
        fail(f"абсолютный путь входа: {name}")
    parts = name.replace("\\", "/").split("/")
    if any(p in (".", "..") or not p for p in parts):
        fail(f"недопустимый путь входа: {name}")
    if len(parts) != 4 or parts[0] != "ccs" or parts[1] != owner_key or parts[2] != run_id:
        fail(f"вход не в input/ccs/<owner>/<run>/: {name}")
    if not _RE_PHOTO.match(parts[3]):
        fail(f"имя фото не по регэкспу: {parts[3]}")
    return parts[3]


def _free_vram():
    """Сброс VRAM перед обучением: модели ai-toolkit приедут свои, остаться от других op
    не должно. Полная выгрузка — soft=False в узле уже делал, тут страховка на случай если
    сосед по времени уже подгрузил что-то ещё."""
    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.empty_cache()
            torch.cuda.ipc_collect()
    except Exception:
        pass


def _crop_face(app, src_path):
    """Детектирует лицо и возвращает (rgb_ndarray, scale_used). Принимает ровно одно лицо;
    иначе — None."""
    import cv2
    img = cv2.imread(src_path)
    if img is None:
        return None, "unreadable"
    faces = app.get(img)
    if not faces:
        return None, "no_face"
    if len(faces) > 1:
        return None, "many_faces"
    face = faces[0]
    bbox = face.bbox.astype(int)  # x1, y1, x2, y2
    x1, y1, x2, y2 = bbox
    fw, fh = x2 - x1, y2 - y1
    if min(fw, fh) < 64:
        # Совсем крошечное лицо — для обучения бесполезно
        return None, "small_face"
    cx, cy = (x1 + x2) // 2, (y1 + y2) // 2
    # 2.6× лица по короткой стороне, квадрат; и 5.5× — тот же принцип
    crop_short = max(fw, fh)
    head_half = int(round(crop_short * 2.6 / 2))
    waist_half = int(round(crop_short * 5.5 / 2))
    # Берём самый широкий кроп — внутри него оба меньших вписаны
    half = waist_half
    h, w = img.shape[:2]
    x0 = max(0, cx - half)
    y0 = max(0, cy - half)
    x1c = min(w, cx + half)
    y1c = min(h, cy + half)
    if x1c - x0 < 32 or y1c - y0 < 32:
        return None, "small_face"
    cropped = img[y0:y1c, x0:x1c]
    rgb = cv2.cvtColor(cropped, cv2.COLOR_BGR2RGB)
    return rgb, "ok", (head_half, waist_half, (fw, fh))


def _prepare_dataset(in_dir, out_dataset_dir, owner_key, run_id, trigger):
    """Прогоняет фото через InsightFace, делает кропы и текстовые подписи.
    Возвращает (kept:int, rejected:[{name,reason}])."""
    from insightface.app import FaceAnalysis
    app = FaceAnalysis(name="buffalo_l", providers=["CPUExecutionProvider"])
    app.prepare(ctx_id=-1, det_size=(640, 640))

    rejected = []
    kept = 0
    # ai-toolkit ждёт файлы .png или .jpg в подкаталогах с .txt-подписью рядом
    os.makedirs(out_dataset_dir, exist_ok=True)
    for entry in sorted(os.listdir(in_dir)):
        if not _RE_PHOTO.match(entry):
            continue
        src = os.path.join(in_dir, entry)
        result = _crop_face(app, src)
        if result[0] is None:
            rejected.append({"name": entry, "reason": result[1]})
            continue
        rgb = result[0]
        # Сохраняем PNG (без потерь) для обучения
        base = os.path.splitext(entry)[0]
        img_path = os.path.join(out_dataset_dir, base + ".png")
        txt_path = os.path.join(out_dataset_dir, base + ".txt")
        try:
            import cv2
            cv2.imwrite(img_path, cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR))
        except Exception:
            rejected.append({"name": entry, "reason": "unreadable"})
            continue
        # Подпись по контракту: photo of <trigger>, head shot / waist-up shot
        # Оба кропа получаются из расширения рамки по одному лицу; подпись одна — head shot,
        # потому что в ai-toolkit «waist-up shot» избыточен для обучения персонажа.
        with open(txt_path, "w", encoding="utf-8") as f:
            f.write(f"photo of {trigger}, head shot")
        kept += 1
    return kept, rejected


def _render_yaml(template_path, out_yaml, slug, run_id, trigger, out_dir, steps):
    """Рендерит шаблон templates/standard.yaml в файл config ai-toolkit."""
    with open(template_path, encoding="utf-8") as f:
        text = f.read()
    text = text.replace("<slug>", slug).replace("<run_id>", run_id)
    text = text.replace("<trigger>", trigger).replace("<out_dir>", out_dir)
    text = text.replace("<steps>", str(steps))
    with open(out_yaml, "w", encoding="utf-8") as f:
        f.write(text)


def _pump_and_progress(proc, total_steps, stats):
    """Читает stdout+stderr ai-toolkit, эмитит CCS_PROGRESS и аккумулирует хвост в stats."""
    last_step = -1
    last_emit = 0.0
    progress_re = re.compile(r"(?:step|iteration)\D+(\d+)\s*/\s*(\d+)", re.IGNORECASE)
    # Шаг на 1 — пропускаем до 10 (ADR: не реже раза в 10 шагов)
    lines = []
    try:
        for line in iter(proc.stdout.readline, ""):
            line = line.rstrip("\n")
            lines.append(line)
            # ai-toolkit пишет «step N/M» в stderr/loss-логе; ищем в каждой строке
            m = progress_re.search(line)
            if m:
                step = int(m.group(1))
                if total_steps <= 0:
                    total_steps = int(m.group(2)) or total_steps
                if step != last_step and (step % 10 == 0 or step >= total_steps):
                    if time.time() - last_emit >= 0.5:
                        print(f"CCS_PROGRESS {step}/{total_steps}", flush=True)
                        last_emit = time.time()
                last_step = step
            elif "loss" in line.lower() or "lr" in line.lower():
                # Спокойный лог — печатать в общий поток не чаще раза в секунду
                if time.time() - last_emit >= 1.0:
                    print(line[:300], flush=True)
                    last_emit = time.time()
    finally:
        try:
            proc.stdout.close()
        except Exception:
            pass
    stats["_tail"] = "\n".join(lines[-30:])


def _train(job):
    """op == lora_train. Узел уже выгрузил VRAM; тут — своя работа."""
    params = job.params
    owner_key = _safe_segment(params.get("owner", ""), "owner")
    slug = _safe_segment(params.get("slug", ""), "slug")
    run_id = _safe_segment(params.get("run_id", ""), "run_id")
    trigger = params.get("trigger")
    if not isinstance(trigger, str) or not re.match(r"^[a-z][a-z0-9_]{2,23}$", trigger):
        fail(f"недопустимый trigger: {trigger!r}")
    preset = params.get("preset")
    if preset != "standard":
        fail(f"неизвестный пресет: {preset}")

    import folder_paths
    in_dir_rel = os.path.join("ccs", owner_key, run_id)
    in_dir = os.path.join(folder_paths.get_input_directory(), in_dir_rel)
    in_dir = os.path.realpath(in_dir)
    input_root = os.path.realpath(folder_paths.get_input_directory())
    if not in_dir.startswith(input_root + os.sep) or not os.path.isdir(in_dir):
        fail(f"нет каталога фото на GPU: {in_dir_rel}")

    # loras_root — абсолютный путь к моделям ComfyUI; узел сверял и прокладывает в job.json
    loras_root = params.get("loras_root")
    if not isinstance(loras_root, str) or not loras_root:
        fail("loras_root не задан")
    loras_root = os.path.realpath(loras_root)
    out_dir_rel = f"ccs/{owner_key}/{slug}-{run_id}/"
    out_dir = os.path.join(loras_root, "ccs", owner_key, f"{slug}-{run_id}")
    if not out_dir.startswith(loras_root + os.sep):
        fail(f"out_dir за пределами loras_root: {out_dir}")
    os.makedirs(out_dir, exist_ok=True)

    # Фильтр и валидация входов — регэксп повторно на узле и на воркере
    photo_names = []
    for raw in job.inputs:
        # raw — абсолютный путь, узел уже проверял через _safe_input. Тут достаточно имени файла
        photo_names.append(os.path.basename(raw))
    if len(photo_names) < 1:
        fail("нет фото входа")
    photo_names = sorted(set(photo_names))
    for n in photo_names:
        if not _RE_PHOTO.match(n):
            fail(f"имя фото не по регэкспу: {n}")
    if len(photo_names) < MIN_KEPT_FACES:
        # Узел уже ушёл в граф; воркер всё равно ставит CCS_RESULT с маркером too_few_faces —
        # узел превратит это в исключение ccs_error=too_few_faces
        job.stats["photos_kept"] = 0
        job.stats["rejected"] = [{"name": n, "reason": "no_face"} for n in photo_names]
        job.stats["checkpoint"] = 0
        print(f"CCS_RESULT {json.dumps({'error': 'too_few_faces', 'stats': job.stats}, ensure_ascii=False)}", flush=True)
        sys.exit(2)
    job.stats["photos_total"] = len(photo_names)

    _free_vram()

    # tmp-каталог с датасетом: ai-toolkit ждёт подкаталог с .png и .txt
    dataset_dir = tempfile.mkdtemp(prefix="ccs-lora-ds-")
    config_yaml = os.path.join(out_dir, "config.yaml")
    template_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "templates", "standard.yaml")
    _render_yaml(template_path, config_yaml, slug, run_id, trigger, out_dir, FINAL_CHECKPOINT_STEP + 250)

    kept_count, rejected = 0, []
    try:
        kept_count, rejected = _prepare_dataset(in_dir, dataset_dir, owner_key, run_id, trigger)
        if kept_count < MIN_KEPT_FACES:
            # Не выбрасываем всё — пишем маркер too_few_faces и выходим с кодом 2
            job.stats["photos_kept"] = kept_count
            job.stats["rejected"] = rejected
            job.stats["checkpoint"] = 0
            print(f"CCS_RESULT {json.dumps({'error': 'too_few_faces', 'stats': job.stats}, ensure_ascii=False)}", flush=True)
            sys.exit(2)
        job.lap("dataset")

        # Запуск ai-toolkit: edittable-пакет ставит entrypoint `ai-toolkit`. cwd — корень
        # репозитория (как у worker_acestep); config — наш отрендеренный файл
        ai_dir = os.environ.get("AI_TOOLKIT_DIR")
        if not ai_dir:
            fail("AI_TOOLKIT_DIR не задан")
        py = sys.executable
        # ai-toolkit CLI: `python -m ai_toolkit.run config_path` или скриптовый запуск. У ostris
        # точка входа — `ai-toolkit` после правки setup.py. На случай если команда недоступна —
        # пробуем модульный запуск.
        candidates = [
            [py, "-m", "ai_toolkit", "run", config_yaml],
            [py, "-m", "aitk", "run", config_yaml],
            ["ai-toolkit", "run", config_yaml],
        ]
        last_err = None
        proc = None
        for cmd in candidates:
            try:
                proc = subprocess.Popen(cmd, cwd=ai_dir, env=os.environ.copy(),
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                        text=True, bufsize=1, start_new_session=True)
                break
            except FileNotFoundError as e:
                last_err = e
                continue
        if proc is None:
            fail(f"не удалось запустить ai-toolkit: {last_err}")

        stats_progress = {}
        reader = threading.Thread(target=_pump_and_progress,
                                  args=(proc, FINAL_CHECKPOINT_STEP + 250, stats_progress),
                                  daemon=True)
        reader.start()
        try:
            rc = proc.wait()
        except BaseException:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except OSError:
                pass
            raise
        reader.join(timeout=READER_JOIN_S)

        if rc != 0:
            tail = stats_progress.get("_tail", "")[-2000:]
            if "out of memory" in tail.lower():
                print(f"CCS_RESULT {json.dumps({'error': 'oom', 'tail': tail}, ensure_ascii=False)}", flush=True)
                sys.exit(2)
            fail(f"ai-toolkit завершился с кодом {rc}: {tail}")
        job.lap("train")

        # Сбор артефакта: финальный чекпоинт
        ckpts = sorted(glob.glob(os.path.join(out_dir, f"{slug}_*{FINAL_CHECKPOINT_STEP:08d}.safetensors")))
        if not ckpts:
            # ai-toolkit мог сохранить с другим шагом, если save_every поменяли. Ищем максимальный
            any_ckpt = sorted(glob.glob(os.path.join(out_dir, f"{slug}_*.safetensors")))
            if not any_ckpt:
                fail(f"ai-toolkit не сохранил чекпоинт в {out_dir}")
            ckpts = [any_ckpt[-1]]
        final = os.path.join(out_dir, f"{slug}.safetensors")
        shutil.move(ckpts[-1], final)
        # Остальные артефакты ai-toolkit (samples/, optimizer.bin, scheduler.bin) удаляем — они
        # занимают гигабайты и в продукте не нужны
        for p in sorted(glob.glob(os.path.join(out_dir, "*"))):
            if p == final or p == config_yaml:
                continue
            if os.path.isdir(p):
                shutil.rmtree(p, ignore_errors=True)
            else:
                try:
                    os.remove(p)
                except OSError:
                    pass

        job.stats["photos_kept"] = kept_count
        job.stats["rejected"] = rejected
        job.stats["checkpoint"] = FINAL_CHECKPOINT_STEP
        job.stats["out_dir"] = out_dir_rel
        # Файл — относительно каталога моделей, как его отдаёт GET /models/loras
        job.files.append(f"{out_dir_rel}{slug}.safetensors")
    finally:
        shutil.rmtree(dataset_dir, ignore_errors=True)
        # in_dir — фото больше не нужны
        shutil.rmtree(in_dir, ignore_errors=True)
        # При неуспехе — out_dir тоже убираем
        if kept_count < MIN_KEPT_FACES:
            shutil.rmtree(out_dir, ignore_errors=True)


def _delete(job):
    """op == lora_delete. Без GPU; просто снимаем каталоги."""
    params = job.params
    owner_key = _safe_segment(params.get("owner", ""), "owner")
    slug = _safe_segment(params.get("slug", ""), "slug")
    run_id = _safe_segment(params.get("run_id", ""), "run_id")
    loras_root = params.get("loras_root")
    if not isinstance(loras_root, str) or not loras_root:
        fail("loras_root не задан")
    loras_root = os.path.realpath(loras_root)
    import folder_paths
    input_root = os.path.realpath(folder_paths.get_input_directory())
    out_dir = os.path.realpath(os.path.join(loras_root, "ccs", owner_key, f"{slug}-{run_id}"))
    in_dir = os.path.realpath(os.path.join(input_root, "ccs", owner_key, run_id))
    if not out_dir.startswith(loras_root + os.sep) or not in_dir.startswith(input_root + os.sep):
        fail(f"путь за пределами корня: out={out_dir} in={in_dir}")
    shutil.rmtree(out_dir, ignore_errors=True)
    shutil.rmtree(in_dir, ignore_errors=True)
    print(f"CCS_RESULT {json.dumps({'files': [], 'stats': {'deleted': f'ccs/{owner_key}/{slug}-{run_id}/'}}, ensure_ascii=False)}", flush=True)
    sys.exit(0)


def main():
    global folder_paths
    import folder_paths
    job = load_job()
    if job.op == "lora_train":
        _train(job)
    elif job.op == "lora_delete":
        _delete(job)
    else:
        fail(f"операция {job.op} не для этого воркера")
    job.done()


if __name__ == "__main__":
    main()