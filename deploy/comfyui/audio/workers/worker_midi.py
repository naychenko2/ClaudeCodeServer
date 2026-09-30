"""Basic Pitch (Spotify): аудио → MIDI, полифония, модель ONNX на CPU.

Лучше всего работает на одном инструменте или вокале — для полного микса сначала
разделить на стемы (separate).
"""
import os

from ccs_common import fail, load_job


def main():
    job = load_job()
    if job.op != "audio_to_midi":
        fail(f"операция {job.op} не для этого воркера")
    src = job.input(0, "звук")
    onset = job.num("onset_threshold", 0.5, 0.05, 0.95)
    frame = job.num("frame_threshold", 0.3, 0.05, 0.95)
    min_note_ms = job.num("min_note_ms", 58, 10, 1000)

    from basic_pitch import ICASSP_2022_MODEL_PATH
    from basic_pitch.inference import predict
    onnx = os.path.splitext(str(ICASSP_2022_MODEL_PATH))[0] + ".onnx"
    job.lap("load")
    _, midi, notes = predict(src, onnx if os.path.exists(onnx) else ICASSP_2022_MODEL_PATH,
                             onset_threshold=onset, frame_threshold=frame, minimum_note_length=min_note_ms)
    job.lap("infer")
    midi.write(job.out("notes.mid"))
    job.stats["notes"] = len(notes)
    job.done()


if __name__ == "__main__":
    main()
