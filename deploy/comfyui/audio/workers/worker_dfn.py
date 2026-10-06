"""DeepFilterNet 3: подавление шума в речи (48 кГц, работает и на CPU).

atten_db ограничивает глубину подавления: 0 — без ограничения, 10–20 дБ — мягче,
голос звучит естественнее.
"""
from ccs_common import fail, load_job


def main():
    job = load_job()
    if job.op != "denoise":
        fail(f"операция {job.op} не для этого воркера")
    src = job.input(0, "звук")
    atten = job.num("atten_db", 0, 0, 100)

    from df.enhance import enhance, init_df, load_audio, save_audio
    model, state, _ = init_df()
    job.lap("load")
    audio, _ = load_audio(src, sr=state.sr())
    clean = enhance(model, state, audio, atten_lim_db=atten or None)
    job.lap("infer")
    job.stats["audio_s"] = round(audio.shape[-1] / state.sr(), 2)
    save_audio(job.out("clean.wav"), clean, state.sr())
    job.done()


if __name__ == "__main__":
    main()
