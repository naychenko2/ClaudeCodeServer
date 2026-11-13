"""MOSS-SoundEffect v2: звуковые эффекты по тексту (48 кГц, стерео) — sfx_moss.

Замер на 3090 (13.11.2026): 21–25 с на клип плюс загрузка модели 29 с, пик видеопамяти 18 ГБ, поэтому
процесс одноразовый: вышел — карта свободна. Модель перегружает звук (пик +1,1 dBFS), поэтому
результат всегда проходит ограничитель: true peak не выше −1 dBFS.
Веса — только из каталога весов (HF_HUB_OFFLINE), пакет и его зависимости (transformers 4.57.1,
diffusers 0.37.1, numpy 1.26.4) закреплены в venv семейства.
"""
import os

from ccs_common import emit_progress, fail, limit_true_peak, load_job, true_peak_db

MODEL_DIR = os.environ.get("CCS_MOSS_SFX_DIR", os.path.expanduser("~/ai-data/audio-extra/moss-sfx-v2"))
MIN_SEC, MAX_SEC = 1.0, 10.0
CEILING_DB = -1.0


def main():
    job = load_job()
    if job.op != "sfx_moss":
        fail(f"операция {job.op} не для этого воркера")
    prompt = job.text("prompt", 1000)
    seconds = job.num("duration", 5.0, MIN_SEC, MAX_SEC)
    steps = job.num("steps", 100, 20, 200, int)
    cfg = job.num("cfg", 4.0, 1.0, 10.0)
    seed = job.num("seed", 0, 0, 2**50, int)
    if not os.path.isdir(MODEL_DIR):
        fail("веса MOSS-SoundEffect v2 не установлены")

    import numpy as np
    import torch
    from moss_soundeffect_v2 import MossSoundEffectPipeline

    pipe = MossSoundEffectPipeline.from_pretrained(MODEL_DIR, torch_dtype=torch.bfloat16, device="cuda")
    job.lap("load")
    emit_progress(0, 1)
    audio = pipe(prompt=prompt, seconds=seconds, num_inference_steps=steps, cfg_scale=cfg, seed=seed)
    wav = audio[0].detach().float().cpu().numpy()  # (каналы, отсчёты)
    sr = int(pipe.sample_rate)
    job.lap("infer")
    emit_progress(1, 1)
    job.stats["peak_before_db"] = round(true_peak_db(wav, sr), 2)
    wav = limit_true_peak(wav, sr, CEILING_DB)
    job.stats["peak_after_db"] = round(true_peak_db(wav, sr), 2)
    job.stats["audio_s"] = round(wav.shape[-1] / sr, 2)
    import soundfile as sf
    sf.write(job.out("sfx.wav"), np.ascontiguousarray(wav.T), sr, subtype="PCM_24")
    job.done()


if __name__ == "__main__":
    main()
