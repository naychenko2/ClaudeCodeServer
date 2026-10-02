"""Общее для аудио-воркеров: разбор job.json, замер времени, запись результата.

Воркер — одноразовый процесс: загрузил модель, сделал работу, напечатал строку
CCS_RESULT и вышел (видеопамять освобождается вместе с процессом).
"""
import json
import os
import subprocess
import sys
import time

MODELS = os.environ.get("CCS_AUDIO_MODELS", os.path.expanduser("~/ai-data/audio-models"))


class Job:
    def __init__(self, path):
        with open(path, encoding="utf-8") as f:
            d = json.load(f)
        self.op = d["op"]
        self.params = d.get("params") or {}
        self.inputs = d.get("inputs") or []
        self.out_dir = d["out_dir"]
        self.prefix = d.get("prefix") or "job"
        self.files = []
        self.stats = {}
        self._t0 = time.time()
        self._mark = self._t0

    def lap(self, name):
        """Время с прошлой отметки — в stats[name_s]."""
        now = time.time()
        self.stats[f"{name}_s"] = round(now - self._mark, 2)
        self._mark = now

    def text(self, name, max_len=5000, required=True):
        v = self.params.get(name)
        if v is None or (isinstance(v, str) and not v.strip()):
            if required:
                fail(f"нужен параметр {name}")
            return None
        v = str(v).strip()
        if len(v) > max_len:
            fail(f"{name} длиннее {max_len} символов")
        return v

    def num(self, name, default, lo, hi, cast=float):
        v = self.params.get(name, default)
        try:
            v = cast(v)
        except (TypeError, ValueError):
            fail(f"{name} — число")
        if not lo <= v <= hi:
            fail(f"{name} — от {lo} до {hi}")
        return v

    def choice(self, name, default, allowed):
        v = self.params.get(name, default)
        if v not in allowed:
            fail(f"{name} — одно из: {', '.join(map(str, allowed))}")
        return v

    def input(self, k, what="вход"):
        if k >= len(self.inputs):
            fail(f"не передан {what}")
        return self.inputs[k]

    def out(self, suffix):
        name = f"{self.prefix}_{suffix}"
        self.files.append(name)
        return os.path.join(self.out_dir, name)

    def save_wav(self, suffix, wav, sr):
        import soundfile as sf
        path = self.out(suffix + ".wav")
        sf.write(path, wav, sr)
        return path

    def done(self):
        self.stats["total_s"] = round(time.time() - self._t0, 2)
        try:
            import torch
            if torch.cuda.is_available():
                self.stats["vram_peak_mb"] = int(torch.cuda.max_memory_allocated() / 2**20)
        except Exception:
            pass
        print("CCS_RESULT " + json.dumps({"files": self.files, "stats": self.stats}, ensure_ascii=False), flush=True)


def fail(message):
    print(f"ОШИБКА: {message}", flush=True)
    sys.exit(2)


def to_mp3(wav_path, mp3_path, bitrate="320k"):
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", wav_path, "-b:a", bitrate, mp3_path], check=True)


def load_job():
    if len(sys.argv) != 2:
        fail("использование: worker.py job.json")
    return Job(sys.argv[1])
