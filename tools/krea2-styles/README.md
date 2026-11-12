# krea2-styles — замер стилей Krea 2 Turbo

Раннер строит картинки **кодом CCS**: зовёт `LocalImageMediaAdapter` (шов `ILocalImageMedia`) тем же запросом, что
собирает редактор (`Model=krea2-turbo`, `KreaStyle` или `StyleReference`). Девять стилей × 2 промпта и стиль по образцу
× 3 образца (образцы — результаты трёх стилей). В решение не входит.

```bash
# туннель к ComfyUI на Video (он слушает loopback внутри WSL)
ssh -f -N -L 18188:127.0.0.1:8188 gpuhost
cd tools/krea2-styles/Krea2StylesBench && dotnet build -c Release
dotnet bin/Release/net10.0/Krea2StylesBench.dll --out ~/krea2-styles-run --phase styles      # потом --phase reference
```

Каждая задача ставится только в пустую очередь ComfyUI (видеокарту делят другие шаги и живые пользователи);
службу раннер не перезапускает. Результат — `results.jsonl` (время исполнения по истории ComfyUI) и `img/`.
Итоги Ш4 — проект «Аудиотесты», `krea2-styles/`.
