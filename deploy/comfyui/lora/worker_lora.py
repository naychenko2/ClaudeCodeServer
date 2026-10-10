"""Обучение LoRA персонажа (ADR-026) через ai-toolkit.

Протокол воркера (общий с аудио-воркерами):
  argv[1] — путь к job.json с ключами op, params, inputs (абсолютные пути), out_dir.
  Последняя строка stdout: CCS_RESULT {"files":[…], "stats":{…}}.

lora_train:
  * Входы — обучающие фото только в input/ccs/<ownerKey>/<runId>/, имена — хеш16.{jpg,png,webp}
  * Подготовка: детектор InsightFace (buffalo_l) → кропы лица 5.5× по короткой стороне
    (внутри вписаны «голова» 2.6×, оба варианта по контракту ADR-026 §5.1).
  * Отказы по фото: no_face, many_faces, small_face, unreadable — собираем в stats.rejected.
  * Подписи: photo of <trigger>, head shot.
  * Обучение: ai-toolkit с шаблоном templates/standard.yaml, рендерится в out_dir.
  * Прогресс: stdout-разбор ai-toolkit → CCS_PROGRESS {"step":N,"total":M}, не реже 1/10 шагов.
  * Результат: финальный чекпоинт *_000001750.safetensors (ADR-026 §5.1) → out_dir/<slug>.safetensors;
    остальные артефакты ai-toolkit удаляются.

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

# Прямой импорт ccs_common из общего каталога воркеров ($DEPLOY/workers). Когда воркер лежит в
# $DEPLOY/lora/ на машине, общий каталог воркеров — на уровень выше в workers/
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "workers"))
from ccs_common import fail, load_job  # noqa: E402

# Шаг, на котором ai-toolkit сохраняет финальный чекпоинт при 2000 шагах и save_every=250.
# Если шаги или save_every поменяют в шаблоне — править и тут (FINAL = STEPS − SAVE_EVERY).
FINAL_CHECKPOINT_STEP = 1750
TOTAL_STEPS = 2000
# Таймаут ожидания потока-читателя вывода ai-toolkit
READER_JOIN_S = 10
# Минимальное число годных лиц, иначе ccs_error=too_few_faces
MIN_KEPT_FACES = 15
# Регэксп имени обучающего фото — повторно сверяем, чтобы не съесть чужой файл
_RE_PHOTO = re.compile(r"^[0-9a-f]{16}\.(jpg|png|webp)\Z")
# Регэкспы сегментов пути воркеда. owner — 32 hex lowercase (после LoraNamespace.OwnerKey);
# slug и runId — только [a-z0-9-] (защита от .. и абсолютных)
_RE_OWNER = re.compile(r"^[0-9a-f]{32}\Z")
_RE_SLUG = re.compile(r"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\Z")
_RE_RUN = re.compile(r"^[0-9a-f]{12}\Z")


def _safe_str(value, regex, what):
    if not isinstance(value, str) or not regex.match(value):
        fail(f"недопустимый {what}: {value!r}")
    return value


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
    """Детектирует лицо. Возвращает (rgb_or_None, status, head_or_size).
    status == "ok" при успехе, иначе причина отказа (no_face, many_faces, small_face, unreadable)."""
    import cv2
    img = cv2.imread(src_path)
    if img is None:
        return None, "unreadable", None
    faces = app.get(img)
    if not faces:
        return None, "no_face", None
    if len(faces) > 1:
        return None, "many_faces", None
    face = faces[0]
    bbox = face.bbox.astype(int)  # x1, y1, x2, y2
    x1, y1, x2, y2 = bbox
    fw, fh = x2 - x1, y2 - y1
    if min(fw, fh) < 64:
        return None, "small_face", None
    cx, cy = (x1 + x2) // 2, (y1 + y2) // 2
    # ADR-026 §5.1: «голова» 2.6× и «по пояс» 5.5× лица, квадрат. Берём больший — внутри оба
    crop_short = max(fw, fh)
    head_half = int(round(crop_short * 2.6 / 2))
    waist_half = int(round(crop_short * 5.5 / 2))
    half = waist_half
    H, W = img.shape[:2]
    x0 = max(0, cx - half)
    y0 = max(0, cy - half)
    x1c = min(W, cx + half)
    y1c = min(H, cy + half)
    if x1c - x0 < 32 or y1c - y0 < 32:
        return None, "small_face", None
    cropped = img[y0:y1c, x0:x1c]
    rgb = cv2.cvtColor(cropped, cv2.COLOR_BGR2RGB)
    return rgb, "ok", (head_half, (fw, fh))


def _prepare_dataset(in_dir, out_dataset_dir, trigger):
    """Прогоняет фото через InsightFace, делает кропы и текстовые подписи.
    Возвращает (kept:int, rejected:[{name,reason}])."""
    from insightface.app import FaceAnalysis
    app = FaceAnalysis(name="buffalo_l", providers=["CPUExecutionProvider"])
    app.prepare(ctx_id=-1, det_size=(640, 640))

    rejected = []
    kept = 0
    # ai-toolkit ждёт файлы .png/.jpg с .txt-подписью рядом
    os.makedirs(out_dataset_dir, exist_ok=True)
    for entry in sorted(os.listdir(in_dir)):
        if not _RE_PHOTO.match(entry):
            continue
        src = os.path.join(in_dir, entry)
        rgb, status, head = _crop_face(app, src)
        if status != "ok":
            rejected.append({"name": entry, "reason": status})
            continue
        base = os.path.splitext(entry)[0]
        img_path = os.path.join(out_dataset_dir, base + ".png")
        txt_path = os.path.join(out_dataset_dir, base + ".txt")
        try:
            import cv2
            cv2.imwrite(img_path, cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR))
        except Exception:
            rejected.append({"name": entry, "reason": "unreadable"})
            continue
        with open(txt_path, "w", encoding="utf-8") as f:
            f.write(f"photo of {trigger}, head shot")
        kept += 1
    return kept, rejected


def _render_yaml(template_path, out_yaml, slug, run_id, trigger, out_dir, steps, dataset_dir):
    """Рендерит шаблон templates/standard.yaml в файл config ai-toolkit."""
    with open(template_path, encoding="utf-8") as f:
        text = f.read()
    text = text.replace("<slug>", slug).replace("<run_id>", run_id)
    text = text.replace("<trigger>", trigger).replace("<out_dir>", out_dir)
    text = text.replace("<steps>", str(steps)).replace("<dataset_dir>", dataset_dir)
    with open(out_yaml, "w", encoding="utf-8") as f:
        f.write(text)


def _pump_and_progress(proc, stats):
    """Читает stdout+stderr ai-toolkit, эмитит CCS_PROGRESS (JSON) и аккумулирует хвост в stats."""
    last_step = -1
    last_emit = 0.0
    progress_re = re.compile(r"(?:step|iteration)\D+(\d+)\s*/\s*(\d+)", re.IGNORECASE)
    lines = []
    try:
        for line in iter(proc.stdout.readline, ""):
            line = line.rstrip("\n")
            lines.append(line)
            m = progress_re.search(line)
            if m:
                step = int(m.group(1))
                # Не реже раза в 10 шагов и не чаще 2 раз в секунду
                if step != last_step and (step % 10 == 0 or step >= TOTAL_STEPS):
                    if time.time() - last_emit >= 0.5:
                        print(f"CCS_PROGRESS {json.dumps({'step': step, 'total': TOTAL_STEPS}, ensure_ascii=False)}",
                              flush=True)
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
    owner = _safe_str(params.get("owner", ""), _RE_OWNER, "owner")
    slug = _safe_str(params.get("slug", ""), _RE_SLUG, "slug")
    run_id = _safe_str(params.get("run_id", ""), _RE_RUN, "run_id")
    trigger = _safe_str(params.get("trigger", ""),
                        re.compile(r"^[a-z][a-z0-9_]{2,23}\Z"), "trigger")
    if params.get("preset") != "standard":
        fail(f"неизвестный пресет: {params.get('preset')!r}")

    # out_dir приходит из job.json: узел уже построил путь внутри loras_root и проверил,
    # что loras_root совпадает с каталогом ComfyUI
    out_dir = os.path.realpath(job.out_dir)
    os.makedirs(out_dir, exist_ok=True)

    # input_dir тоже прокинут узлом — воркеру не нужны ComfyUI-модули (folder_paths)
    input_root = os.path.realpath(getattr(job, "input_dir", None) or "")
    if not input_root:
        fail("input_dir не передан узлом в job.json")
    in_dir = os.path.realpath(os.path.join(input_root, "ccs", owner, run_id))
    if not in_dir.startswith(input_root + os.sep) or not os.path.isdir(in_dir):
        fail(f"нет каталога фото на GPU: ccs/{owner}/{run_id}")

    # Валидация имён входов: узел уже проверил _safe_input, тут достаточно регэкспов имён
    photo_names = sorted(set(os.path.basename(raw) for raw in job.inputs))
    if not photo_names:
        fail("нет фото входа")
    for n in photo_names:
        if not _RE_PHOTO.match(n):
            fail(f"имя фото не по регэкспу: {n}")
    if len(photo_names) < MIN_KEPT_FACES:
        # Узел уже ушёл в граф; воркер ставит CCS_RESULT с маркером too_few_faces — узел
        # превратит это в исключение ccs_error=too_few_faces
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
    _render_yaml(template_path, config_yaml, slug, run_id, trigger, out_dir, TOTAL_STEPS, dataset_dir)

    kept_count, rejected = 0, []
    success = False
    try:
        kept_count, rejected = _prepare_dataset(in_dir, dataset_dir, trigger)
        if kept_count < MIN_KEPT_FACES:
            job.stats["photos_kept"] = kept_count
            job.stats["rejected"] = rejected
            job.stats["checkpoint"] = 0
            print(f"CCS_RESULT {json.dumps({'error': 'too_few_faces', 'stats': job.stats}, ensure_ascii=False)}", flush=True)
            sys.exit(2)
        job.lap("dataset")

        # Запуск ai-toolkit. ai-toolkit — набор скриптов (run.py), а не pip-пакет; entrypoint —
        # python run.py config. cwd — корень репозитория (как у worker_acestep)
        ai_dir = os.environ.get("AI_TOOLKIT_DIR")
        if not ai_dir:
            fail("AI_TOOLKIT_DIR не задан")
        py = sys.executable
        cmd = [py, "run.py", config_yaml]
        try:
            proc = subprocess.Popen(cmd, cwd=ai_dir, env=os.environ.copy(),
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    text=True, bufsize=1, start_new_session=True)
        except FileNotFoundError as e:
            fail(f"не удалось запустить ai-toolkit: {e}")

        stats_progress = {}
        reader = threading.Thread(target=_pump_and_progress,
                                  args=(proc, stats_progress),
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
            any_ckpt = sorted(glob.glob(os.path.join(out_dir, f"{slug}_*.safetensors")))
            if not any_ckpt:
                fail(f"ai-toolkit не сохранил чекпоинт в {out_dir}")
            ckpts = [any_ckpt[-1]]
        final = os.path.join(out_dir, f"{slug}.safetensors")
        shutil.move(ckpts[-1], final)
        # Остальные артефакты ai-toolkit (samples/, optimizer.bin, scheduler.bin) удаляем
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
        success = True
    finally:
        shutil.rmtree(dataset_dir, ignore_errors=True)
        # in_dir — фото больше не нужны
        shutil.rmtree(in_dir, ignore_errors=True)
        # При неуспехе — out_dir тоже убираем (узел тоже это делает в finally, но на случай
        # падения тут родителя — добавочная страховка)
        if not success:
            shutil.rmtree(out_dir, ignore_errors=True)


def _delete(job):
    """op == lora_delete. Без GPU; просто снимаем каталоги."""
    params = job.params
    owner = _safe_str(params.get("owner", ""), _RE_OWNER, "owner")
    slug = _safe_str(params.get("slug", ""), _RE_SLUG, "slug")
    run_id = _safe_str(params.get("run_id", ""), _RE_RUN, "run_id")
    out_dir = os.path.realpath(job.out_dir)

    input_root = os.path.realpath(getattr(job, "input_dir", None) or "")
    if not input_root:
        fail("input_dir не передан узлом в job.json")
    in_dir = os.path.realpath(os.path.join(input_root, "ccs", owner, run_id))
    if not in_dir.startswith(input_root + os.sep):
        fail(f"in_dir за пределами input: {in_dir}")

    shutil.rmtree(out_dir, ignore_errors=True)
    shutil.rmtree(in_dir, ignore_errors=True)
    print(f"CCS_RESULT {json.dumps({'files': [], 'stats': {'deleted': f'ccs/{owner}/{slug}-{run_id}/'}}, ensure_ascii=False)}", flush=True)
    sys.exit(0)


def main():
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