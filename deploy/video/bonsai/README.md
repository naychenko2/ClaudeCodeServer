# Bonsai 2 на «Video» — дежурная модель одношаговых мест

Машина «Video» (192.168.7.107, WSL2, 1×RTX 3090) держит ComfyUI (картинки, видео H3, WhisperX)
и LoRA-тренер. Рядом с ними живёт **Bonsai 2 9B** (тернарный Qwen3.5-9B, 2,4 ГБ, llama-server
из форка PrismML) — на неё CCS уводит одношаговые места (заголовки, теги, классификация), чтобы
не грузить прод-движок ходов. Карту не делит: **gpu-arbiter** останавливает Bonsai, как только
карта нужна медиазадаче, и возвращает после тишины.

Замеры 26.10.2026 (9B PQ2_0, сборка cuda-13.3 `prism-b10826`): 218 tok/s генерация,
4377 tok/s промпт, старт до `/health` 1,8 с (веса в page cache), VRAM 2,9 ГБ; уступает за
0,2 с (чужой процесс ловится за ≈1,3 с), возвращается через 5 с тишины + старт. Картинка
Qwen-Image при живом Bonsai: 22 с, пик VRAM 21,8 ГБ, без OOM.

## Раскладка на Video (`/home/an/bonsai`)

```
bin/cuda133/            релиз форка PrismML-Eng/llama.cpp, linux-cuda-13.3 (bin/cuda128 — запас)
cudalibs/cu13/          libcudart/libcublas/libcublasLt .so.13 (копия из venv ComfyUI)
models/                 Ternary-Bonsai-2-9B-PQ2_0.gguf (+ -MTP-Q8_0, + 27B на будущее)
gpu-arbiter.py          арбитр (этот каталог репозитория — источник)
logs/
```

Юниты: `/etc/systemd/system/bonsai.service`, `/etc/systemd/system/gpu-arbiter.service`
(копии здесь). Установка:

```bash
sudo cp bonsai.service gpu-arbiter.service /etc/systemd/system/
cp gpu-arbiter.py /home/an/bonsai/
sudo systemctl daemon-reload && sudo systemctl enable --now bonsai gpu-arbiter
```

Откат: `sudo systemctl disable --now bonsai gpu-arbiter` — ComfyUI и остальное не затронуты.

## Арбитр (порт 18031)

Правила уступки: аренда от CCS (`POST /lease {"ttl_s", "reason"}` → `{"id"}`,
`DELETE /lease/{id}`), чужой процесс на GPU (всё, кроме ComfyUI и llama-server), тяжёлый граф
в очереди ComfyUI (маркеры `h3, minimax, video, fasth3, whisper` в `class_type` и строковых
входах узлов: `MiniMaxH3ImageToVideo`, `H3MultiStream`, `minimax_h3_*`). Возврат — через
`ARBITER_RETURN_QUIET_S` (5 с) без оснований. `GET /ready` — 200, когда Bonsai отвечает
(это и спрашивает CCS перед вызовом), `GET /state` — подробности и счётчики.

Картинки (Qwen-Image, Klein) Bonsai не выгоняют: 9B + KV ≈ 3 ГБ умещаются рядом с кэшем
моделей ComfyUI (17,9 ГБ в простое, пик картинки 21,8 ГБ). Для 27B (≈10 ГБ) так не выйдет —
понадобится `/free` ComfyUI при возврате и выгрузка на каждую картинку (+15 с холодной
загрузки первой картинке).

## CCS

Секция `LocalEngines:bonsai` в `appsettings.Local.json` (см. `docs/architecture/llm-providers.md`,
раздел «Именованные локальные движки»).
