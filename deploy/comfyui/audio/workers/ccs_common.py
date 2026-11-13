"""Общее для аудио-воркеров: разбор job.json, замер времени, запись результата.

Воркер — одноразовый процесс: загрузил модель, сделал работу, напечатал строку
CCS_RESULT и вышел (видеопамять освобождается вместе с процессом). По ходу работы воркер
с естественными шагами печатает строки CCS_PROGRESS (emit_progress).
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
        self.input_dir = d.get("input_dir") or ""  # путь к input ComfyUI; узел прокидывает явно
        # loras_root — путь к весам персонажа; узел прокидывает для LoRA-воркера, чтобы он повторно
        # проверил realpath(out_dir) внутри realpath(loras_root) — иначе shutil.rmtree по симлинку
        # мог бы удалить данные за пределами каталога loras
        self.loras_root = d.get("loras_root") or ""
        # рабочий каталог LoRA-воркера: создаёт и удаляет узел, он переживает SIGKILL воркера
        self.work_dir = d.get("work_dir") or ""
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


PROGRESS_MARK = "CCS_PROGRESS "


def emit_progress(step, total):
    """Строка протокола CCS_PROGRESS {"step": N, "total": M}: узел держит последнюю и двигает ProgressBar.

    Прогресс — подсказка, а не результат: кривые значения и закрытый stdout работу не роняют.
    """
    try:
        step, total = int(step), int(total)
    except (TypeError, ValueError):
        return
    if total <= 0:
        return
    step = max(0, min(step, total))
    try:
        print(PROGRESS_MARK + json.dumps({"step": step, "total": total}), flush=True)
    except (OSError, ValueError):
        pass


def fail(message):
    print(f"ОШИБКА: {message}", flush=True)
    sys.exit(2)


def to_mp3(wav_path, mp3_path, bitrate="320k"):
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", wav_path, "-b:a", bitrate, mp3_path], check=True)


def load_job():
    if len(sys.argv) != 2:
        fail("использование: worker.py job.json")
    return Job(sys.argv[1])


def true_peak_db(wav, sr, oversample=4):
    """Истинный пик (dBFS) по передискретизации в oversample раз; wav — (каналы, отсчёты) или (отсчёты,)."""
    import numpy as np
    from scipy.signal import resample_poly
    x = np.atleast_2d(np.asarray(wav, dtype=np.float64))
    peak = float(np.max(np.abs(resample_poly(x, oversample, 1, axis=-1)))) if x.size else 0.0
    return -200.0 if peak <= 0 else 20.0 * float(np.log10(peak))


def limit_true_peak(wav, sr, ceiling_db=-1.0, lookahead_ms=5.0, release_ms=50.0):
    """Ограничитель громкости: true peak результата не выше ceiling_db (по умолчанию −1 dBFS).

    Опережающий лимитер по огибающей 4x-пика: усиление опускается заранее на lookahead_ms и
    плавно возвращается за release_ms, поэтому без щелчков; в конце — страховка точным
    масштабированием, если оценка пика всё же выше потолка. Тише потолка — звук не меняется.
    wav — (каналы, отсчёты) float32/64; возвращает массив той же формы.
    """
    import numpy as np
    from scipy.ndimage import maximum_filter1d, uniform_filter1d
    from scipy.signal import resample_poly
    x = np.asarray(wav, dtype=np.float64)
    shape = x.shape
    x2 = np.atleast_2d(x)
    ceiling = 10.0 ** (ceiling_db / 20.0)
    if x2.size == 0 or true_peak_db(x2, sr) <= ceiling_db:
        return x.astype(np.float32)
    up = 4
    env = np.max(np.abs(resample_poly(x2, up, 1, axis=-1)), axis=0)  # огибающая по каналам, частота sr*4
    gain = np.minimum(1.0, ceiling / np.maximum(env, 1e-12))
    look = max(1, int(sr * up * lookahead_ms / 1000.0))
    # минимум усиления по окну вперёд/назад, затем сглаживание: гладкая кривая не выше исходной
    # (min-фильтр + скользящее среднее окна в 2*look+1 не поднимают её выше минимума в окне)
    g = -maximum_filter1d(-gain, size=2 * look + 1, mode="nearest")
    g = uniform_filter1d(g, size=2 * look + 1, mode="nearest")
    rel = max(1, int(sr * up * release_ms / 1000.0))
    g = np.minimum(g, uniform_filter1d(g, size=rel, mode="nearest") + 0.0) if rel > 1 else g
    g_base = g[::up]
    n = x2.shape[-1]
    g_base = np.interp(np.arange(n), np.arange(len(g_base)), g_base) if len(g_base) != n else g_base
    y = x2 * g_base
    tp = true_peak_db(y, sr)
    if tp > ceiling_db:  # страховка: точное масштабирование до потолка
        y = y * (10.0 ** ((ceiling_db - tp) / 20.0))
    return y.reshape(shape).astype(np.float32)
