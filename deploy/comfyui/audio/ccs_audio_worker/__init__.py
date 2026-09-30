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
  {"families": {"tts": {"python": ".../.venv/bin/python", "script": ".../worker_tts.py"}}}
Протокол воркера: argv[1] — путь к job.json; последняя строка stdout, начинающаяся с
CCS_RESULT, — JSON {"files": [имена в out_dir], "stats": {...}}.
"""
import gc
import json
import logging
import os
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
    "voice_convert": "seedvc",
    "rvc_convert": "rvc",
    "rvc_train": "rvc",
    "audio_to_midi": "midi",
    "denoise": "dfn",
    "upsample": "audiosr",
    "master": "sep",
    "music_edit": "acestep",
    "transcribe": "asr",
}

OUTPUT_SUBFOLDER = "ccs-local-media"
RESULT_MARK = "CCS_RESULT "
TAIL_LINES = 40


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


def _free_vram():
    mm.unload_all_models()
    gc.collect()
    mm.soft_empty_cache(force=True)


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

        _free_vram()

        job = {"op": op, "params": params, "inputs": in_paths, "out_dir": out_dir, "prefix": prefix}
        fd, job_path = tempfile.mkstemp(prefix="ccs-audio-", suffix=".json")
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(job, f, ensure_ascii=False)

        started = time.time()
        log.info("аудио-воркер %s (%s): старт", op, OPS[op])
        # Веса ставятся при установке: скачивание внутри задачи держало бы общую очередь GPU
        env = dict(os.environ, PYTHONUNBUFFERED="1", HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
        env.pop("PYTHONPATH", None)
        proc = subprocess.Popen([family["python"], family["script"], job_path], env=env,
                                cwd=family.get("cwd") or os.path.dirname(family["script"]),
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                start_new_session=True)
        lines = []

        def pump():
            for line in proc.stdout:
                line = line.rstrip("\n")
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
                    break
                if time.time() > deadline:
                    raise TimeoutError(f"воркер {op} не уложился в {timeout_minutes} мин")
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
            tail = "\n".join(l for l in lines if not l.startswith(RESULT_MARK))[-2000:]
            raise RuntimeError(f"воркер {op} завершился с кодом {proc.returncode}: {tail}")

        files = []
        for name in result.get("files", []):
            base = os.path.basename(name)
            if base != name or not os.path.isfile(os.path.join(out_dir, base)):
                raise RuntimeError(f"воркер вернул недопустимый файл: {name}")
            files.append({"filename": base, "subfolder": OUTPUT_SUBFOLDER, "type": "output"})
        log.info("аудио-воркер %s: готово за %.0f с, файлов %d", op, time.time() - started, len(files))
        return {"ui": {"audio": files, "ccs_stats": [result.get("stats", {})]}}


NODE_CLASS_MAPPINGS = {"CcsAudioWorker": CcsAudioWorker}
NODE_DISPLAY_NAME_MAPPINGS = {"CcsAudioWorker": "CCS Audio Worker (venv)"}
