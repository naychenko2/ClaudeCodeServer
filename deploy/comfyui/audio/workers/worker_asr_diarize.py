"""Распознавание встреч со спикерами: WhisperX large-v3-turbo + диаризация pyannote → .json.

Заказчик — конвейер транскрибации встреч: он шлёт сюда звук встречи и забирает сегменты
со спикерами, а саммари делает сам. Версии (whisperx 3.8.6, pyannote 4.0.7, torch 2.8)
те же, что у конвейера, чтобы транскрипты не разошлись с уже накопленными.
"""
import json

from ccs_common import load_job, fail

MODEL = "large-v3-turbo"


def patch_torch_load(torch):
    """Веса pyannote сохранены с объектами, а torch 2.6+ по умолчанию грузит только тензоры."""
    original = torch.load

    def patched(*args, **kwargs):
        kwargs["weights_only"] = False
        return original(*args, **kwargs)

    torch.load = patched


def main():
    job = load_job()
    if job.op != "transcribe_diarize":
        fail(f"операция {job.op} не для этого воркера")
    src = job.input(0, "звук")
    language = job.params.get("language") or "ru"
    batch_size = job.num("batch_size", 8, 1, 32, int)

    import torch
    import whisperx
    from whisperx.diarize import DiarizationPipeline
    patch_torch_load(torch)
    torch.backends.cuda.matmul.allow_tf32 = True
    torch.backends.cudnn.allow_tf32 = True

    # CPU — только для проверки воркера на машине без свободной карты
    device = "cuda" if torch.cuda.is_available() else "cpu"
    audio = whisperx.load_audio(src)
    job.lap("decode")
    asr = whisperx.load_model(MODEL, device, compute_type="float16" if device == "cuda" else "int8",
                              language=language)
    diarize = DiarizationPipeline(device=device)
    job.lap("load")
    result = asr.transcribe(audio, batch_size=batch_size, language=language)
    job.lap("asr")
    result = whisperx.assign_word_speakers(diarize(audio), result)
    job.lap("diarize")

    segments = [{"start": round(s.get("start", 0.0), 2), "end": round(s.get("end", 0.0), 2),
                 "speaker": s.get("speaker", "UNKNOWN"), "text": s.get("text", "").strip()}
                for s in result.get("segments", []) if s.get("text", "").strip()]
    with open(job.out("segments.json"), "w", encoding="utf-8") as f:
        json.dump({"language": language, "segments": segments}, f, ensure_ascii=False)
    job.stats["audio_s"] = round(len(audio) / 16000, 2)
    job.stats["segments"] = len(segments)
    job.stats["speakers"] = len({s["speaker"] for s in segments})
    job.done()


if __name__ == "__main__":
    main()
