"""Chatterbox Multilingual (Resemble AI, MIT): озвучка на 23 языках, клон по образцу.

Нужен для A/B с Qwen3-TTS на русском. Вход 1 (необязательный) — образец голоса.
exaggeration — выразительность (0,25–2), cfg_weight — темп и близость к образцу.
"""
import os
import re

import numpy as np
import torch

from ccs_common import MODELS, fail, load_job

CHUNK = 300


def chunks(text):
    parts, cur = [], ""
    for s in re.split(r"(?<=[.!?…])\s+", text.strip()):
        if cur and len(cur) + len(s) > CHUNK:
            parts.append(cur)
            cur = s
        else:
            cur = f"{cur} {s}".strip()
    return parts + ([cur] if cur else [])


def main():
    job = load_job()
    if job.op != "tts_chatterbox":
        fail(f"операция {job.op} не для этого воркера")
    text = job.text("text", 5000)
    lang = job.text("language", 8, required=False) or "ru"
    exaggeration = job.num("exaggeration", 0.5, 0.25, 2.0)
    cfg = job.num("cfg_weight", 0.5, 0.0, 1.0)
    ref = job.inputs[0] if job.inputs else None
    torch.manual_seed(int(job.params.get("seed", 0)) or 0)

    from chatterbox.mtl_tts import ChatterboxMultilingualTTS
    model = ChatterboxMultilingualTTS.from_local(os.path.join(MODELS, "ResembleAI", "chatterbox"), device="cuda")
    job.lap("load")
    pieces = []
    for part in chunks(text):
        wav = model.generate(part, language_id=lang, audio_prompt_path=ref,
                             exaggeration=exaggeration, cfg_weight=cfg)
        pieces += [wav.squeeze(0).cpu().numpy(), np.zeros(int(model.sr * 0.25), dtype=np.float32)]
    audio = np.concatenate(pieces)
    job.lap("infer")
    job.stats["audio_s"] = round(len(audio) / model.sr, 2)
    job.save_wav("speech", audio, model.sr)
    job.done()


if __name__ == "__main__":
    main()
