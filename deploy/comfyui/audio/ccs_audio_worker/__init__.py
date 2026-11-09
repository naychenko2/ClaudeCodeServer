"""Аудио-воркеры local-media: узел очереди ComfyUI, который запускает модель в своём venv.

GPU1 общая: ComfyUI рисует картинки и видео, конвейер транскрибации гоняет WhisperX,
а аудиомоделям (TTS, стемы, смена голоса, реставрация) нужны свои зависимости, которые
в venv ComfyUI не встают. Поэтому каждая модель живёт в отдельном venv, а в очередь
ComfyUI ставится этот узел — так у GPU1 остаётся одна очередь на всех.

Узел, как и шлагбаум транскрибации, сначала выгружает модели ComfyUI из видеопамяти,
уже заняв очередь, затем запускает воркер отдельным процессом и ждёт его. Процесс
завершился — видеопамять свободна, следующая генерация сама загрузит свою модель.

Безопасность: операция — только из белого списка OPS, воркер — только из машинного
конфига (путь к python venv и к скрипту), параметры — JSON, который валидирует сам
воркер. Входы — имена файлов в input ComfyUI без «..» и абсолютных путей. Результат
пишется только в output/ccs-local-media.

Конфиг: $CCS_AUDIO_WORKERS или ~/ai-data/audio-workers/workers.json
  {"families": {"tts": {"python": ".../.venv/bin/python", "script": ".../worker_tts.py", "env": {...}}}}
Протокол воркера: argv[1] — путь к job.json; последняя строка stdout, начинающаяся с
CCS_RESULT, — JSON {"files": [имена в out_dir], "stats": {...}}. Необязательные строки
CCS_PROGRESS {"step": N, "total": M} узел превращает в ProgressBar ComfyUI (сообщение сокета
progress); воркер без них работает как раньше, только без шагов.
"""
import gc
import json
import logging
import os
import re
import shutil
import signal
import subprocess
import tempfile
import threading
import time
import uuid

import comfy.model_management as mm
import folder_paths

log = logging.getLogger("ccs_audio_worker")

# операция → семейство воркера (venv); список закрыт, новое — только правкой узла
OPS = {
    "separate": "sep",
    "tts": "tts",
    "voice_clone": "tts",
    "tts_chatterbox": "chatterbox",
    "tts_moss": "moss",
    "voice_convert": "seedvc",
    "rvc_convert": "rvc",
    "rvc_train": "rvc",
    "audio_to_midi": "midi",
    "denoise": "dfn",
    "upsample": "audiosr",
    "master": "sep",
    "music_edit": "acestep",
    "transcribe": "asr",
    "transcribe_diarize": "whisperx",
    # Обучение LoRA (ADR-026 §5): отдельное семейство с полной выгрузкой и сбросом RAM-кэша,
    # путь к весам из workers.json (loras_root обязан совпасть с folder_paths loras)
    "lora_train": "lora",
    "lora_delete": "lora",
}

OUTPUT_SUBFOLDER = "ccs-local-media"
RESULT_MARK = "CCS_RESULT "
TAIL_LINES = 40


# Хвост лога воркера для исключения: последние TAIL_LINES строк без префикса RESULT/PROGRESS,
# с абсолютными путями заменены на «<path>» — чтобы исключение не утекало внутренние пути наружу.
# Утечка не критична (всё своё), но в логах бэкенда и на экране пользователя абсолютные
# пути машины не нужны и могут мешать диагностике.
def _tail_no_paths(lines, limit=2000):
    cleaned = [l for l in lines if not l.startswith(RESULT_MARK)]
    cleaned = cleaned[-limit:]
    return re.sub(r"/[\w./-]{6,}", "<path>", "\n".join(cleaned))


_PROGRESS_RE = re.compile(r"^CCS_PROGRESS\s+(.+)$")


