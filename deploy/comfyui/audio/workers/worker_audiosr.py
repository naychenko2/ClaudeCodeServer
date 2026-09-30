"""AudioSR: восстановление верхних частот (любая частота → 48 кГц), диффузия.

model=basic — музыка и любые звуки, model=speech — речь. Память растёт с длиной входа (30 с —
13,5 ГБ, 120 с — OOM на 24 ГБ), поэтому вход режется на куски по CHUNK_S с перекрытием и
сшивается кроссфейдом. Скорость ≈ реальное время плюс загрузка, отсюда потолок длины.
"""
import os
import subprocess
import tempfile

import numpy as np

from ccs_common import fail, load_job

MAX_SECONDS = 120
CHUNK_S = 20.0
OVERLAP_S = 0.5
SR = 48000


def decode(path, sr):
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", path, "-f", "f32le", "-ac", "1", "-ar", str(sr), "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32)


def main():
    job = load_job()
    if job.op != "upsample":
        fail(f"операция {job.op} не для этого воркера")
    src = job.input(0, "звук")
    kind = job.choice("model", "basic", ["basic", "speech"])
    steps = int(job.num("ddim_steps", 50, 10, 200, int))
    guidance = job.num("guidance_scale", 3.5, 1.0, 10.0)
    seed = int(job.params.get("seed", 42))

    import soundfile as sf
    info = sf.info(src)
    if info.duration > MAX_SECONDS:
        fail(f"вход длиннее {MAX_SECONDS} с ({info.duration:.0f} с)")
    # исходная частота сохраняется: AudioSR сам решает, что восстанавливать
    audio = decode(src, info.samplerate)
    sr_in = info.samplerate

    import audiosr
    model = audiosr.build_model(model_name=kind, device="cuda")
    job.lap("load")

    step = int((CHUNK_S - OVERLAP_S) * sr_in)
    size = int(CHUNK_S * sr_in)
    fade = int(OVERLAP_S * SR)
    out = np.zeros(0, dtype=np.float32)
    tmp = tempfile.mkdtemp(prefix="audiosr-")
    try:
        for k, start in enumerate(range(0, max(1, len(audio) - int(OVERLAP_S * sr_in)), step)):
            chunk = audio[start:start + size]
            path = os.path.join(tmp, f"{k}.wav")
            sf.write(path, chunk, sr_in)
            wav = np.asarray(audiosr.super_resolution(model, path, seed=seed, guidance_scale=guidance,
                                                      ddim_steps=steps), dtype=np.float32).squeeze()
            if wav.ndim > 1:
                wav = wav.mean(axis=0)
            # AudioSR дополняет кусок до кратного окна — обрезаем до его настоящей длины
            wav = wav[: int(len(chunk) / sr_in * SR)]
            if len(out) >= fade and len(wav) >= fade:
                ramp = np.linspace(0, 1, fade, dtype=np.float32)
                out[-fade:] = out[-fade:] * (1 - ramp) + wav[:fade] * ramp
                wav = wav[fade:]
            out = np.concatenate([out, wav])
            job.stats["chunks"] = k + 1
    finally:
        subprocess.run(["rm", "-rf", tmp], check=False)
    job.lap("infer")
    job.stats["audio_s"] = round(info.duration, 2)
    job.save_wav("48k", out, SR)
    job.done()


if __name__ == "__main__":
    main()
