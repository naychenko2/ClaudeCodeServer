"""Предзагрузка весов аудиомоделей: воркеры работают с HF_HUB_OFFLINE=1, чтобы скачивание
не держало общую очередь GPU. Запускается интерпретатором того venv, чьи веса грузит.

warm_models.py <семейство> [каталог весов]
"""
import os
import sys

from huggingface_hub import hf_hub_download, snapshot_download

family = sys.argv[1]
models = sys.argv[2] if len(sys.argv) > 2 else os.path.expanduser("~/ai-data/audio-models")


def local(repo, patterns=None):
    # Веса, которые воркер читает по пути, а не из HF-кэша
    path = snapshot_download(repo, local_dir=os.path.join(models, repo), allow_patterns=patterns)
    print(path, flush=True)


if family == "seedvc":
    # v2 (речь) и v1 с F0 (пение) — те же файлы, что тянут inference.py/inference_v2.py
    for repo, f in [("Plachta/Seed-VC", "v2/cfm_small.pth"), ("Plachta/Seed-VC", "v2/ar_base.pth"),
                    ("Plachta/ASTRAL-quantization", "bsq32/bsq32_light.pth"),
                    ("Plachta/ASTRAL-quantization", "bsq2048/bsq2048_light.pth"),
                    ("funasr/campplus", "campplus_cn_common.bin"), ("lj1995/VoiceConversionWebUI", "rmvpe.pt"),
                    ("Plachta/Seed-VC", "DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth"),
                    ("Plachta/Seed-VC", "config_dit_mel_seed_uvit_whisper_base_f0_44k.yml")]:
        print(hf_hub_download(repo, f), flush=True)
    for repo in ["openai/whisper-small", "facebook/hubert-large-ll60k"]:
        print(snapshot_download(repo, allow_patterns=["*.json", "*.safetensors", "*.txt", "*.model"]), flush=True)
    # у BigVGAN в репозитории ещё дискриминаторы на гигабайты — инференсу нужен только генератор
    for repo in ["nvidia/bigvgan_v2_22khz_80band_256x", "nvidia/bigvgan_v2_44khz_128band_512x"]:
        for f in ["bigvgan_generator.pt", "config.json"]:
            print(hf_hub_download(repo, f), flush=True)
elif family == "audiosr":
    for n in ["basic", "speech"]:
        print(hf_hub_download(f"haoheliu/audiosr_{n}", "pytorch_model.bin"), flush=True)
    # текстовая часть CLAP внутри AudioSR
    print(snapshot_download("roberta-base", allow_patterns=["*.json", "*.txt", "model.safetensors"]), flush=True)
elif family == "whisper":
    print(snapshot_download("mobiuslabsgmbh/faster-whisper-large-v3-turbo"), flush=True)
elif family == "qwen3tts":
    for repo in ["Qwen/Qwen3-TTS-Tokenizer-12Hz", "Qwen/Qwen3-TTS-12Hz-1.7B-Base", "Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign",
                 "Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"]:
        local(repo)
    print(snapshot_download("mobiuslabsgmbh/faster-whisper-large-v3-turbo"), flush=True)
elif family == "chatterbox":
    # только файлы мультиязычной модели (from_local), а не весь репозиторий
    local("ResembleAI/chatterbox", ["ve.pt", "t3_mtl23ls_v2.safetensors", "s3gen.pt", "conds.pt",
                                    "grapheme_mtl_merged_expanded_v1.json", "Cangjie5_TC.json", "mtl_tokenizer.json"])
elif family == "separator":
    from audio_separator.separator import Separator
    sep = Separator(model_file_dir=os.path.join(models, "separator"))
    for m in ["model_bs_roformer_ep_317_sdr_12.9755.ckpt", "htdemucs_ft.yaml", "htdemucs_6s.yaml",
              "mel_band_roformer_karaoke_becruily.ckpt"]:
        sep.download_model_and_data(m)
        print(m, flush=True)
elif family == "acestep":
    local("ACE-Step/Ace-Step1.5")
    local("ACE-Step/acestep-v15-xl-base")
    # раскладка, которую ждёт ACE-Step: $ACESTEP_CHECKPOINTS_DIR/<модель>
    ckpt = os.path.join(models, "acestep-checkpoints")
    os.makedirs(ckpt, exist_ok=True)
    base = os.path.join(models, "ACE-Step", "Ace-Step1.5")
    links = {d: os.path.join(base, d) for d in os.listdir(base) if os.path.isdir(os.path.join(base, d))
             and not d.startswith(".")}
    links["acestep-v15-xl-base"] = os.path.join(models, "ACE-Step", "acestep-v15-xl-base")
    for name, target in links.items():
        link = os.path.join(ckpt, name)
        if not os.path.lexists(link):
            os.symlink(target, link)
    print(ckpt, flush=True)
else:
    raise SystemExit(f"неизвестное семейство {family}")
