# QA-3 · Финальная приёмка «Ускорение тестов» по HEAD 7e5991e34 (30.10.2026)

Задача `9b786305-d431-4720-b3ba-b89b07e5869c` (роль `qa`) · эпик `2cc8123a` ·
дерево `/home/an/Sources/ClaudeCodeServer-test-speedup`, ветка `feat/test-speedup`, HEAD `7e5991e34`.

**Вердикт: ВОЗВРАТ (major).** c1 и c3 взяты (замеры на тихом фоне), цифры CI подтверждены по
логам. Блокируют приёмку три вещи: предгейт красный на живых данных (неверный порог
сторожа длительности), нестабильный код возврата шарда (−6 при зелёных тестах) и красный
CI-прогон из-за флака `LoopbackResolverTests`, который фича обязана учесть, вводя шарды.

---

## 1. Условия замера

Машина общая, фон постоянный: load1 за день ходил 3,8–80. «Тихого окна» ниже load1 ≈ 4
не бывает долго, но в 04:34–04:41 load1 держался 4–11 (окно ~7 минут) — этого хватило на
ключевые прогоны. Все цифры ниже с указанием load1 до старта.

Калибровка: монолит одним процессом на этом дереве — **218,6 с** против базовых 305,6 с
(база — артефакт 19.10). Монолит сам по себе на ветке быстрее базы на 28 %: R1/R2 разрезали
`SessionManagerTests` на 6 классов, R4 — `ClaudeSessionPromptSections*` на 4, и монолит получил
больше параллелизма внутри одного процесса.

## 2. Таблица критериев

| Критерий | Цифра | Источник | Взят? |
|---|---|---|---|
| **c1** монолит шардами N=3, потолок 150 с | 122,9 с (load1 5,07) и 132,3 с (load1 11,43) — в тихом окне; 207,8 с (load1 42,2) и 218,3 с (load1 43,5) — под нагрузкой | `scripts/test-shards.py --run backend/ClaudeHomeServer.Tests -n 3 --timings r5/run1..3`, стена из вывода скрипта | **да** (в тихом окне; под нагрузкой 207–218 с, как у QA-2) |
| **c2** CI: Backend от старта до конца последнего шарда | 6:52 (build 00:37:40 → monolith 5/6 завершён 00:44:32) | тайминги джобов прогона 40980861541 (`gh run view --json jobs`) | **да**, цифра совпала |
| **c2** CI: с профилем | 7:02 (job `Backend (profile)` 00:44:34→00:44:42) | там же | **да** |
| **c2** CI: весь прогон | 7:06 (created 00:37:37 → updated 00:44:43) | `gh api …/runs/40980861541` | **да** |
| **c3** Fast локально (`--run-fast`), пороги Александра: ≤ 60 с при load1 < 12; ≤ 100 с при любой нагрузке; честный порог на тихой машине 75 с | 51,6 с (load1 10,45) и 41,7 с (load1 4,35) — в тихом окне; 44,9 / 39,8 / 81,0 / 90,5 / 93,6 с — под нагрузкой | `scripts/test-shards.py --run-fast backend/ClaudeHomeServer.slnx --split Tests=3,Llm.Tests=2 --filter Category!=Host&…` | **да** (41,7–51,6 с против R5 59–100 с) |
| **c3** серийный класс Fast ≤ 15 с в одиночном прогоне | максимум 12,8 с (`ManagedCliTests.Подменённый_манифест_при_настоящей_подписи_отвергается`), в монолитных шардах — 10,2 с | TRX `c3-run8` | **да** |
| **c4/c5** мутации сторожей | 7 проб, 7 краснели — см. раздел 4 | `scripts/test_test_shards.py`, `scripts/test_test_profile.py`, `dotnet test` | **да** |
| **c4** доки: ссылки живые, `ProjectMapHygieneGuardTests` зелёный | битых ссылок 0 в `docs/operations/testing.md` и `CLAUDE.md`; гвард 2/2 passed | скрипт проверки ссылок, `dotnet test Guards.Tests --filter ProjectMapHygiene` | **да** |
| Стабильность `Llm.Tests` | 2235 passed / 3 skipped — два прогона подряд | `dotnet test backend/ClaudeHomeServer.Llm.Tests` | **да** |
| Стабильность `Team.Tests` | 325 passed — два прогона подряд | `dotnet test backend/ClaudeHomeServer.Team.Tests` | **да** |

## 3. Красное в CI (прогон 40980861541) — разбор

### 3.1 `Frontend (lint + typecheck + build)` — «чужой», но ветку надо перебазировать

Красный шаг — `Lint remote guard (image-editor, audio-editor, video-editor, хосты контекста)`,
12 ошибок `no-restricted-imports` в трёх `gpuCard.tsx` (`imageEditor/thread`,
`audioEditor/thread`, `videoEditor/feed`): прямые импорты `../../../components/LocalGpuQueueLine`
и `../../../lib/cloudRescue` вместо `aihome_shell/kit` (ADR-018 §10.3, ADR-021 §3, ADR-022).

