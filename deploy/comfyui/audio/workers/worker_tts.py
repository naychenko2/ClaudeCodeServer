"""Qwen3-TTS 1.7B: озвучка текста (tts) и клон голоса по образцу (voice_clone).

tts: голос задаётся описанием (VoiceDesign) или готовым диктором (CustomVoice, speaker).
Длинный текст режется по предложениям; чтобы голос не «плыл» между кусками, первый
кусок озвучивается по описанию, а остальные — клоном этого же куска (модель Base).
voice_clone: образец голоса (3–30 с) + необязательная его расшифровка ref_text.
"""
import os
import re

import numpy as np
import torch

from ccs_common import MODELS, fail, load_job

LANGS = ["auto", "russian", "english", "chinese", "japanese", "korean", "german", "french",
         "portuguese", "spanish", "italian"]
SPEAKERS = ["Vivian", "Serena", "Uncle_Fu", "Dylan", "Eric", "Ryan", "Aiden", "Ono_Anna", "Sohee"]
CHUNK = 400


def model(name):
    from qwen_tts import Qwen3TTSModel
    return Qwen3TTSModel.from_pretrained(os.path.join(MODELS, "Qwen", name), device_map="cuda:0",
                                         dtype=torch.bfloat16, attn_implementation="sdpa")


def chunks(text):
    parts, cur = [], ""
    for s in re.split(r"(?<=[.!?…])\s+", text.strip()):
        if cur and len(cur) + len(s) > CHUNK:
            parts.append(cur)
            cur = s
        else:
            cur = f"{cur} {s}".strip()
    if cur:
        parts.append(cur)
    return parts


def budget(parts):
    # 12 кодов в секунду; речь не медленнее ~8 символов в секунду. Без потолка зациклившаяся
    # генерация идёт до 2048 кодов (~3 мин звука) и держит GPU
    return min(2048, int(max(len(p) for p in parts) / 8 * 12) + 120)


REF_MAX_S = 15


def transcribe_ref(path, language):
    """Распознать образец (faster-whisper на CPU) и обрезать его по концу слова до REF_MAX_S.

    Возвращает (звук, sr) образца и его точную расшифровку.
    """
    import subprocess
    from faster_whisper import WhisperModel

    def decode(sr):
        raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", path, "-f", "f32le", "-ac", "1", "-ar", str(sr), "-"],
                             capture_output=True, check=True).stdout
        return np.frombuffer(raw, dtype=np.float32)

    pcm = decode(24000)
    asr = WhisperModel("mobiuslabsgmbh/faster-whisper-large-v3-turbo", device="cpu", compute_type="int8")
    segs, _ = asr.transcribe(decode(16000), language=language, word_timestamps=True, vad_filter=True)
    words = [w for s in segs for w in (s.words or [])]
    if not words:
        fail("в образце голоса не распознано ни одного слова — нужна запись речи")
    kept = [w for w in words if w.end <= REF_MAX_S] or words[:1]
    end = min(len(pcm) / 24000, kept[-1].end + 0.2)
    text = "".join(w.word for w in kept).strip()
    return (pcm[: int(end * 24000)], 24000), text


def lang(job):
    v = job.choice("language", "auto", LANGS)
    return "Auto" if v == "auto" else v.capitalize()


def main():
    job = load_job()
    torch.manual_seed(int(job.params.get("seed", 0)) or 0)
    text = job.text("text", max_len=5000)
    language = lang(job)
    parts = chunks(text)
    pause = None

    if job.op == "tts":
        speaker = job.params.get("speaker")
        if speaker:
            if speaker not in SPEAKERS:
                fail("speaker — один из: " + ", ".join(SPEAKERS))
            m = model("Qwen3-TTS-12Hz-1.7B-CustomVoice")
            job.lap("load")
            instruct = job.text("instruct", 500, required=False) or ""
            wavs, sr = m.generate_custom_voice(text=parts, language=[language] * len(parts),
                                               speaker=[speaker] * len(parts), instruct=[instruct] * len(parts), max_new_tokens=budget(parts))
        else:
            voice = job.text("voice", 500, required=False) or \
                "Спокойный, ясный голос диктора средних лет, естественная интонация."
            design = model("Qwen3-TTS-12Hz-1.7B-VoiceDesign")
            job.lap("load")
            first, sr = design.generate_voice_design(text=parts[0], language=language, instruct=voice,
                                                     max_new_tokens=budget(parts[:1]))
            wavs = [first[0]]
            if len(parts) > 1:
                del design
                torch.cuda.empty_cache()
                base = model("Qwen3-TTS-12Hz-1.7B-Base")
                prompt = base.create_voice_clone_prompt(ref_audio=(first[0], sr), ref_text=parts[0])
                rest, sr = base.generate_voice_clone(text=parts[1:], language=[language] * (len(parts) - 1),
                                                     voice_clone_prompt=prompt, max_new_tokens=budget(parts))
                wavs += list(rest)
    elif job.op == "voice_clone":
        ref = job.input(0, "образец голоса")
        ref_text = job.text("ref_text", 2000, required=False)
        if ref_text is None:
            # Расшифровка обязана совпадать с образцом слово в слово: на обрывке фразы
            # модель «договаривает» и сыплется в кашу (замер: CER 77 % против 0,7 %)
            ref, ref_text = transcribe_ref(ref, "ru" if language == "Russian" else None)
            job.lap("ref_asr")
        m = model("Qwen3-TTS-12Hz-1.7B-Base")
        job.lap("load")
        prompt = m.create_voice_clone_prompt(ref_audio=ref, ref_text=ref_text)
        wavs, sr = m.generate_voice_clone(text=parts, language=[language] * len(parts), voice_clone_prompt=prompt,
                                          max_new_tokens=budget(parts))
    else:
        fail(f"операция {job.op} не для этого воркера")

    pause = np.zeros(int(sr * 0.25), dtype=np.float32)
    audio = np.concatenate([np.concatenate([np.asarray(w, dtype=np.float32), pause]) for w in wavs])
    job.lap("infer")
    job.stats["audio_s"] = round(len(audio) / sr, 2)
    job.stats["chunks"] = len(parts)
    job.save_wav("speech", audio, sr)
    job.done()


if __name__ == "__main__":
    main()
