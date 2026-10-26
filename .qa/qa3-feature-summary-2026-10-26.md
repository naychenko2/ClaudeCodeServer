# QA3 — перепроверка голоса оркестратора на фичах без идущих задач (после B14)

Дата: 26.10.2026. Ветка: `feat/feature-summary-state`, HEAD `058a4fb79` (исправление B14).
Дерево: `/home/an/Sources/ClaudeCodeServer-feature-summary` (worktree чата).
Предыдущие приёмки: [.qa/qa1-feature-summary-2026-10-26.md](qa1-feature-summary-2026-10-26.md),
[.qa/qa2-feature-summary-2026-10-26.md](qa2-feature-summary-2026-10-26.md).

## Стенд и фикстуры

- Бэкенд из worktree: `mcp__dev__build` (42/42 проекта, 0 ошибок) → `systemd-run --user
  ccs-qa-fs3-backend.service`, `http://127.0.0.1:5563`, `ASPNETCORE_ENVIRONMENT=Development`,
  `WorkingDirectory=backend/ClaudeHomeServer`.
- Конфиг стенда: `backend/ClaudeHomeServer/appsettings.Local.json` (в .gitignore, удалён после
  приёмки) — свой `DataPath` (`/home/an/qa-fs3/data/projects.json`), `Auth:DevPassword=12345`,
  `Execution:Isolation.Enabled=false`, `McpTasksApiUrl`, `LlmProviders:minimax` (ключ из
  `$ANTHROPIC_AUTH_TOKEN`).
- Фронт: дев-витэ `ccs-qa-fs3-vite.service`, `BACKEND_PORT=5563`, `http://127.0.0.1:5193`.
- Вход: `admin` / `12345`. Фиче включён флаг `features-overview`. Слоты моделей владельца:
  `PUT /api/me/model-tiers` = `MiniMax-M3.1-Flash[1m]`.
- Проект стенда: `621b25f5-8dc0-4c2a-9f62-2276a88975e8` («QA3 голос фичи»), корень
  `/home/an/qa-fs3/proj` (вне дерева волны).
- Чаты: оркестратор `e5e86b76-1c8e-4de9-9d5f-27b16c82358e`; сессия исполнителя
  `d5a685bc-969b-444f-a9a2-be1caafd66aa` (создана боевым запуском).
- Фикстуры (id — в [.qa/qa3-fixture-ids.json](qa3-fixture-ids.json)): `main` (шаг в prod),
  `waiting` (отметка ожидания человека), `stopped` (живой исполнитель + кнопка «Стоп»),
  `paused` (удержание manual), `queued` (удержание queue), `stage` (задача разреза + шаг — две
  стадии), `running` (живой исполнитель для строки «Сейчас»).
- Скрипты: [.qa/qa3-fixture.py](qa3-fixture.py) (фикстуры), [.qa/qa3-voice.py](qa3-voice.py)
  (голос через MCP и REST + чтение сводки), [.qa/qa3-live.py](qa3-live.py) (живой исполнитель,
  «Стоп», отказ исполнителю), [.qa/qa3-brief.py](qa3-brief.py) (снимок сводок).

**Про фикстуры и честность замеров.** Связь «оркестратор» (право писать голос) у фикстур
проставлена тем же полем, которое пишет живой запуск исполнителя
(`ExecutorLaunchedBySessionId` эпика = чат-оркестратор) — `PUT /api/tasks/{id}`. Для фичи
`stopped` связь получена ЖИВЫМ запуском (`POST /api/tasks/{id}/execute` с
`X-Caller-Session-Id`), и на ней же проверены живой «Стоп» и отказ исполнителю. Фича `running`
и `main` — живой исполнитель minimax (`claudeStartedAt` → состояние «идёт» → шаг выполнен).

## 1. Голос на фичах без идущих задач — stale=false, метки нет (кроме очереди)

Голос пишет оркестратор через MCP-over-HTTP `POST /mcp/tasks/{sessionId}` (JSON-RPC
`tools/call`, `tasks_update`, `featureNote`) — тот же путь, что у CLI; один раз человек через
`PUT /api/tasks/features/{id}/note`. Ответ — `GET /api/tasks/features/{id}` → `brief.note`;
метка — из живого DOM (`[data-testid="brief-voice-stale"]`).

| фича | state | brief.kind | как записан | stageAtWrite | stale сразу | метка в карточке |
|---|---|---|---|---|---|---|
| `waiting` | waiting | waiting_you | MCP | `prod` | **false** | нет |
| `stopped` | stopped | error | MCP (живой оркестратор) | `prod` | **false** | нет |
| `main` (не начата) | not_started | idle | MCP | `prod` | **false** | нет |
| `main` (после выполненного шага) | idle | idle | MCP | `review` | **false** | нет |
| `paused` | paused | paused | MCP | `prod` | **false** | нет |
| `queued` | paused | queued | MCP | `prod` | **true** | **«до смены этапа»** |
| `waiting` | waiting | waiting_you | REST PUT (человек) | `prod` | **false** | нет |

