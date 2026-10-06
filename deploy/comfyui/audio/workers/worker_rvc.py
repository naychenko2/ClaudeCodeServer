"""RVC через Applio: обучение модели голоса (rvc_train) и смена голоса ею (rvc_convert).

rvc_train: входы — записи одного голоса (в сумме от ~2 до 30 мин, чистая речь или пение
без музыки). Результат — модель .pth и индекс .index; они ложатся в проект и дальше
передаются в rvc_convert как входы — общего реестра голосов на сервере нет.
rvc_convert: вход 1 — что переозвучить, вход 2 — .pth, вход 3 (необязательный) — .index.
Работает CLI самого Applio (cwd — корень applio).
"""
import glob
import os
import shutil
import subprocess
import sys
import tempfile

from ccs_common import fail, load_job

CORE = [sys.executable, "core.py"]


def run(args):
    subprocess.run(CORE + args, check=True)


def train(job):
    if not job.inputs:
        fail("нужны записи голоса для обучения")
    name = "".join(c for c in job.prefix if c.isalnum() or c in "-_")
    epochs = int(job.num("epochs", 200, 10, 1000, int))
    sr = str(job.choice("sample_rate", 40000, [32000, 40000, 48000]))
    batch = int(job.num("batch_size", 8, 1, 32, int))
    data = tempfile.mkdtemp(prefix="rvc-data-")
    try:
        for k, src in enumerate(job.inputs):
            shutil.copy(src, os.path.join(data, f"{k:03d}{os.path.splitext(src)[1]}"))
        run(["preprocess", "--model-name", name, "--dataset-path", data, "--sample-rate", sr,
             "--cpu-cores", "8", "--cut-preprocess", "Automatic"])
        job.lap("preprocess")
        run(["extract", "--model-name", name, "--f0-method", "rmvpe", "--gpu", "0",
             "--sample-rate", sr, "--embedder-model", "contentvec", "--cpu-cores", "8"])
        job.lap("extract")
        run(["train", "--model-name", name, "--total-epoch", str(epochs), "--sample-rate", sr,
             "--batch-size", str(batch), "--gpu", "0", "--save-every-epoch", str(epochs),
             "--save-only-latest", "--pretrained", "--cleanup"])
        job.lap("train")
        run(["index", "--model-name", name, "--index-algorithm", "Auto"])
        job.lap("index")
    finally:
        shutil.rmtree(data, ignore_errors=True)

    logs = os.path.join("logs", name)
    weights = sorted(glob.glob(os.path.join(logs, f"{name}_*e_*s.pth")), key=os.path.getmtime)
    index = sorted(glob.glob(os.path.join(logs, "*.index")), key=os.path.getmtime)
    if not weights:
        fail("Applio не сохранил модель")
    shutil.copy(weights[-1], job.out("voice.pth"))
    if index:
        shutil.copy(index[-1], job.out("voice.index"))
    shutil.rmtree(logs, ignore_errors=True)
    job.stats["epochs"] = epochs


def convert(job):
    src = job.input(0, "звук для переозвучки")
    pth = job.input(1, "модель голоса .pth")
    if not pth.endswith(".pth"):
        fail("второй вход — модель голоса .pth (результат rvc_train)")
    index = job.inputs[2] if len(job.inputs) > 2 else ""
    pitch = int(job.num("pitch_shift", 0, -24, 24, int))
    index_rate = job.num("index_rate", 0.3 if index else 0.0, 0.0, 1.0)
    out = job.out("converted.wav")
    run(["infer", "--input-path", src, "--output-path", out, "--pth-path", pth, "--index-path", index,
         "--pitch", str(pitch), "--index-rate", str(index_rate), "--f0-method", "rmvpe",
         "--split-audio", "--export-format", "WAV"])
    job.lap("infer")


def main():
    job = load_job()
    if job.op == "rvc_train":
        train(job)
    elif job.op == "rvc_convert":
        convert(job)
    else:
        fail(f"операция {job.op} не для этого воркера")
    job.done()


if __name__ == "__main__":
    main()