Доказательство «чужой»:
- фича фронт не трогала: `git diff master..HEAD -- frontend/src/features/` — пусто;
- те же файлы и то же правило есть в merge-base `f4150f445`;
- в `origin/master` (`e5522a2a5`) этих `gpuCard.tsx` уже нет — master их переписал на kit,
  и тот же шаг там зелёный (прогон 40976142984, шаг прошёл за 4,3 с);
- общий `npm run lint` — non-blocking, красные там — легаси-долг одинаково в обеих ветках.

Причина расхождения: ветка отстала от master на **129 коммитов** (`git rev-list --count HEAD..origin/master`).
Фича не виновата, но «зелёный CI на ветке» недостижим без перебазирования.

### 3.2 `LoopbackResolverTests` — флак, не внесён фичей; но фича его подсветила

Падение: `При_двух_живых_семьях_выбирается_IPv4` — ждал `http://127.0.0.1:35401`, получил
`http://[::1]:35401` (джоб `Backend (test monolith 1/6)`, шаг `Test (Fast)`, 1 красный из 1127).

Доказательства «не дефект раскладки, а флак, существовавший до фичи»:
1. `git diff master..HEAD -- backend/ClaudeHomeServer.Tests/Services/LoopbackResolverTests.cs backend/ClaudeHomeServer.ProjectServices/…/LoopbackResolver.cs` — **пусто**. Фича эти файлы не меняла.
2. Изолированно класс зелёный: 10 прогонов `dotnet test --filter LoopbackResolverTests` подряд — 5/5 зелёные; 3 прогона `dotnet test` монолита Fast-фильтром одним процессом — зелёные.
3. В параллельном наборе (`--run-fast`, 6 процессов) — красный 1 раз из 8 прогонов: `c3-run6`, шард `Tests-3`, симптом **зеркальный** — `Резолвер_находит_порт_по_IPv6_когда_IPv4_молчит` ждал `http://[::1]:33175`, получил `http://127.0.0.1:33175`. Зеркальность симптома — признак гонки за семью/порт, а не детерминированной ошибки.
4. На master-CI (прогон 40047186274, монолит одним процессом, тот же фильтр) `Loopback` не падал: 1 красный — `AudioEditorControllerTests`, другой тест.
5. На ветке падало в разных шардах: `1/6` в прогоне 30.10, `4/6` в прогоне 29.10 — то есть не «шард такой-то», а набор соседей по процессу.

Механизм (гипотеза, подтверждена частично): `LoopbackResolver` держит **статический кэш выбора семьи по номеру порта** с TTL 10 с (`Cache` в `LoopbackResolver.cs`), а порты у тестов эфемерные (`TcpListener(…, 0)`). Тест `Кэш_семьи_не_подменяет_пробу_живости` оставляет запись в кэше **без `Invalidate` в `finally`** (единственный из пяти, кто не снимает), и следующий тест того же шарда, взяв тот же номер порта, получает оттуда чужую семью. Проба (добавить `Invalidate(port)` в конец этого теста, 4 прогона шардами) — Loopback не краснеет ни разу.

6. **Проба с исправлением (6 прогонов шардами, откат после):** добавил `LoopbackResolver.Invalidate(port)` в конец теста `Кэш_семьи_не_подменяет_пробу_живости` (единственный, кто не снимает кэш в `finally`) — Loopback не краснеет ни разу за 6 прогонов (красными в 2 из 6 были другие тесты: `VideoAssembleIsolationTests` и `SessionManagerWorkLoopTests.ContinueWorkLoop_…`). Проба снята, дерево чистое.

Итог по дефекту: он существующий (дефекты `f8b03f9f`, `abf36937`), не внесён фичей, но фича
ввела шардирование и параллельный набор — то есть сделала его заметным и убрала «запас»
на зелёный прогон. Закрыть его нужно до мержа: без этого CI фичи красный.

### 3.3 Прочие красные прогоны ветки (для контекста)

`TaskExecutionServiceJoinTests.ReviewTree_ТерминальнаяОстановкаИсполнителя_ОсвобождаетСлот`
(прогоны 29.10) и `DeviceAgentApiContractTests.*` (прогон 29.10) — карточка `47caa27f`,
закрыта как чужой флак.

## 4. c4/c5 — пробы мутации сторожей

Каждая проба: правка → прогон → краснеет? → **откат дерева** (`git diff` пуст после каждой).

