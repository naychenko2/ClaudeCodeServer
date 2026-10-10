"""Обучение LoRA персонажа (ADR-026) через ai-toolkit: op lora_train и lora_delete.

Протокол общий с аудио-воркерами (deploy/comfyui/audio/README.md): argv[1] — job.json с ключами
op, params, inputs (абсолютные пути), out_dir, input_dir; последняя строка stdout — CCS_RESULT.
out_dir и input_dir строит и проверяет узел CcsAudioWorker; воркер дополнительно сверяет сегменты.

lora_train:
  * фото — только input/ccs/<owner>/<run_id>/<sha16>.<ext>;
  * InsightFace buffalo_l (CPU): ровно одно лицо → кропы «голова» 2,6× и «по пояс» 5,5× лица,
    квадрат; отказы no_face / many_faces / small_face / unreadable уходят в stats.rejected;
  * подписи «photo of <trigger>, head shot» / «… waist-up shot»;
  * ai-toolkit по шаблону templates/<preset>.yaml, прогресс — CCS_PROGRESS {"step","total"};
  * итог — чекпоинт шага 1750 → out_dir/<slug>.safetensors, остальное из out_dir удаляется;
  * годных фото < 15 → CCS_RESULT {"error":"too_few_faces"}, код 2.
lora_delete: удалить out_dir и in_dir прогона, без GPU и без подпроцесса.

Пути машины (веса, extras Qwen-Image-2.1, общий код воркеров) — из env семейства в workers.json.
CCS_LORA_MAX_STEPS — только для проверки на стенде: укорачивает прогон, итог — последний шаг.
"""
import glob
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
# общий ccs_common.py аудио-воркеров: на машине — каталог из env, в репозитории — соседний audio/workers
sys.path.insert(0, os.environ.get("CCS_WORKERS_DIR") or os.path.join(HERE, "..", "audio", "workers"))
from ccs_common import fail, load_job  # noqa: E402

# Пресеты — белый список ADR-026; шаги и финальный чекпоинт живут здесь, на проводе их нет
PRESETS = {"standard": {"steps": 2000, "checkpoint": 1750}}
MIN_KEPT_FACES = 15
MIN_FACE_PX = 64
HEAD_SCALE, WAIST_SCALE = 2.6, 5.5

_RE_PHOTO = re.compile(r"[0-9a-f]{16}\.(jpg|png|webp)\Z")
_RE_OWNER = re.compile(r"[0-9a-f]{32}\Z")
_RE_SLUG = re.compile(r"[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\Z")
_RE_RUN = re.compile(r"[0-9a-f]{12}\Z")
_RE_TRIGGER = re.compile(r"[a-z][a-z0-9_]{2,23}\Z")


def _check(value, regex, what):
    if not isinstance(value, str) or not regex.match(value):
        fail(f"недопустимый {what}: {value!r}")
    return value


def _inside(path, root):
    path, root = os.path.realpath(path), os.path.realpath(root)
    return path.startswith(root + os.sep)


def _run_dirs(job):
    """Сегменты прогона по регэкспам §1 и каталоги, заново сверенные с корнями."""
    p = job.params
    owner = _check(p.get("owner"), _RE_OWNER, "owner")
    slug = _check(p.get("slug"), _RE_SLUG, "slug")
    run_id = _check(p.get("run_id"), _RE_RUN, "run_id")
    if not job.input_dir:
        fail("узел не передал input_dir")
    in_dir = os.path.join(job.input_dir, "ccs", owner, run_id)
    if not _inside(in_dir, job.input_dir):
        fail("каталог фото вне input")
    if not os.path.normpath(job.out_dir).endswith(os.sep + os.path.join("ccs", owner, f"{slug}-{run_id}")):
        fail("out_dir не каталог этого прогона")
    for d in (in_dir, job.out_dir):
        if os.path.islink(d):
            fail(f"символическая ссылка вместо каталога: {d}")
    return owner, slug, run_id, in_dir


