"""Seed-VC: смена голоса без обучения (zero-shot) — речь и пение.

Вход 1 — исходная запись (что говорят/поют), вход 2 — образец целевого голоса (1–30 с).
mode=speech — модель v2 (AR + CFM, 22 кГц): convert_style переносит ещё и манеру/акцент,
сдвига высоты нет — pitch_shift≠0 в speech бэкенд отклоняет ещё до очереди;
mode=singing — модель v1 с опорой на F0 (44 кГц, BigVGAN), pitch_shift — сдвиг в полутонах.
Работает скриптами самого репозитория (cwd — корень seed-vc): их загрузка весов через
HF-кэш остаётся как у авторов.
"""
import glob
import os
import shutil
import subprocess
import sys
import tempfile

from ccs_common import fail, load_job, to_mp3


def main():
    job = load_job()
    if job.op != "voice_convert":
        fail(f"операция {job.op} не для этого воркера")
    source = job.input(0, "исходная запись")
    target = job.input(1, "образец голоса")
    mode = job.choice("mode", "speech", ["speech", "singing"])
    steps = int(job.num("diffusion_steps", 30 if mode == "speech" else 40, 4, 100, int))
    tmp = tempfile.mkdtemp(prefix="seedvc-")
    try:
        if mode == "speech":
            cmd = [sys.executable, "inference_v2.py", "--source", source, "--target", target, "--output", tmp,
                   "--diffusion-steps", str(steps),
                   "--convert-style", "true" if job.params.get("convert_style") else "false"]
        else:
            shift = int(job.num("pitch_shift", 0, -24, 24, int))
            cmd = [sys.executable, "inference.py", "--source", source, "--target", target, "--output", tmp,
                   "--diffusion-steps", str(steps), "--f0-condition", "true",
                   "--auto-f0-adjust", "false", "--semi-tone-shift", str(shift), "--fp16", "true"]
        subprocess.run(cmd, check=True)
        job.lap("infer")
        out = sorted(glob.glob(os.path.join(tmp, "*.wav")), key=os.path.getmtime)
        if not out:
            fail("Seed-VC не записал результат")
        wav = job.out("converted.wav")
        shutil.move(out[-1], wav)
        if mode == "singing":
            job.files.remove(os.path.basename(wav))
            to_mp3(wav, job.out("converted.mp3"))
            os.remove(wav)
        job.stats["mode"] = mode
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    job.done()


if __name__ == "__main__":
    main()
