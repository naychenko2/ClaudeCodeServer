"""Распознавание речи и текста песен: faster-whisper large-v3-turbo (та же модель, что у
конвейера транскрибации) → .txt, .srt (субтитры) и .lrc (строки песни с таймкодами).

Для песен лучше отдавать вокал после separate: whisper на полном миксе теряет слова.
"""
import subprocess

import numpy as np

from ccs_common import load_job, fail

MODEL = "mobiuslabsgmbh/faster-whisper-large-v3-turbo"


def stamp_srt(t):
    ms = int(round(t * 1000))
    return f"{ms // 3600000:02d}:{ms // 60000 % 60:02d}:{ms // 1000 % 60:02d},{ms % 1000:03d}"


def stamp_lrc(t):
    cs = int(round(t * 100))
    return f"[{cs // 6000:02d}:{cs // 100 % 60:02d}.{cs % 100:02d}]"


def preload_cuda():
    """У ctranslate2 своих библиотек CUDA нет: грузим cublas и cudnn CUDA 12 из колёс nvidia
    глобально, дальше его dlopen находит их по soname."""
    import ctypes
    import glob
    import os
    import nvidia
    for pattern in ("cublas/lib/libcublasLt.so.12", "cublas/lib/libcublas.so.12", "cudnn/lib/libcudnn*.so.9"):
        for root in nvidia.__path__:
            for lib in sorted(glob.glob(os.path.join(root, pattern))):
                ctypes.CDLL(lib, mode=ctypes.RTLD_GLOBAL)


def main():
    job = load_job()
    if job.op != "transcribe":
        fail(f"операция {job.op} не для этого воркера")
    src = job.input(0, "звук")
    language = job.params.get("language") or None

    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", src, "-f", "f32le", "-ac", "1", "-ar", "16000", "-"],
                         capture_output=True, check=True).stdout
    pcm = np.frombuffer(raw, dtype=np.float32)

    preload_cuda()
    from faster_whisper import WhisperModel
    model = WhisperModel(MODEL, device="cuda", compute_type="float16")
    job.lap("load")
    segments, info = model.transcribe(pcm, language=language, beam_size=5, vad_filter=True)
    segments = [s for s in segments if s.text.strip()]
    job.lap("infer")

    with open(job.out("text.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(s.text.strip() for s in segments) + "\n")
    with open(job.out("subtitles.srt"), "w", encoding="utf-8") as f:
        for n, s in enumerate(segments, 1):
            f.write(f"{n}\n{stamp_srt(s.start)} --> {stamp_srt(s.end)}\n{s.text.strip()}\n\n")
    with open(job.out("lyrics.lrc"), "w", encoding="utf-8") as f:
        for s in segments:
            f.write(f"{stamp_lrc(s.start)}{s.text.strip()}\n")
    job.stats["language"] = info.language
    job.stats["audio_s"] = round(len(pcm) / 16000, 2)
    job.stats["segments"] = len(segments)
    job.done()


if __name__ == "__main__":
    main()