def _square(cx, cy, side, w, h, top_share):
    """Квадрат стороной side вокруг (cx, cy), лицо на top_share высоты сверху; сдвиг внутрь кадра."""
    side = int(min(side, w, h))
    x0 = int(cx - side / 2)
    y0 = int(cy - side * top_share)
    x0 = max(0, min(x0, w - side))
    y0 = max(0, min(y0, h - side))
    return x0, y0, side


def _crops(app, path):
    """[(кроп BGR, подпись-хвост)] или причина отказа строкой."""
    import cv2
    img = cv2.imread(path)
    if img is None:
        return "unreadable"
    faces = app.get(img)
    if not faces:
        return "no_face"
    if len(faces) > 1:
        return "many_faces"
    x1, y1, x2, y2 = (int(v) for v in faces[0].bbox)
    face = max(x2 - x1, y2 - y1)
    if face < MIN_FACE_PX:
        return "small_face"
    h, w = img.shape[:2]
    cx, cy = (x1 + x2) / 2, (y1 + y2) / 2
    out = []
    x0, y0, s = _square(cx, cy, face * HEAD_SCALE, w, h, 0.5)
    out.append((img[y0:y0 + s, x0:x0 + s], "head shot"))
    # «по пояс»: лицо в верхней четверти; кадр, который не шире «головы» в полтора раза, — не отдельный ракурс
    x0, y0, s2 = _square(cx, cy, face * WAIST_SCALE, w, h, 0.25)
    if s2 >= s * 1.5:
        out.append((img[y0:y0 + s2, x0:x0 + s2], "waist-up shot"))
    return out


def _prepare_dataset(names, in_dir, dataset_dir, trigger):
    import cv2
    from insightface.app import FaceAnalysis
    app = FaceAnalysis(name="buffalo_l", providers=["CPUExecutionProvider"])
    app.prepare(ctx_id=-1, det_size=(640, 640))
    kept, rejected, images = 0, [], 0
    for name in names:
        result = _crops(app, os.path.join(in_dir, name))
        if isinstance(result, str):
            rejected.append({"name": name, "reason": result})
            continue
        base = name.split(".")[0]
        for k, (crop, shot) in enumerate(result):
            stem = os.path.join(dataset_dir, f"{base}-{k}")
            cv2.imwrite(stem + ".png", crop)
            with open(stem + ".txt", "w", encoding="utf-8") as f:
                f.write(f"photo of {trigger}, {shot}")
            images += 1
        kept += 1
    return kept, rejected, images


def _render(template, target, values):
    with open(template, encoding="utf-8") as f:
        text = f.read()
    for key, value in values.items():
        text = text.replace(f"<{key}>", str(value))
    if re.search(r"<[a-z_]+>", text):
        fail(f"в шаблоне остались плейсхолдеры: {re.findall(r'<[a-z_]+>', text)}")
    with open(target, "w", encoding="utf-8") as f:
        f.write(text)


def _result_error(code, job):
    print("CCS_RESULT " + json.dumps({"error": code, "files": [], "stats": job.stats}, ensure_ascii=False), flush=True)
    sys.exit(2)