- Штамп стадии непустой на всех состояниях — активная стадия конвейера (`prod`/`review`/`cut`),
  а не пустая строка, как было до B14.
- На `waiting`/`stopped`/`paused`/`idle` метки «до смены этапа»/«до паузы» в карточке нет вовсе
  (`brief-voice-stale` отсутствует, `staleLabelsOnPage=0`).
- Живой DOM, `main` после снятия паузы: `Ничего не идёт` + подпись голоса
  `ведёт фичу · 12 мин назад`, без метки.

**Про состояние «остановлена»:** получено боевым путём — живой исполнитель, кнопка «Стоп»
(`POST /api/board/agents/{sessionId}/interrupt`): `executorStopReason=interrupted_by_user`,
`executorStoppedAt` позже `claudeStartedAt`, `state=stopped`.

## 2. Устаревание по делу — метки появляются

| случай | что сделано | результат |
|---|---|---|
| смена стадии | фича `stage`: голос записан на стадии `cut` → задача разреза закрыта → активная стадия `prod` | `stale=true`, в карточке **«до смены этапа»**. Категория не менялась (`idle` → `idle`) — устарело только по стадии |
| пауза | фича `main` (`idle`, свежий голос) → `POST features/{id}/hold {reason:"manual"}` | `kind=paused`, `stale=true`, в карточке **«до паузы»** |
| снятие паузы | та же фича → `DELETE features/{id}/hold` | тот же голос снова свежий (`stale=false`) — устаревание обратимо и привязано к делу |
| отказ исполнителю | MCP `tasks_update` с `featureNote` ИЗ СЕССИИ ИСПОЛНИТЕЛЯ | `isError=true`, текст `Голос фичи пишет оркестратор или человек`; голос эпика не перезаписан (остался текст оркестратора) |

## 3. Регресс строки «Сейчас» — совпадает с QA1/QA2

Строки сняты из живого DOM (`[data-testid="brief-now-line"]`, `brief-now-sub`).

| kind | строка «Сейчас» | подстрока | сверено с |
|---|---|---|---|
| running | `Производство шага S7 (бэк) — у Ассистента` | `с 16:10 · 1 мин` | QA1: `Производство шага S3 (фронт) — у Ассистента`, `с 12:23` |
| waiting_you | `Ждёт вашего ответа: нужен выбор направления` | `ждёт 18 мин · без вас фича стоит` | QA1 (merge_pending): `Ждёт вашего согласия на мерж в master`, `ждёт 14 мин · без вас фича стоит` — формула та же, reason другой |
| error | `Исполнитель встал: Исполнение прервано` | — | QA2 §4 — дословно так же |
| paused | `На паузе — поставили вы` | — | QA1 — дословно так же |
| queued | `Ждёт своей очереди` | — | QA1 — дословно так же |
| idle | `Ничего не идёт` | — | QA1 — дословно так же |

- B14 тронул `NowOf` только по коду-моушену: `ActiveStageOf(run)` — то же самое выражение
  (`run.Stages.FirstOrDefault(s => s.State == "active")?.Key ?? ""`), что было инлайн в ветке
  тревоги. Значение `brief.now` не изменилось ни на одном состоянии — это видно и по коду, и по
  живому DOM.
- «Дальше» у идущей фичи: `После производства — ревью, затем проверка и финальное ревью» —
  родительный падеж из QA2 на месте.

## 4. ui_probe (360 и 1280, светлая тема) — чисто

`mcp__tests__ui_probe`, селектор `[data-testid="feature-brief-block"]`, маршрут
`/#/project/{pid}/feature/{fid}` в `path`, авторизация `admin/12345`, фича `stopped` (голос
оркестратора + строка «Исполнитель встал»).

| ширина | smallTargets | horizontalOverflow | clippedText | unnamedInteractive |
|---|---|---|---|---|
| 360 | 0 | 0 | 0 | 0 |
| 1280 | 1 | 0 | 0 | 0 |

- Единственный `smallTarget` на 1280 — линк «Статистика ›» (80,5×24 px, `Button size="xs"` из
  UI-кита; на мобиле у него `minHeight: 40`). Тот же элемент отмечен в QA1 и QA2 — не регресс.
- `consoleErrors`/`failedRequests` завышены (502 модуль-федерации `*-remote/remoteEntry.js` на
  дев-витэ, `ERR_ABORTED` при навигации) — артефакт стенда, не дефект (по счётчикам смотрим
  только smallTargets/horizontalOverflow/clippedText/unnamedInteractive).

## Тесты

