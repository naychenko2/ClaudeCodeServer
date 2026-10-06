"""Стемы (audio-separator: BS-/Mel-RoFormer, HTDemucs) и мастеринг по референсу (Matchering, CPU).

separate, mode:
  vocals  — вокал + минус (BS-RoFormer Viperx 1297, лучший SDR вокала из открытых)
  4stems  — вокал, барабаны, бас, прочее (HTDemucs ft)
  6stems  — плюс гитара и пианино (HTDemucs 6s)
  karaoke — основной вокал отдельно от бэк-вокала и музыки (Mel-RoFormer karaoke)
master: вход 1 — трек, вход 2 — референс, на который равняется громкость и АЧХ.
"""
import os

from ccs_common import MODELS, fail, load_job, to_mp3

SEPARATE = {
    "vocals": "model_bs_roformer_ep_317_sdr_12.9755.ckpt",
    "4stems": "htdemucs_ft.yaml",
    "6stems": "htdemucs_6s.yaml",
    "karaoke": "mel_band_roformer_karaoke_becruily.ckpt",
}


def separate(job):
    mode = job.choice("mode", "vocals", list(SEPARATE))
    fmt = job.choice("format", "mp3", ["mp3", "wav", "flac"])
    src = job.input(0, "трек")
    from audio_separator.separator import Separator
    sep = Separator(output_dir=job.out_dir, output_format=fmt.upper(),
                    model_file_dir=os.path.join(MODELS, "separator"), log_level=30)
    sep.load_model(model_filename=SEPARATE[mode])
    job.lap("load")
    files = sep.separate(src, custom_output_names=None)
    job.lap("infer")
    for f in files:
        base = os.path.basename(f)
        # имя стема идёт в скобках: «track_(Vocals)_model.mp3» → prefix_vocals.mp3
        stem = base.split("_(")[-1].split(")")[0].lower().replace(" ", "_") if "_(" in base else base
        target = job.out(f"{stem}.{fmt}")
        os.replace(os.path.join(job.out_dir, base), target)
    job.stats["mode"] = mode


def master(job):
    import matchering as mg
    target = job.input(0, "трек")
    reference = job.input(1, "референс")
    job.lap("load")
    wav = job.out("master.wav")
    mg.process(target=target, reference=reference, results=[mg.pcm24(wav)])
    job.files.remove(os.path.basename(wav))
    to_mp3(wav, job.out("master.mp3"))
    os.remove(wav)
    job.lap("infer")


def main():
    job = load_job()
    if job.op == "separate":
        separate(job)
    elif job.op == "master":
        master(job)
    else:
        fail(f"операция {job.op} не для этого воркера")
    job.done()


if __name__ == "__main__":
    main()