| № | Что мутировали | Ожидание | Факт |
|---|---|---|---|
| 1 | `scripts/test-shards.py verify()` — самопроверка покрытия всегда пуста | краснеет | **краснеет** (4 теста: `test_удалённый_класс_из_раскладки_ловится`, `test_класс_в_двух_шардах_ловится`, +2 CLI) |
| 2 | `TestShards.Verify` (C#) — самопроверка всегда пуста | краснеет | **краснеет** (2 теста `TestShardsTests`) |
| 3 | `wait_all()` — код возврата всегда 0 | краснеет | **краснеет** (2 теста `WaitAllTests`) |
| 3b | `wait_all()` — `max(failed)` без фильтра `c > 0` | краснеет | **не краснеет** — тест проверяет лишь «ненулевой код», а −9 и есть ненулевой; разница наблюдаема только на уровне значения. Покрытие слабое, но контракт «ненулевой код» соблюдён |
| 4 | выбор `PARALLEL` по режиму (локальные режимы не переключают) | краснеет | **краснеет** (`test_cli_PARALLEL_локальные_режимы_берут_LOCAL_PARALLEL…`) |
| 5 | предупреждение о пропавших `--timings` убрано | краснеет | **краснеет** (`test_load_profile_пропавший_профиль_даёт_предупреждение…`) |
| 6 | Host-сторож: снята `[Trait("Category","Host")]` с `StubModuleEndpointTests` | краснеет | **краснеет** (`ХостовыйКласс_ПомеченHost` — называет класс) |
| 7 | коллекции xunit: снято `[CollectionDefinition(Inotify)]` в `TestCollectionDefinitions.cs` | краснеет | **краснеет** (`ОпределенияКоллекцийЛокальныДляЭтойСборки`) |
| 8 | сторож длительности: `slow_offenders()` всегда пуст | краснеет | **краснеет** (3 теста `test_test_profile.py`) |

Итог: 7 из 8 проб краснеют, одна (3b) — слепое пятно в формулировке проверки, не в контракте.

## 5. Блокирующие находки

### 5.1 (major) Предгейт красный на живых данных: порог сторожа длительности

`backend/ClaudeHomeServer/appsettings.json`, `Pregate.Checks`:

```
"быстрые тесты":       … --run-fast … --results .cc-attachments/pregate-fast
"сторож длительности": python3 -I scripts/test-profile.py .cc-attachments/pregate-fast --check --latest-run
```

`--threshold` не задан → берётся дефолт `CHECK_THRESHOLD = 5.0` (`scripts/test-profile.py:45`).
В CI тот же сторож идёт с `DURATION_GUARD_THRESHOLD: "30"` (`.github/workflows/ci.yml:191`).

Факт на живых данных (прогон `pregate-sim`, 15840 тестов Fast):

```
Сторож длительности: 8 тестов дольше 5,0 с без метки Slow (TRX набора Fast).
  21,64 с  ClaudeHomeServer.DeviceAgent.Tests.Cli.ManagedCliTests.Подменённый_манифест_при_настоящей_подписи_отвергается
  18,69 с  ClaudeHomeServer.DeviceAgent.Tests.Cli.CliManifestVerifierTests.Ключ_без_ожидаемого_отпечатка_не_принимается
   7,51 с  ClaudeHomeServer.Tests.Services.ClaudeSessionDiedEmptyRetryTests.DiedEmptyРетрай_СтартуетНовыйПроцессСЖивымMcpКонфигом
   7,06 с  ClaudeHomeServer.Tests.Services.FfmpegVideoDspTests.Без_музыки_и_встык_склейкой_длины_складываются
   6,22 с  ClaudeHomeServer.DeviceAgent.Tests.Supervision.SupervisorHandoffProcessTests.Linux_супервизор_заменяет_себя_новой_версией_на_месте
   5,36 с  ClaudeHomeServer.Tests.Services.CSharpGraphProviderTests.Build_FiltersOutBclTypes
   5,23 с  ClaudeHomeServer.Tasks.Tests.FeatureTimelineJournalTests.Build_Замер_20kЗаписей_Быстрый
   5,03 с  ClaudeHomeServer.Tasks.Tests.FeatureJournalStoreTests.Cap_Exceeds_MaxLines_TurnsSkipped_TransitionsWritten_Truncated
```

Воспроизведение (команды предгейта как есть):

```bash
python3 -I scripts/test-shards.py --run-fast backend/ClaudeHomeServer.slnx \
  --split backend/ClaudeHomeServer.Tests=3,backend/ClaudeHomeServer.Llm.Tests=2 \
  --filter "Category!=Host&Category!=Slow&Category!=Integration&Category!=Node&Category!=Bench" \
  --results .cc-attachments/pregate-sim
python3 -I scripts/test-profile.py .cc-attachments/pregate-sim --check --latest-run   # exit 1
```

Два из восьми — крипто-проверки подписи (12–22 с), они долгие по существу, а не от нагрузки:
на CI эти тесты идут в Fast и проходят сторож с порогом 30. Остальные шесть — по 5–7 с.
Ни один из восьми не помечен `Slow` и в master, и в ветке (`git show origin/master:<файл>` — меток
нет), так что это не долг фичи: фича ввела **проверку с неверным порогом**, и предгейт шага
красный всегда, а не под нагрузкой.

### 5.2 (minor→major по последствиям) Шард возвращает −6 при полностью зелёных тестах

Прогон `c3-run5`: `Стена шардов: 29,6 с; коды выхода: [-6, 0, 0, 0, 0, 0]`. При этом в логе
шарда `fast-ClaudeHomeServer.Tests-1.log` — `Passed! - Failed: 0, Passed: 1675, Skipped: 6`,
то есть все тесты зелёные, а процесс умер с SIGABRT уже после отчёта. Скрипт честно отдаёт
ненулевой код (`wait_all`: `max(c for c in failed if c > 0, default=1)` → 1), и предгейт
красный на пустом месте.

Частота: 1 из 8 прогонов `--run-fast` (остальные семь — 0). В CI такого не было.
Детали (почему abort) в логе нет: `stderr` шарда обрывается на строке `Passed!`.

### 5.3 (major, унаследованный) Красный CI из-за `LoopbackResolverTests`

См. раздел 3.2. Фича не виновата в самом флаке, но ввод шардов и параллельного набора делает
его вероятным и убирает зелёный прогон CI — закрывать нужно до мержа (карточки `f8b03f9f`, `abf36937`).

## 6. Доки

- `docs/operations/testing.md` сверена с кодом: `LOCAL_PARALLEL = 3` для `--run`/`--run-fast` и `PARALLEL = 5` для CI (`scripts/test-shards.py:86-90`), `--timings` — медиана по профилям (`load_profile`), режим `--run-fast` и его разбор описаны, `ClaudeSessionPromptSections*` вместо старого `ClaudeSessionPromptSectionsOrderTests` — и в коде (`OrderTests/TailTests/HintsTests/LocalMediaTests` + база), и в тексте дока; `SessionManagerTests` упоминается как «бывший, разрезан по темам» — класса с таким именем в коде уже нет. Расхождений нет.
- `CLAUDE.md` — выжимка про наборы и шарды соответствует коду (`TestRuns:MonolithShards`), детали вынесены в `testing.md` (ссылка живая, тон файла соблюдён).
- Ссылки: 0 битых в `docs/operations/testing.md` и `CLAUDE.md`.
- `ProjectMapHygieneGuardTests` — 2/2 passed.

## 7. Стабильность

| Набор | Прогон 1 | Прогон 2 | Красные |
|---|---|---|---|
| `ClaudeHomeServer.Llm.Tests` | 2235 passed, 3 skipped (47,3 с) | 2235 passed, 3 skipped (44,6 с) | нет |
| `ClaudeHomeServer.Team.Tests` | 325 passed (13,2 с) | 325 passed (12,9 с) | нет |
| монолит шардами N=3 (`c1-run3/4`, тихий фон) | 122,9 с, коды [0,0,0] | 132,3 с, коды [0,0,0] | нет |

Красное в прогонах под нагрузкой — `VideoAssembleIsolationTests.Сборка_фильма_идёт_в_scope_slice_под_занятым_слотом`
(слот `BuildConcurrencyGate` занят — наш, на ветке метку `Slow` поставили, но в прогоне монолита
без фильтра он всё равно идёт) и `NpmBuildServiceTests.Разбор_ЭтапыАтакующейФормы_ЛинейныйИБыстрый`
(порог `watch.Elapsed < 100ms`, 103–147 мс) — последний легаси-флак по часам, в `testing.md` описан.

## 8. Известные пределы (не дефекты, подтверждено)

`TeamWave+ChatArchive ≈ 15 с` вместо 13; баланс шардов ×1,2 по job / ×1,44 по чистому времени;
кэш NuGet −6 с на шард; W4 7–7,9 с; `actionlint` SC2016 (info) на `ci.yml`.

## 9. Что не проверено

- Ни один замер c1/c3 не выполнен при load1 < 4 (нижняя граница фона хоста) — при load1 4–11 взяты.
- Приёмка на Windows/macOS: шардирование на других ОС не проверялось (CI — Linux).
- `run_tests` с `TestRuns:MonolithShards` (серверная раскладка по числу тестов, без профиля) не гонял: в этом чате нет серверного стенда; проверял только скриптовые режимы `--run`/`--run-fast`.
- `actionlint` и `yamllint` локально не гонял (только CI-лог прогона).
- Точный механизм флака `LoopbackResolver` не доведён до конца: связь «кэш без `Invalidate` + переиспользование эфемерного порта» подтверждена пробой (4/4 зелёные), но точная привязка к CI-среде не установлена.
