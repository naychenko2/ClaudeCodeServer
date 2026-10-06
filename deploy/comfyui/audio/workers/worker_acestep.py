"""ACE-Step 1.5 (официальный пакет): правка готового трека — music_edit.

task:
  cover    — перепеть/переиграть трек в другом стиле (caption), strength — насколько держаться исходника
  repaint  — перегенерировать кусок start..end (с), остальное остаётся
  extract  — вытащить одну дорожку (track) из микса            ┐
  lego     — дописать дорожку (track) поверх готового трека     ├ только модель xl-base
  complete — доаранжировать трек инструментами из tracks        ┘
Генерация по тексту живёт в ComfyUI (нативные ноды); здесь только правка, которой ComfyUI не умеет.
Чекпоинты — $ACESTEP_CHECKPOINTS_DIR (подкаталоги acestep-v15-turbo, acestep-v15-xl-base, vae, …).
"""
import glob
import os
import shutil
import tempfile

from ccs_common import fail, load_job, to_mp3

TRACKS = ["vocals", "backing_vocals", "drums", "bass", "guitar", "keyboard", "percussion", "strings", "synth",
          "fx", "brass", "woodwinds"]
BASE_ONLY = {"extract", "lego", "complete"}


def main():
    job = load_job()
    if job.op != "music_edit":
        fail(f"операция {job.op} не для этого воркера")
    task = job.choice("task", "cover", ["cover", "repaint", "extract", "lego", "complete"])
    src = job.input(0, "исходный трек")
    caption = job.text("prompt", 2000, required=task in ("cover", "repaint")) or ""
    lyrics = job.text("lyrics", 5000, required=False) or ""
    seed = int(job.params.get("seed", -1))

    from acestep.handler import AceStepHandler
    from acestep.inference import GenerationConfig, GenerationParams, generate_music
    from acestep.llm_inference import LLMHandler

    kw = dict(task_type=task, src_audio=src, caption=caption, lyrics=lyrics, seed=seed, thinking=False,
              use_cot_metas=False, use_cot_caption=False, use_cot_language=False)
    if task == "cover":
        kw["audio_cover_strength"] = job.num("strength", 0.6, 0.0, 1.0)
    elif task == "repaint":
        kw["repainting_start"] = job.num("start", 0, 0, 600)
        kw["repainting_end"] = job.num("end", -1, -1, 600)
        kw["chunk_mask_mode"] = "explicit"
    elif task in ("extract", "lego"):
        track = job.choice("track", "vocals", TRACKS)
        kw["instruction"] = (f"Extract the {track} track from the audio:" if task == "extract"
                             else f"Generate the {track} track based on the audio context:")
        if task == "lego":
            kw["repainting_start"], kw["repainting_end"] = 0.0, -1
    else:
        tracks = job.params.get("tracks") or ["drums", "bass"]
        if not isinstance(tracks, list) or not tracks or any(t not in TRACKS for t in tracks):
            fail("tracks — список из: " + ", ".join(TRACKS))
        kw["instruction"] = f"Complete the input track with {', '.join(tracks)}:"

    base = task in BASE_ONLY
    config = "acestep-v15-xl-base" if base else "acestep-v15-turbo"
    kw["inference_steps"] = 50 if base else 8
    if base:
        kw["guidance_scale"] = 7.0
        kw["shift"] = 3.0

    dit = AceStepHandler()
    status, ok = dit.initialize_service(project_root=os.path.dirname(os.path.abspath(__file__)),
                                        config_path=config, device="cuda")
    if not ok:
        fail(f"ACE-Step не загрузился: {status}")
    job.lap("load")

    tmp = tempfile.mkdtemp(prefix="acestep-")
    try:
        result = generate_music(dit, LLMHandler(), GenerationParams(**kw),
                                GenerationConfig(batch_size=1, use_random_seed=seed < 0,
                                                 seeds=None if seed < 0 else [seed], audio_format="wav"),
                                save_dir=tmp)
        if not result.success:
            fail(f"ACE-Step: {result.error}")
        job.lap("infer")
        wav = sorted(glob.glob(os.path.join(tmp, "**", "*.wav"), recursive=True), key=os.path.getmtime)
        if not wav:
            fail("ACE-Step не записал результат")
        to_mp3(wav[-1], job.out(f"{task}.mp3"))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    job.stats["task"] = task
    job.stats["model"] = config
    job.done()


if __name__ == "__main__":
    main()