class _Progress:
    """Последний шаг воркера из строк CCS_PROGRESS (НЕ путать с CCS_RESULT).

    feed() зовёт поток чтения stdout, report() — только поток задачи: ProgressBar шлёт сообщение
    сокета от имени исполняемого узла (ADR-026 §5.1). Воркер без прогресса строк не печатает —
    ProgressBar не создаётся, сообщений сокета нет, всё как до появления прогресса.
    """

    def __init__(self):
        self.last = None
        self.reported = None
        self.bar = None

    def feed(self, line):
        """True — строка прогресса (в лог и хвост исключения не идёт), даже если она битая."""
        m = _PROGRESS_RE.match(line)
        if not m:
            return False
        try:
            payload = json.loads(m.group(1))
            step, total = int(payload["step"]), int(payload["total"])
            if total > 0:
                self.last = (max(0, min(step, total)), total)
        except Exception:
            pass
        return True

    def report(self):
        last = self.last
        if last is None or last == self.reported:
            return
        try:
            from comfy.utils import ProgressBar  # лениво, чтобы не требовать его вне ComfyUI
            self.bar = self.bar or ProgressBar(last[1])  # свой на каждый запуск: экземпляр узла переживает прогоны
            self.bar.update_absolute(last[0], last[1])
            self.reported = last
        except Exception:
            pass


def _config():
    path = os.environ.get("CCS_AUDIO_WORKERS") or os.path.expanduser("~/ai-data/audio-workers/workers.json")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _safe_input(name):
    name = (name or "").strip()
    if not name:
        return None
    if os.path.isabs(name) or ".." in name.replace("\\", "/").split("/"):
        raise ValueError(f"недопустимое имя входа: {name}")
    root = os.path.realpath(folder_paths.get_input_directory())
    full = os.path.realpath(os.path.join(root, name))
    if not full.startswith(root + os.sep) or not os.path.isfile(full):
        raise ValueError(f"вход не найден: {name}")
    return full


# Запас сверх бюджета семейства: CUDA-контекст процесса воркера и фрагментация
HEADROOM_MB = 1536


def _free_vram(need_mb=None):
    """С бюджетом (vram_mb семейства в workers.json) освобождает только нужное: модели ComfyUI
    частично уходят в ОЗУ и потом возвращаются без пересборки. Без бюджета — выгрузить всё."""
    if need_mb:
        mm.free_memory((need_mb + HEADROOM_MB) * 2**20, mm.get_torch_device())
    else:
        mm.unload_all_models()
    gc.collect()
    mm.soft_empty_cache(force=True)


# Регэкспы и константы по ADR-026 §1: они жёстко связаны с LoraNamespace и CharacterStore,
# которые валидируют на бэкенде. Узел повторяет их, чтобы лишний раз не дать воркеру мусор
# (проверка на стороне сервера не спасает: воркер удаляет файлы)
_LO_OWNER = re.compile(r"[0-9a-f]{32}\Z")
_LO_SLUG = re.compile(r"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\Z")
_LO_RUN = re.compile(r"[0-9a-f]{12}\Z")
_LO_TRIG = re.compile(r"^[a-z][a-z0-9_]{2,23}\Z")
_LO_PRESETS = {"standard"}