- `dotnet test` по `backend/ClaudeHomeServer.Tasks.Tests`: **753 из 753 пройдено** (15,7 с).
- `dotnet test` по `backend/ClaudeHomeServer.Tests`: **6805 из 6832 пройдено**, 26 пропущено,
  **1 упавший** — `LocalModelStatsHistoryTests.НеАдмин_ЧужойId_404`. Падение не связано с B14:
  в логе прогона `System.IO.FileNotFoundException: Could not find file '/tmp/x.png'` — тест ждёт
  несуществующий файл фикстуры (`MediaHistoryResolution(1, "/tmp/x.png", …)`), и код B14
  (проектор сводки, REST-ручка голоса, MCP-тулсет) этих мест не касается. Проверено: после
  создания `/tmp/x.png` тест проходит (1 из 1) — то есть это предсуществующая средовая
  зависимость теста, а не регресс.

### Мутационные пробы: тесты B14 действительно держат исправление

Правились во временную сторону по одному файлу, гонялись тесты, затем правка откатывалась
(`git checkout -- <файл>`; в конце `git status --porcelain` — чисто).

| мутация | краснеет | зеленеет (контроль) |
|---|---|---|
| REST-штамп обратно на `run.Now.FirstOrDefault()?.Stage ?? ""` (`TasksController.cs:1029`) | `TasksControllerTests.PutNote_BezIdushchihZadach_ShtampAktivnayaStadiya_GolosNeUstarevshi` (`StageAtWrite` `""` ≠ `"prod"`) | MCP-тест `FeatureNote_БезИдущихЗадачей_ГолосНеУстаревший` — зелёный (другая ветка) |
| MCP-штамп обратно на `run?.Now.FirstOrDefault()?.Stage ?? ""` (`TasksToolset.cs:483`) | `TasksToolsetFeatureNoteTests.FeatureNote_БезИдущихЗадачей_ГолосНеУстаревший` | REST-тест — зелёный |
| `StaleOf` сверяет с `run.Now[0]` вместо `ActiveStageOf(run)` (`FeatureBrief.cs:274`) | `FeatureBriefTests.Note_ГолосНаСостоянииБезИдущихЗадачейНеСразуУстаревший` — все 4 состояния (waiting/stopped/idle/not_started) + `Note_БезСменStaleЛожный_ПоляРовноИзХранимогоГолоса` | после отката — 43 из 43 в `FeatureBriefTests` |

Вывод: исправление B14 не «зелёное на словах» — каждый из трёх путей (REST-штамп, MCP-штамп,
сравнение при устаревании) держится своим тестом.

## Находки

1. **Голос на фиче в очереди сразу помечается устаревшим** (`kind=queued`): после записи голоса
   `brief.note.stale=true`, в карточке метка «до смены этапа», хотя ничего не менялось.
   Причина — сравнение категорий: `StateAtWrite` хранит СОСТОЯНИЕ фичи (`paused`), а текущая
   категория для очереди — `queued` (`FeatureBrief.KindOf`: `paused` + `Hold.Reason=queue`), и
   `KindByState["paused"]="paused"` ≠ `"queued"` → голос «устарел» (fail-closed). Ограничение
   задокументировано в коде (`FeatureBrief.cs`, комментарий к `StaleOf`: «Ограничение хранимого
   поля (B3): причина удержания на момент записи не хранится … paused → queued различить нечем»),
   и это НЕ регресс B14: до правки штамп был `""` ≠ `"prod"` → голос был устаревшим тоже.
   Критерий c1 задачи в части `queued` не выполнен — заведена отдельная карточка дефекта.
   B14 (единая база штампа) это не чинит: нужен либо другой состав хранимого поля, либо решение
   «queued не считается устареванием».

2. **`LocalModelStatsHistoryTests.НеАдмин_ЧужойId_404` падает без файла `/tmp/x.png`**
   (500 `FileNotFoundException` вместо 200). К B14 отношения не имеет: тест и код медиаистории
   не менялись, причина — несуществующий файл фикстуры (при его создании тест проходит).
   Наблюдение, отдельной карточки не завожу (вне фичи).

3. Счётчики консоли ui_probe завышены — артефакт дев-витэ (502 модуль-федерации), как и в QA1/QA2.

## Не проверялось

- Тёмная тема (по постановке «светлая достаточно»); ui_probe её не умеет.
- Фича, у которой голос пишет ЧЕЛОВЕК, у которой сразу после записи меняется стадия, — тот же
  путь `StaleOf`, но комбинация «голос человека + смена стадии» на стенде не ставилась (голос
  человека проверялся на `waiting`).
- `merged`/`merging`/`closed`/`draft` — в список пунктов этой приёмки не входят (проверены в QA1).
- Оценка «≈ N осталось» — не входит в список пунктов.
- Нагрузка (параллельные голоса на одну фичу) — не входит в список пунктов.