def _train(job):
    owner, slug, run_id, in_dir = _run_dirs(job)
    trigger = _check(job.params.get("trigger"), _RE_TRIGGER, "trigger")
    preset = PRESETS.get(job.params.get("preset"))
    if preset is None:
        fail(f"неизвестный пресет: {job.params.get('preset')!r}")
    steps, checkpoint = preset["steps"], preset["checkpoint"]
    cap = int(os.environ.get("CCS_LORA_MAX_STEPS") or 0)
    if cap > 0:
        steps, checkpoint = min(steps, cap), None

    names = sorted({os.path.basename(p) for p in job.inputs})
    for p in job.inputs:
        if os.path.dirname(os.path.realpath(p)) != os.path.realpath(in_dir) or not _RE_PHOTO.match(os.path.basename(p)):
            fail(f"вход не фото этого прогона: {p}")

    base_weights = os.environ.get("CCS_LORA_BASE_WEIGHTS")
    extras = os.environ.get("CCS_LORA_EXTRAS")
    ai_dir = os.environ.get("AI_TOOLKIT_DIR")
    if not (base_weights and extras and ai_dir):
        fail("в workers.json не заданы CCS_LORA_BASE_WEIGHTS, CCS_LORA_EXTRAS или AI_TOOLKIT_DIR")

    os.makedirs(job.out_dir, exist_ok=True)
    work = tempfile.mkdtemp(prefix="ccs-lora-")
    success = False
    try:
        dataset = os.path.join(work, "dataset")
        os.makedirs(dataset)
        kept, rejected, images = _prepare_dataset(names, in_dir, dataset, trigger)
        job.stats.update(photos_total=len(names), photos_kept=kept, rejected=rejected, images=images)
        job.lap("dataset")
        print(f"фото: годных {kept} из {len(names)}, кропов {images}", flush=True)
        if kept < MIN_KEPT_FACES:
            _result_error("too_few_faces", job)

        name = f"ccs-{slug}-{run_id}"
        config = os.path.join(work, "config.yaml")
        _render(os.path.join(HERE, "templates", f"{job.params['preset']}.yaml"), config, {
            "name": name, "training_folder": os.path.join(work, "out"), "dataset_dir": dataset,
            "steps": steps, "base_weights": base_weights, "extras_dir": extras,
        })
        # без новой сессии: узел гасит группу процессов воркера, и ai-toolkit должен уйти вместе с ней
        proc = subprocess.Popen([sys.executable, "run.py", config], cwd=ai_dir, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, bufsize=1)
        tail = []
        reader = threading.Thread(target=_pump, args=(proc, name, steps, tail), daemon=True)
        reader.start()
        rc = proc.wait()
        reader.join(timeout=10)
        job.lap("train")
        if rc != 0:
            text = "\n".join(tail)
            if "out of memory" in text.lower():
                print(text[-1500:], flush=True)
                _result_error("oom", job)
            fail(f"ai-toolkit завершился с кодом {rc}:\n{text[-1500:]}")

        run_dir = os.path.join(work, "out", name)
        source = (os.path.join(run_dir, f"{name}_{checkpoint:09d}.safetensors") if checkpoint
                  else os.path.join(run_dir, f"{name}.safetensors"))
        if not os.path.isfile(source):
            fail(f"ai-toolkit не сохранил {os.path.basename(source)}: {sorted(os.listdir(run_dir))}")
        for p in glob.glob(os.path.join(job.out_dir, "*")):
            shutil.rmtree(p) if os.path.isdir(p) else os.remove(p)
        shutil.move(source, os.path.join(job.out_dir, f"{slug}.safetensors"))
        job.files.append(f"{slug}.safetensors")
        job.stats.update(steps=steps, checkpoint=checkpoint or steps)
        success = True
    finally:
        shutil.rmtree(work, ignore_errors=True)
        shutil.rmtree(in_dir, ignore_errors=True)
        if not success:
            shutil.rmtree(job.out_dir, ignore_errors=True)


def _pump(proc, name, total, tail):
    """Вывод ai-toolkit: шаги из строки прогресса tqdm прогона («<name>:  6%|…| 120/2000 […]»)."""
    step_re = re.compile(re.escape(name) + r":\s+\d+%\|[^|]*\|\s*(\d+)/(\d+)")
    last = -1
    for line in proc.stdout:  # text=True: «\r» tqdm режет строки так же, как «\n»
        line = line.rstrip()
        m = step_re.search(line)
        if m:
            step = int(m.group(1))
            if step != last and (step - last >= 10 or step == total or last < 0):
                print("CCS_PROGRESS " + json.dumps({"step": step, "total": total}), flush=True)
                last = step
            continue
        if line:
            tail.append(line)
            del tail[:-60]
            print(line[:300], flush=True)


def _delete(job):
    owner, slug, run_id, in_dir = _run_dirs(job)
    shutil.rmtree(job.out_dir, ignore_errors=True)
    shutil.rmtree(in_dir, ignore_errors=True)
    job.stats["deleted"] = f"ccs/{owner}/{slug}-{run_id}/"


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