class CcsAudioWorker:
    CATEGORY = "audio/ccs"
    FUNCTION = "run"
    RETURN_TYPES = ()
    OUTPUT_NODE = True

    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "op": (sorted(OPS.keys()),),
            "params_json": ("STRING", {"default": "{}", "multiline": True}),
            # имена файлов в input ComfyUI, по одному на строку
            "inputs": ("STRING", {"default": "", "multiline": True}),
            "filename_prefix": ("STRING", {"default": "job"}),
            "timeout_minutes": ("INT", {"default": 30, "min": 1, "max": 240}),
        }}

    @classmethod
    def IS_CHANGED(cls, **kwargs):
        # каждый запуск — новая работа: кэш ComfyUI тут неуместен
        return float("nan")

    def run(self, op, params_json, inputs, filename_prefix, timeout_minutes):
        # LoRA-операции идут в отдельную ветку: свои проверки путей, полная выгрузка VRAM,
        # итог в ui.ccs_lora (а не ui.audio), чистка in_dir в обеих ветках и out_dir при сбое
        if op in ("lora_train", "lora_delete"):
            return self._run_lora(op, params_json, inputs, filename_prefix, timeout_minutes)
        return self._run(op, params_json, inputs, filename_prefix, timeout_minutes, soft=True)

    def _run(self, op, params_json, inputs, filename_prefix, timeout_minutes, soft):
        if op not in OPS:
            raise ValueError(f"неизвестная операция {op}")
        params = json.loads(params_json or "{}")
        if not isinstance(params, dict):
            raise ValueError("params_json — объект JSON")
        prefix = "".join(c for c in filename_prefix if c.isalnum() or c in "-_")[:80] or "job"

        family = _config()["families"][OPS[op]]
        in_paths = [p for p in (_safe_input(n) for n in (inputs or "").splitlines()) if p]
        out_dir = os.path.join(folder_paths.get_output_directory(), OUTPUT_SUBFOLDER)
        os.makedirs(out_dir, exist_ok=True)

        need_mb = family.get("vram_mb") if soft else None
        _free_vram(need_mb)

        job = {"op": op, "params": params, "inputs": in_paths, "out_dir": out_dir, "prefix": prefix}
        fd, job_path = tempfile.mkstemp(prefix="ccs-audio-", suffix=".json")
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(job, f, ensure_ascii=False)

        started = time.time()
        log.info("аудио-воркер %s (%s): старт", op, OPS[op])
        # Веса ставятся при установке: скачивание внутри задачи держало бы общую очередь GPU
        env = dict(os.environ, PYTHONUNBUFFERED="1", HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
        # у воркера свой venv и свой torch: библиотеки ComfyUI (cu13) и его аллокатор
        # (cuda_malloc.py ставит PYTORCH_CUDA_ALLOC_CONF=backend:cudaMallocAsync) ему чужие —
        # AudioSR с ними забивал 24 ГБ и шёл втрое медленнее
        for key in ("PYTHONPATH", "LD_LIBRARY_PATH", "LD_PRELOAD", "PYTORCH_CUDA_ALLOC_CONF"):
            env.pop(key, None)
        # переменные семейства из машинного конфига (каталоги весов и т. п.)
        env.update({str(k): str(v) for k, v in (family.get("env") or {}).items()})
        proc = subprocess.Popen([family["python"], family["script"], job_path], env=env,
                                cwd=family.get("cwd") or os.path.dirname(family["script"]),
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                start_new_session=True)
        lines = []
        progress = _Progress()

        def pump():
            for line in proc.stdout:
                line = line.rstrip("\n")
                if progress.feed(line):
                    continue
                lines.append(line)
                if not line.startswith(RESULT_MARK):
                    log.info("[%s] %s", op, line[:300])
                if len(lines) > 800:
                    del lines[:-400]

        reader = threading.Thread(target=pump, daemon=True)
        reader.start()
        try:
            deadline = started + timeout_minutes * 60
            while True:
                if proc.poll() is not None:
                    reader.join(timeout=10)
                    progress.report()
                    break
                if time.time() > deadline:
                    raise TimeoutError(f"воркер {op} не уложился в {timeout_minutes} мин")
                progress.report()
                # отмена кнопкой или /interrupt
                mm.throw_exception_if_processing_interrupted()
                time.sleep(0.5)
        except BaseException:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except OSError:
                pass
            raise
        finally:
            try:
                os.remove(job_path)
            except OSError:
                pass

        result = None
        for line in reversed(lines):
            if line.startswith(RESULT_MARK):
                result = json.loads(line[len(RESULT_MARK):])
                break
        if proc.returncode != 0 or result is None:
            # воркер мог упасть до того, как успел напечатать CCS_RESULT — тогда result=None;
            # или мог напечатать {"error": "..."} и выйти с кодом 2. Маркер ccs_error=<код>
            # нужен бэкенду, чтобы отличить too_few_faces/oom от прочих сбоев воркера (ADR-026 §5.1)
            err = (result or {}).get("error")
            tail = _tail_no_paths(lines)
            if need_mb and ("out of memory" in tail.lower() or err == "oom"):
                log.warning("аудио-воркер %s: OOM при бюджете %d МБ — повтор с полной выгрузкой", op, need_mb)
                return self._run(op, params_json, inputs, filename_prefix, timeout_minutes, soft=False)
            if err:
                raise RuntimeError(f"воркер {op} ccs_error={err}: {tail}")
            raise RuntimeError(f"воркер {op} завершился с кодом {proc.returncode}: {tail}")
        if result.get("error"):
            tail = _tail_no_paths(lines)
            raise RuntimeError(f"воркер {op} ccs_error={result['error']}: {tail}")

        files = []
        for name in result.get("files", []):
            base = os.path.basename(name)
            if base != name or not os.path.isfile(os.path.join(out_dir, base)):
                raise RuntimeError(f"воркер вернул недопустимый файл: {name}")
            files.append({"filename": base, "subfolder": OUTPUT_SUBFOLDER, "type": "output"})
        log.info("аудио-воркер %s: готово за %.0f с, файлов %d", op, time.time() - started, len(files))
        return {"ui": {"audio": files, "ccs_stats": [result.get("stats", {})]}}

    def _run_lora(self, op, params_json, inputs, filename_prefix, timeout_minutes):
        """LoRA-воркер (ADR-026 §5): op lora_train/lora_delete.

        Контракт:
          * Полная выгрузка VRAM (без vram_mb) — на замере без /free не влезает (17.6 / 24.6 ГБ).
          * out_dir = loras_root/ccs/<owner>/<slug>-<run_id>/; файл лежит там, не в OUTPUT_SUBFOLDER.
          * Входы только из ccs/<owner>/<run_id>/, имена — регэксп §1 (повторно на узле и воркере).
          * Прогресс — отдельная строка CCS_PROGRESS {"step":N,"total":M} (не CCS_RESULT).
          * Успех lora_train: <slug>.safetensors есть в out_dir → ccs_lora с именем для LoraNamespace.
          * Успех lora_delete: каталог прогона снят (или его не было) → ccs_lora со статусом deleted.
          * finally: in_dir чистятся. При сбое lora_train дополнительно снимается out_dir.
        """
        if op not in OPS or OPS[op] != "lora":
            raise ValueError(f"операция {op} не для lora-воркера")
        params = json.loads(params_json or "{}")
        if not isinstance(params, dict):
            raise ValueError("params_json — объект JSON")

        # 1) Валидации по §1 (зеркало LoraNamespace + LoraTrigger + LoraTrainPresets на бэкенде)
        owner = params.get("owner", "")
        if not isinstance(owner, str) or not _LO_OWNER.fullmatch(owner):
            raise ValueError(f"owner — 32 hex: {owner!r}")
        slug = params.get("slug", "")
        if not isinstance(slug, str) or not _LO_SLUG.match(slug):
            raise ValueError(f"slug не по регэкспу §1: {slug!r}")
        run_id = params.get("run_id", "")
        if not isinstance(run_id, str) or not _LO_RUN.fullmatch(run_id):
            raise ValueError(f"run_id — 12 hex: {run_id!r}")
        if op == "lora_train":
            trigger = params.get("trigger", "")
            if not isinstance(trigger, str) or not _LO_TRIG.match(trigger):
                raise ValueError(f"trigger не по регэкспу §1: {trigger!r}")
            preset = params.get("preset", "")
            if preset not in _LO_PRESETS:
                raise ValueError(f"preset — одно из: {', '.join(sorted(_LO_PRESETS))}")

        # 2) Сверка loras_root с каталогом ComfyUI (узел не врёт про путь к весам)
        family = _config()["families"]["lora"]
        loras_root_raw = family.get("loras_root")
        if not isinstance(loras_root_raw, str):
            raise ValueError("loras_root в workers.json не задан для семейства lora")
        loras_root = os.path.realpath(loras_root_raw)
        comfy_loras = [os.path.realpath(p) for p in folder_paths.get_folder_paths("loras")]
        if loras_root not in comfy_loras:
            raise ValueError(
                f"loras_root семейства lora {loras_root} не совпадает с каталогом ComfyUI: {comfy_loras}"
            )

        # 3) out_dir для воркера (внутри loras_root), in_dir для in_paths (внутри input)
        out_dir = os.path.join(loras_root, "ccs", owner, f"{slug}-{run_id}")
        # in_dir существует только когда оп lora_train: фильтр входов всё равно упадёт ниже,
        # если входы переданы для lora_delete (а они по контракту и не должны)
        in_paths = [p for p in (_safe_input(n) for n in (inputs or "").splitlines()) if p]
        if op == "lora_train":
            in_dir_prefix = f"ccs/{owner}/{run_id}/"
            for p in in_paths:
                # _safe_input уже проверил, что путь внутри input; дополнительно проверим префикс
                rel = os.path.relpath(p, folder_paths.get_input_directory()).replace("\\", "/")
                if not rel.startswith(in_dir_prefix):
                    raise ValueError(f"вход не в {in_dir_prefix}: {rel}")

        prefix = "".join(c for c in filename_prefix if c.isalnum() or c in "-_")[:80] or "job"

        # 4) Полная выгрузка VRAM: на замере пик 17.6 / 24.6 ГБ без /free не влезает. lora_delete — без выгрузки
        if op == "lora_train":
            _free_vram(need_mb=None)

        # 5) job.json с out_dir, указывающим на каталог в loras_root (не OUTPUT_SUBFOLDER).
        # Воркеру нужен input_dir для очистки in_dir — прокидываем явно, чтобы он не лазил в
        # folder_paths (это ComfyUI-окружение, в воркере недоступно). loras_root нужен воркеру
        # для повторной проверки: realpath(out_dir) обязан лежать внутри realpath(loras_root),
        # иначе shutil.rmtree может удалить данные за пределами через симлинк в ccs/<owner>.
        # work_dir (кропы фото, кэши ai-toolkit) создаёт и удаляет узел: воркер при /interrupt получает
        # SIGKILL, и его собственный finally не выполняется — кропы лиц оставались бы в /tmp
        input_dir = folder_paths.get_input_directory()
        work_dir = tempfile.mkdtemp(prefix="ccs-lora-")
        job = {"op": op, "params": params, "inputs": in_paths, "out_dir": out_dir,
               "input_dir": input_dir, "loras_root": loras_root, "work_dir": work_dir, "prefix": prefix}
        fd, job_path = tempfile.mkstemp(prefix="ccs-audio-", suffix=".json")
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(job, f, ensure_ascii=False)

        started = time.time()
        log.info("lora-воркер %s (%s): старт", op, OPS[op])
        # extras Qwen-Image-2.1 (энкодер, VAE, конфиги) ставит install.sh: сеть внутри задачи держала бы очередь GPU
        env = dict(os.environ, PYTHONUNBUFFERED="1", HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
        for key in ("PYTHONPATH", "LD_LIBRARY_PATH", "LD_PRELOAD", "PYTORCH_CUDA_ALLOC_CONF"):
            env.pop(key, None)
        env.update({str(k): str(v) for k, v in (family.get("env") or {}).items()})
        proc = subprocess.Popen([family["python"], family["script"], job_path], env=env,
                                cwd=family.get("cwd") or os.path.dirname(family["script"]),
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                start_new_session=True)

        # Сбор прогресса CCS_PROGRESS (НЕ путать с CCS_RESULT) и хвост логов
        progress = _Progress()
        log_tail = []

        def pump():
            for line in proc.stdout:
                line = line.rstrip("\n")
                # Прогресс узел сам НЕ печатает — только хранит последний шаг
                if progress.feed(line):
                    continue
                log_tail.append(line)
                if not line.startswith(RESULT_MARK):
                    log.info("[%s] %s", op, line[:300])
                if len(log_tail) > 800:
                    del log_tail[:-400]

        reader = threading.Thread(target=pump, daemon=True)
        reader.start()

        try:
            deadline = started + timeout_minutes * 60
            while True:
                if proc.poll() is not None:
                    reader.join(timeout=10)
                    progress.report()
                    break
                if time.time() > deadline:
                    raise TimeoutError(f"lora-воркер {op} не уложился в {timeout_minutes} мин")
                # Прогресс в ProgressBar — только из потока задачи, не из pump (ADR §5.1)
                progress.report()
                mm.throw_exception_if_processing_interrupted()
                time.sleep(0.5)
        except BaseException:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except OSError:
                pass
            raise
        finally:
            try:
                os.remove(job_path)
            except OSError:
                pass
            shutil.rmtree(work_dir, ignore_errors=True)
            # Если воркер не успел — out_dir при lora_train чистится, in_dir всегда
            if proc.poll() is None or proc.returncode != 0:
                if op == "lora_train" and os.path.isdir(out_dir):
                    shutil.rmtree(out_dir, ignore_errors=True)
                if op == "lora_train":
                    shutil.rmtree(os.path.join(folder_paths.get_input_directory(), "ccs", owner, run_id),
                                  ignore_errors=True)

        # Парсим CCS_RESULT — это финальная строка; CCS_PROGRESS в ней нет (мы их фильтровали)
        result = None
        for line in reversed(log_tail):
            if line.startswith(RESULT_MARK):
                try:
                    result = json.loads(line[len(RESULT_MARK):])
                except Exception:
                    result = None
                break

        if proc.returncode != 0 or result is None:
            # воркер мог упасть до того, как успел напечатать CCS_RESULT — тогда result=None;
            # или мог напечатать {"error": "..."} и выйти с кодом 2. Маркер ccs_error=<код>
            # нужен бэкенду, чтобы отличить too_few_faces/oom от прочих сбоев воркера (ADR-026 §5.1)
            err = (result or {}).get("error")
            tail = _tail_no_paths(log_tail)
            if err:
                raise RuntimeError(f"lora-воркер {op} ccs_error={err}: {tail}")
            raise RuntimeError(f"lora-воркер {op} завершился с кодом {proc.returncode}: {tail}")
        if result.get("error"):
            err = result["error"]
            tail = _tail_no_paths(log_tail)
            raise RuntimeError(f"lora-воркер {op} ccs_error={err}: {tail}")

        stats = result.get("stats", {})
        # Успех: ccs_lora + ccs_stats. Воркер не возвращает files — узел сам проверяет артефакт
        if op == "lora_train":
            final_path = os.path.join(out_dir, f"{slug}.safetensors")
            if not os.path.isfile(final_path):
                shutil.rmtree(out_dir, ignore_errors=True)
                raise RuntimeError(f"lora-воркер не оставил {final_path}")
            # in_dir — узел снимает; out_dir остаётся
            shutil.rmtree(os.path.join(folder_paths.get_input_directory(), "ccs", owner, run_id),
                          ignore_errors=True)
            log.info("lora-воркер %s: готово за %.0f с", op, time.time() - started)
            return {"ui": {
                "ccs_lora": [{"name": f"ccs/{owner}/{slug}-{run_id}/{slug}.safetensors"}],
                "ccs_stats": [stats],
            }}
        else:  # lora_delete
            # Ничего не пишем: воркер уже снёс out_dir и in_dir (если они были). Нет — это тоже успех
            log.info("lora-воркер %s: готово за %.0f с", op, time.time() - started)
            return {"ui": {
                "ccs_lora": [{"deleted": f"ccs/{owner}/{slug}-{run_id}/"}],
            }}


NODE_CLASS_MAPPINGS = {"CcsAudioWorker": CcsAudioWorker}
NODE_DISPLAY_NAME_MAPPINGS = {"CcsAudioWorker": "CCS Audio Worker (venv)"}
