"""MOSS-TTS v1.5 (8B, Apache-2.0): озвучка на 31 языке и клон по образцу — tts_moss.

A/B на русском (2026-10-01): клон CER 0 % при похожести 0,92 — разборчиво, как Qwen3-TTS, и почти
по тембру, как Chatterbox, и быстрее обоих. Модель bf16 ≈17 ГБ, поэтому кодек (MOSS-Audio-Tokenizer)
держим на CPU: вместе они в 24 ГБ не влезают. Длинный текст режется по предложениям; без образца
голос первого куска становится образцом для остальных, чтобы голос не менялся между кусками.
"""
import os
import re

import numpy as np
import soundfile as sf
import torch

from ccs_common import MODELS, fail, load_job

LANGS = {
    "zh": "Chinese", "yue": "Cantonese", "en": "English", "ar": "Arabic", "cs": "Czech", "da": "Danish",
    "nl": "Dutch", "fi": "Finnish", "fr": "French", "de": "German", "el": "Greek", "he": "Hebrew", "hi": "Hindi",
    "hu": "Hungarian", "it": "Italian", "ja": "Japanese", "ko": "Korean", "mk": "Macedonian", "ms": "Malay",
    "fa": "Persian", "pl": "Polish", "pt": "Portuguese", "ro": "Romanian", "ru": "Russian", "es": "Spanish",
    "sw": "Swahili", "sv": "Swedish", "tl": "Tagalog", "th": "Thai", "tr": "Turkish", "vi": "Vietnamese",
}
CHUNK = 400


def _load(path, *args, **kwargs):
    # torchaudio 2.9 читает через torchcodec 0.8, а тот не умеет системный FFmpeg 8
    data, sr = sf.read(str(path), dtype="float32", always_2d=True)
    return torch.from_numpy(data.T.copy()), sr


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
    if job.op != "tts_moss":
        fail(f"операция {job.op} не для этого воркера")
    text = job.text("text", 5000)
    lang = job.choice("language", "ru", list(LANGS))
    ref = job.inputs[0] if job.inputs else None
    torch.manual_seed(int(job.params.get("seed", 0)) or 0)

    import torchaudio
    torchaudio.load = _load
    torch.backends.cuda.enable_cudnn_sdp(False)
    from transformers import AutoModel, AutoProcessor
    root = os.path.join(MODELS, "OpenMOSS-Team")
    processor = AutoProcessor.from_pretrained(os.path.join(root, "MOSS-TTS-v1.5"), trust_remote_code=True,
                                              codec_path=os.path.join(root, "MOSS-Audio-Tokenizer"))
    processor.audio_tokenizer = processor.audio_tokenizer.to("cpu")
    model = AutoModel.from_pretrained(os.path.join(root, "MOSS-TTS-v1.5"), trust_remote_code=True,
                                      attn_implementation="sdpa", torch_dtype=torch.bfloat16).to("cuda").eval()
    sr = processor.model_config.sampling_rate
    job.lap("load")

    pieces = []
    tmp_ref = None
    for k, part in enumerate(chunks(text)):
        msg = processor.build_user_message(text=part, language=LANGS[lang], reference=[ref] if ref else None)
        batch = processor([[msg]], mode="generation")
        with torch.no_grad():
            # ~12,5 кодов на секунду, речь не медленнее ~8 знаков в секунду: потолок от длины куска
            outputs = model.generate(input_ids=batch["input_ids"].to("cuda"),
                                     attention_mask=batch["attention_mask"].to("cuda"),
                                     max_new_tokens=min(4096, int(len(part) / 8 * 12.5) + 150))
        wav = next(iter(processor.decode(outputs))).audio_codes_list[0].float().cpu().numpy()
        pieces += [wav, np.zeros(int(sr * 0.25), dtype=np.float32)]
        if ref is None and k == 0:
            tmp_ref = job.out("ref.wav")
            sf.write(tmp_ref, wav, sr)
            job.files.remove(os.path.basename(tmp_ref))
            ref = tmp_ref
    audio = np.concatenate(pieces)
    job.lap("infer")
    if tmp_ref:
        os.remove(tmp_ref)
    job.stats["audio_s"] = round(len(audio) / sr, 2)
    job.save_wav("speech", audio, sr)
    job.done()


if __name__ == "__main__":
    main()
