"""AudioSR: восстановление верхних частот (любая частота → 48 кГц), диффузия.

model=basic — музыка и любые звуки, model=speech — речь. Долгий: примерно реальное время
и медленнее, поэтому вход ограничен по длине.
"""
import numpy as np

from ccs_common import fail, load_job

MAX_SECONDS = 120


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

    import audiosr
    model = audiosr.build_model(model_name=kind, device="cuda")
    job.lap("load")
    wav = audiosr.super_resolution(model, src, seed=seed, guidance_scale=guidance, ddim_steps=steps)
    job.lap("infer")
    wav = np.asarray(wav).squeeze()
    if wav.ndim > 1:
        wav = wav.T
    # AudioSR дополняет вход до кратного окна — обрезаем до исходной длины
    wav = wav[: int(info.duration * 48000)]
    job.stats["audio_s"] = round(info.duration, 2)
    job.save_wav("48k", wav, 48000)
    job.done()


if __name__ == "__main__":
    main()
