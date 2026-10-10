# ADR-026: контракты обучения LoRA персонажа

Приложение к [ADR-026](ADR-026-character-lora-training.md). Здесь всё, что нужно исполнителю шага без чтения соседних шагов: имена файлов, сигнатуры, поля JSON, регэкспы, коды и примеры. JSON — camelCase, `null`-поля опущены, enum'ы строками. Расхождение кода с этим файлом — дефект кода либо правка этого файла с пометкой в ревью.

Обозначения:
- `ownerKey` — `ownerId` без дефисов, нижний регистр, 32 hex;
- `projectKey` — то же для `projectId`;
- `runId` — 12 hex.

## 1. Регэкспы и константы

| Что | Значение | Где живёт |
|---|---|---|
| `ownerKey`, `projectKey` | `^[0-9a-f]{32}$` | `LoraNamespace.OwnerKey(string id)` (Core) — из Guid-строки; не Guid → `ArgumentException` |
| `slug` | `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\z` | уже есть: `CharacterStore.IsValidSlug` |
| `runId` | `^[0-9a-f]{12}\z` | `LoraNamespace.IsValidRunId`; генерирует сервер: `Guid.NewGuid().ToString("N")[..12]` |
| триггер обученной версии | `^[a-z][a-z0-9_]{2,23}\z` | `LoraTrigger.IsValid` (Core, рядом с `LoraTrainPresets`) |
| имя обучающего фото | `^[0-9a-f]{16}\.(jpg\|png\|webp)\z` | `CharacterTrainingPhotos.IsValidName` |
| пресет | `standard` (срез 1 — единственный) | `LoraTrainPresets` (Core) |
| `title` версии | после `Trim()`: пусто → `null` (шаблон); 1–60 символов; > 60 → `400 bad_title` | ImageEditor |
| фото для обучения | мин. 20, макс. 50, ≤ 8 МБ, короткая сторона ≥ 512 px, `jpg/png/webp` по сигнатуре | `CharacterTrainingPhotos` |
| версий на персонажа | 5 (`ready` + `training`) | `CharacterLoraRuns.MaxVersions` |
| годных лиц у воркера | мин. 15, иначе `too_few_faces` | `worker_lora.py` |

Автотриггер: `"ccs" + slug.Replace("-", "")`, обрезка до 24 символов. Результат обязан пройти регэксп триггера; не прошёл — `"ccs" + runId[..8]`.

## 2. `LoraNamespace` (Core) — пути и видимость

Файл: `backend/ClaudeHomeServer.Core/Services/ImageEditor/LoraNamespace.cs`. `VisibleTo` и `PrefixFor` вводит шаг 1а, остальное добавляет шаг 5а. Проверки префикса за пределами этого класса запрещены.

```csharp
namespace ClaudeHomeServer.Services.ImageEditor;

public static class LoraNamespace
{
    public static string OwnerKey(string id);                       // "a300cc8f0074…"; не Guid → ArgumentException
    public static string PrefixFor(string ownerId);                 // "ccs/<ownerKey>/"
    public static bool VisibleTo(string ownerId, string? loraName);  // шаг 1а
    public static bool IsValidRunId(string? runId);
    // Каталог прогона в models/loras, со слэшем в конце: "ccs/<ownerKey>/<slug>-<runId>/"
    public static string RunDirectory(string ownerId, string slug, string runId);
    // Итоговое имя весов, как его отдаёт GET /models/loras: "ccs/<ownerKey>/<slug>-<runId>/<slug>.safetensors"
    public static string RunLoraFile(string ownerId, string slug, string runId);
    // Подпапка input ComfyUI для фото прогона, без слэша в конце: "ccs/<ownerKey>/<runId>"
    public static string RunInputFolder(string ownerId, string runId);
}
```

Невалидный `slug` или `runId` в `Run*` → `ArgumentException`: строки из мусора не строим.

## 3. Шов `ILocalLoraTrainer` (Core)

Файл: `backend/ClaudeHomeServer.Core/Services/ImageEditor/ILocalLoraTrainer.cs`. Реализация — `LocalLoraTrainerAdapter` в Images, регистрация в `LocalMediaRegistration.cs` как singleton. Потребители берут шов необязательным параметром (`ILocalLoraTrainer? trainer = null`).

```csharp
namespace ClaudeHomeServer.Services.ImageEditor;

public interface ILocalLoraTrainer
{
    // LocalMedia:Enabled && LocalMedia:LoraTrainEnabled. ComfyUI при этом может лежать
    bool Enabled { get; }

    // Идущее на инстансе обучение (любого владельца) или null. Mine — оно этого владельца
    LoraTrainBusy? Busy(string ownerId);

    Task<LoraTrainStartResult> StartAsync(LoraTrainStart request, CancellationToken ct);

    // Чужая задача неотличима от несуществующей: null. Обновляет статус из истории ComfyUI
    Task<LoraTrainState?> GetAsync(string ownerId, string jobId, CancellationToken ct);

    // Задачи lora_train владельца, новые сверху, без опроса ComfyUI
    IReadOnlyList<LoraTrainState> ListFor(string ownerId);

    // Все задачи lora_train стора, любых владельцев. ТОЛЬКО для фонового доводчика
    IReadOnlyList<LoraTrainState> ListAll();

    // asAdmin = true — отмена без проверки владельца (ручка админа); иначе чужая → NotFound
    Task<LoraCancelResult> CancelAsync(string ownerId, string jobId, bool asAdmin, CancellationToken ct);

    // Идемпотентно: первый вызов ставит op lora_delete, следующие отдают его исход.
    // Deleted — каталога прогона на GPU нет (в том числе «и не было»)
    Task<LoraDeleteState> DeleteRunAsync(string ownerId, string projectId, string slug, string runId, CancellationToken ct);
}

public static class LoraTrainPresets
{
    public const string Standard = "standard";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Standard };
}

// HostPath — абсолютный путь файла в data/lora-training (собран ImageEditor через SafePath.Join);
// Name — имя по регэкспу из §1, под ним фото уедет в input ComfyUI
public sealed record LoraTrainPhoto(string Name, string HostPath);

public sealed record LoraTrainStart(
    string OwnerId,
    string ProjectId,
    string Slug,
    string RunId,
    string Preset,
    string Trigger,
    IReadOnlyList<LoraTrainPhoto> Photos);

public enum LoraTrainErrorCode
{
    Disabled, Unavailable, GpuBusy, OwnerBusy, QueueFull, TooFewPhotos, BadPreset, BadTrigger, BadRequest,
}

public sealed record LoraTrainBusy(DateTime? BusyUntil, bool Mine, string JobId);

public sealed record LoraTrainStartResult(
    string? JobId,
    int? QueuePosition,
    int? EtaSeconds,
    LoraTrainErrorCode? Error = null,
    string? Message = null,
    LoraTrainBusy? Busy = null);

public enum LoraTrainStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed record LoraPhotoRejection(string Name, string Reason);   // Reason: no_face | many_faces | small_face | unreadable

public sealed record LoraTrainFailure(string Code, string Message);   // Code — §6.3

public sealed record LoraTrainState(
    string JobId,
    string OwnerId,
    string ProjectId,
    string Slug,
    string RunId,
    string Preset,
    LoraTrainStatus Status,
    int? QueuePosition,
    int? Step,               // null — шаг неизвестен (нет сокета, рестарт бэкенда)
    int? TotalSteps,
    int? EtaSeconds,         // остаток в секундах; null — не обещаем
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? LoraFile,        // только при Completed; равен LoraNamespace.RunLoraFile(...)
    LoraTrainFailure? Error,
    int? PhotosKept,
    IReadOnlyList<LoraPhotoRejection>? Rejected);

public enum LoraCancelResult { Cancelled, NotFound, AlreadyFinished }

public enum LoraDeleteState { Pending, Deleted, Failed }
```

Правила адаптера:
- **Порядок проверок `StartAsync`** — под `_submitGate` `LocalMediaService`:
  1. `Enabled` (`Disabled`);
  2. `Preset` из `All` (`BadPreset`);
  3. `Trigger` по регэкспу (`BadTrigger`);
  4. `Slug`/`RunId` валидны, `Photos.Count` 20–50, имена по регэкспу (`BadRequest`/`TooFewPhotos`);
  5. `ActiveLoraTrainCount(owner) > 0` (`OwnerBusy`);
  6. `ActiveLoraTrainCount() > 0` (`GpuBusy`, заполнить `Busy`);
  7. `MaxQueuedPerOwner`, `MaxComfyQueue` (`QueueFull`);
  8. ComfyUI недоступен (`Unavailable`);
  9. загрузка фото;
  10. постановка графа.
- **Сбой загрузки** любого фото — задача не ставится, `Unavailable`. Уже загруженные удалит `lora_delete` при уборке версии (`failed` → удаление).
- **ETA при постановке** — `LocalMediaService.LoraTrainEta(preset, startSeconds)`: таблица по пресету. Значение `standard` = 2000 шагов × 2,35 с + `LocalMedia:LoraTrainStartSeconds` = 4850 с. Дефолт старта — **150 с** по замеру 1г (97–125 с до первого шага), прежние 300 — устаревшая оценка; правка константы и теста — за исполнителем шага 5. Пробные картинки ai-toolkit в продукте выключены (ADR-026), поэтому прежние 6660 с ручного прогона к продукту не относятся. Концы регэкспов — `\z`, а не `$`: `$` пропускает финальный `\n`.
- **Остаток в `LoraTrainState.EtaSeconds`:**
  - `Running` с известным шагом — `(TotalSteps − Step) × секунд/шаг` из той же таблицы;
  - без шага — `StartedAt + EtaSeconds − now`, не меньше 60;
  - `Queued` — `EtaSeconds` при постановке.
- **`BusyUntil`** = `(StartedAt ?? CreatedAt) + EtaSeconds` активной задачи.

## 4. `LocalMediaJobStore` и `LocalMediaService` (Images)

Новые операции в `LocalMediaOps`: `LoraTrain = "lora_train"`, `LoraDelete = "lora_delete"`. В `IsKnown` они **не** входят: это не инструменты MCP `local-media`, ставит их только адаптер шва.

Новые поля `LocalMediaJob`. Все аддитивные, `FormatVersion` не меняется:

```csharp
public string? LoraSlug { get; set; }
public string? LoraRunId { get; set; }
public string? LoraPreset { get; set; }
public string? LoraFile { get; set; }                  // итог lora_train
public int? LoraPhotosKept { get; set; }
public List<LoraPhotoRejectionDto>? LoraRejected { get; set; }   // { name, reason }
public string? ErrorCode { get; set; }                 // §6.3; Error — текст
public bool Cancelled { get; set; }                    // отмена: Status = failed, Cancelled = true
```

- **`Status`** остаётся `queued | running | completed | failed`. `Cancelled` — флаг, иначе поменялась бы семантика `IsTerminal` для всего стора.
- **Счётчики** (образец — `ActiveHeavyCount`): `ActiveLoraTrainCount()` по инстансу и `ActiveLoraTrainCount(string ownerId)`. Обе считают незавершённые `Op == lora_train`.
- **`Heavy = true`** у `lora_train`.
- **Сбор:** `CollectPendingAsync` для `lora_train`/`lora_delete` идёт в `CollectLoraAsync`, а не в `CollectUnderGateAsync`. Эта ветка проект не резолвит, файлов не скачивает, `Outputs` оставляет пустым, усыновителей не зовёт.
  - `lora_train` + успех: имя из `ui.ccs_lora[0].name` обязано **точно** равняться `LoraNamespace.RunLoraFile(owner, slug, runId)`, иначе `failed` с `bad_result`. Затем `LoraFile`, `LoraPhotosKept`, `LoraRejected` из `ccs_stats`.
  - `lora_delete` + успех → `completed`.
  - Ошибка → `failed`, `ErrorCode` по §6.3.
- **`ComfyClient`:** `GetHistoryAsync` дополнительно читает массивы `ccs_lora` и `ccs_stats` выходов узла в новые свойства `ComfyHistoryEntry.CcsLora` / `CcsStats` (`JsonArray?`). Прежние ключи не трогаются.
- **Прогресс:** `LocalLoraTrainerAdapter` держит не больше одного слушателя `ComfyClient.ListenProgressAsync(promptId, f => step = (int)Math.Round(f * total), ct)`. Он стартует при `running` в `GetAsync` или `ListFor` и гаснет на терминальном статусе. `total` — шаги пресета (2000). Шаг — поле адаптера, в стор он не пишется.

Граф — `backend/ClaudeHomeServer.Images/Services/LocalMedia/ComfyWorkflows.Lora.cs`:

```csharp
public static partial class ComfyWorkflows
{
    // inputs — имена, которые вернул UploadInputAsync: "ccs/<ownerKey>/<runId>/<sha16>.jpg"
    public static JsonObject LoraTrain(string ownerId, string slug, string runId, string trigger, string preset,
        IReadOnlyList<string> inputs, string jobId);   // → AudioWorker("lora_train", params, inputs, jobId, 240)

    public static JsonObject LoraDelete(string ownerId, string slug, string runId, string jobId);
        // → AudioWorker("lora_delete", params, [], jobId, 1)
}
```

`WorkerOps` дополняется `"lora_train", "lora_delete"`. Тест графа проверяет:
- в `params_json` ровно ключи из §5;
- каждый вход начинается с `RunInputFolder(...) + "/"`;
- невалидный `runId`/`slug`/пресет даёт `ArgumentException`.

## 5. Провод: узел `CcsAudioWorker` (GPU)

Граф — один узел `CcsAudioWorker` (как у всех аудио-op).

**`lora_train`:**

```json
{
  "op": "lora_train",
  "params_json": "{\"owner\":\"a300cc8f0074…\",\"slug\":\"andrey\",\"run_id\":\"3f9a1c2b7d10\",\"trigger\":\"ccsandrey\",\"preset\":\"standard\"}",
  "inputs": "ccs/a300cc8f0074…/3f9a1c2b7d10/0a1b2c3d4e5f6a7b.jpg\nccs/a300cc8f0074…/3f9a1c2b7d10/…",
  "filename_prefix": "lm_<jobId>",
  "timeout_minutes": 240
}
```

**`lora_delete`:**

```json
{
  "op": "lora_delete",
  "params_json": "{\"owner\":\"a300cc8f0074…\",\"slug\":\"andrey\",\"run_id\":\"3f9a1c2b7d10\"}",
  "inputs": "",
  "filename_prefix": "lm_<jobId>",
  "timeout_minutes": 1
}
```

В `params_json` других ключей нет. Путей, yaml, числа шагов, `rank` и `lr` на проводе нет никогда.

Узел, файл `deploy/comfyui/audio/ccs_audio_worker/__init__.py`:

- `OPS` += `"lora_train": "lora"`, `"lora_delete": "lora"`. Обе op уходят в отдельную функцию `_run_lora` до общего `_run`.
- **Проверки** до любых действий: `owner` `^[0-9a-f]{32}$`, `slug` по регэкспу §1, `run_id` `^[0-9a-f]{12}$`, `trigger` `^[a-z][a-z0-9_]{2,23}$`, `preset` из `{"standard"}`. Неверно → `ValueError`.
- **Корень весов** — `family["loras_root"]` из `workers.json` (пишет `install.sh`). Узел сверяет: его `realpath` обязан совпасть с `realpath` одного из `folder_paths.get_folder_paths("loras")`.
- `out_dir = <loras_root>/ccs/<owner>/<slug>-<run_id>`, `in_dir = <input>/ccs/<owner>/<run_id>`. У обоих `realpath` обязан начинаться с корня + `os.sep`, символические ссылки на любом уровне — отказ.
- **`lora_train`:**
  - каждый вход обязан начинаться с `ccs/<owner>/<run_id>/` и пройти `_safe_input`;
  - выгрузка — по §10 ADR: без `vram_mb` полная, плюс сброс RAM-кэша в ветке (Б);
  - `job.json` получает `out_dir`, воркер пишет только туда;
  - прогресс — §5.1;
  - успех: файл `<slug>.safetensors` есть в `out_dir`, тогда `return {"ui": {"ccs_lora": [{"name": "ccs/<owner>/<slug>-<run_id>/<slug>.safetensors"}], "ccs_stats": [stats]}}`;
  - `finally` — удалить `in_dir`; при неуспехе (исключение, прерывание, таймаут) — ещё и `out_dir`.
- **`lora_delete`:** без выгрузки моделей и без подпроцесса. Удалить `out_dir` и `in_dir`, если есть; нет — это успех. `return {"ui": {"ccs_lora": [{"deleted": "ccs/<owner>/<slug>-<run_id>/"}]}}`.

### 5.1 Воркер `deploy/comfyui/lora/worker_lora.py`

- Протокол общий (README аудио): `argv[1]` — `job.json` с ключами `op`, `params`, `inputs` (абсолютные пути), `out_dir`, `prefix`. Последняя строка stdout `CCS_RESULT {json}`.
- **Прогресс:** строки `CCS_PROGRESS {"step": 120, "total": 2000}` не реже раза в 10 шагов. Источник шага — вывод ai-toolkit, разбор на совести воркера.
  - Узел хранит последнюю строку и в цикле ожидания (поток задачи, раз в 0,5 с) зовёт `ProgressBar(total).update_absolute(step, total)`. Создаётся `ProgressBar` лениво в том же потоке.
  - Из потока-читателя вызывать нельзя.
- **Подготовка:** InsightFace (`buffalo_l`, CPU или GPU — на усмотрение Марка) → кропы «голова» 2,6 × и «по пояс» 5,5 × лица, квадрат. Отказы по фото — `no_face`, `many_faces`, `small_face`, `unreadable`. Годных < 15 → `CCS_RESULT {"error":"too_few_faces","stats":{…}}` и код выхода 2.
- **Подписи:** `photo of <trigger>, head shot` / `photo of <trigger>, waist-up shot`.
- **yaml** рендерится из шаблона `deploy/comfyui/lora/templates/standard.yaml`. Параметры v1:
  - rank 16, alpha 16, lr 1e-4, adamw8bit, bf16, convrot8, `low_vram`;
  - разрешения 512/768/1024, `steps: 2000`, `save_every: 250`;
  - пробные картинки выключены, фиксированный seed.
- **Результат:** `*_000001750.safetensors` → `out_dir/<slug>.safetensors`, остальные чекпоинты и служебные файлы ai-toolkit в `out_dir` удалить. Итог `CCS_RESULT {"files":["<slug>.safetensors"],"stats":{"photos_kept":34,"rejected":[{"name":"0a1b….jpg","reason":"no_face"}],"steps":2000,"checkpoint":1750}}`.
- **Ошибка `too_few_faces`** узел превращает в исключение с текстом, содержащим `ccs_error=too_few_faces`. Бэкенд ищет маркер `ccs_error=<code>` в `exception_message` истории.

## 6. ImageEditor: хранение

### 6.1 Обучающие фото — `CharacterTrainingPhotos`

Файл: `backend/ClaudeHomeServer.ImageEditor/Characters/CharacterTrainingPhotos.cs`.

- Корень: `Path.GetDirectoryName(config["DataPath"])`, как у `LocalMediaJobStore`.
- Каталог: `SafePath.Join(dataDir, "lora-training", ownerKey, projectKey, slug)`.

```csharp
public sealed class CharacterTrainingPhotos(IConfiguration config)
{
    public const int MinPhotos = 20, MaxPhotos = 50, MinShortSide = 512;
    public IReadOnlyList<TrainingPhoto> List(string ownerId, string projectId, string slug);
    public TrainingPhotosAddResult Add(string ownerId, string projectId, string slug, IReadOnlyList<PhotoUpload> uploads);
    public bool Remove(string ownerId, string projectId, string slug, string name);
    public string? PathOf(string ownerId, string projectId, string slug, string name);   // для GET файла и для старта
    public static string PhotosHash(IEnumerable<string> names);   // "sha256:<64 hex>"
    public static bool IsValidName(string? name);
}
public sealed record TrainingPhoto(string Name, long Bytes, int Width, int Height);
public sealed record TrainingPhotosAddResult(IReadOnlyList<TrainingPhoto> Added, IReadOnlyList<TrainingPhotoRejection> Rejected);
public sealed record TrainingPhotoRejection(string FileName, string Reason);   // format | too_big | too_small | duplicate | limit
```

`PhotosHash`: имена сортируются ordinal, соединяются `\n`, от UTF-8 считается SHA-256, результат — `"sha256:" + hex` в нижнем регистре.

### 6.2 Записи версий — `CharacterLoraRuns`

Файл: `backend/ClaudeHomeServer.ImageEditor/Characters/CharacterLoraRuns.cs`. Место: `<project>/characters/<slug>/lora/runs/<runId>.json`, через `SafePath.Join` от корня проекта. Битый json пропускается с Warning и список не роняет.

Методы:

| Метод | Что делает |
|---|---|
| `List` | без `deleting`, новые сверху |
| `ListDeleting` | только записи `deleting` |
| `Get` | одна запись |
| `Create` | новая запись |
| `Update` | правка записи |
| `Erase` | стирает запись |
| `CountTowardsLimit` | `ready` + `training` |

`MaxVersions = 5`.

Схема записи:

```json
{
  "version": 1,
  "runId": "3f9a1c2b7d10",
  "jobId": "lm_5d0c…",
  "status": "ready",
  "title": "До бороды",
  "preset": "standard",
  "trigger": "ccsandrey",
  "photosCount": 34,
  "photosHash": "sha256:9f2c…",
  "createdAt": "2026-10-10T08:00:00Z",
  "trainedAt": "2026-10-10T09:52:10Z",
  "loraFile": "ccs/a300cc8f0074…/andrey-3f9a1c2b7d10/andrey.safetensors",
  "photosKept": 31,
  "rejected": [{ "name": "0a1b2c3d4e5f6a7b.jpg", "reason": "no_face" }]
}
```

- `status`: `training | ready | failed | cancelled | deleting`.
- `title` `null` → фронт показывает шаблон «Обучено {дата trainedAt}, {photosCount} фото».
- `error`: `{ "code": "...", "message": "..." }` — только у `failed`.
- `loraFile` заполнен только у `ready`.
- `photosCount` — сколько отправлено, `photosKept` — сколько годных у воркера.

### 6.3 Коды ошибок прогона (`error.code`, `LocalMediaJob.ErrorCode`)

| Код | Когда |
|---|---|
| `too_few_faces` | годных фото < 15 |
| `oom` | в хвосте ошибки воркера `out of memory` |
| `timeout` | истёк `timeout_minutes` узла |
| `comfy_restart` | задача пропала из очереди и истории ComfyUI |
| `bad_result` | узел вернул имя не из `RunLoraFile` |
| `worker_error` | всё остальное |

Отмена кодом не считается: это `cancelled`.

### 6.4 Доводчик — `LoraTrainingFinisher`

Файл: `backend/ClaudeHomeServer.ImageEditor/Characters/LoraTrainingFinisher.cs`, `BackgroundService`, круг раз в 30 с. Регистрируется в `ImageEditorSubsystem` при наличии шва. Тот же метод `SettleAsync(ownerId, projectId, slug)` вызывают GET-ручки персонажа перед ответом.

Проект резолвится через `IProjectManager.GetById(projectId)` с проверкой `project.OwnerId == ownerId` (образец — `LocalImageAdopter`). Нет проекта, персонажа или записи → задачу пропускаем.

Для каждой `ListAll()`-задачи с записью в статусе `training`:

| Статус задачи | Что делает доводчик |
|---|---|
| `Completed` | запись → `ready` (`loraFile`, `trainedAt` = `FinishedAt`, `photosKept`, `rejected`); затем, **только если `manifest.ReadLora() == null`**, — `CharacterStore` `Set(loraFile, trigger, strength 0,8)` |
| `Failed` | запись → `failed` с `error` |
| `Cancelled` | запись → `cancelled` |

Манифест в двух последних случаях не трогается.

Для каждой записи `deleting` вызывается `DeleteRunAsync`. `Deleted` → `Erase`, `Pending` → ждём, `Failed` → повтор на следующем круге.

## 7. Маршруты

Префикс проектных ручек — `api/projects/{projectId}/image-editor`, гейт — `ProjectGate` (чужой проект — 404). Тело ошибки везде — `{ "error": "<текст для человека>", "code": "<код>" }`, как у `ImageEditorEndpoints.Error`.

Контроллеры (новые файлы в `backend/ClaudeHomeServer.ImageEditor/Controllers/`):
- `CharacterLoraTrainController.cs` — фото и обучение;
- `CharacterLoraVersionsController.cs` — версии;
- `LoraTrainAdminController.cs` — ручки уровня инстанса.

### 7.1 Обучающие фото

| Метод и путь | Успех | Ошибки |
|---|---|---|
| `GET characters/{slug}/training-photos` | 200 `TrainingPhotosDto` | 404 `character_not_found` |
| `POST characters/{slug}/training-photos` (multipart, поле `files`, до 10 файлов за запрос) | 200 `{ "photos": TrainingPhotosDto, "rejected": [{ "fileName", "reason" }] }` — отказ по фото не роняет запрос | 404 `character_not_found`; 400 `invalid_request` (нет файлов, > 10) |
| `GET characters/{slug}/training-photos/{name}` | 200 файл (`image/*`) | 404 `photo_not_found` (в т. ч. имя не по регэкспу) |
| `DELETE characters/{slug}/training-photos/{name}` | 204 | 404 `photo_not_found` |

```json
{ "items": [{ "name": "0a1b2c3d4e5f6a7b.jpg", "bytes": 2483113, "width": 3024, "height": 4032 }],
  "count": 34, "min": 20, "max": 50, "photosHash": "sha256:9f2c…" }
```

Удалять фото во время обучения можно: к этому моменту они уже на GPU.

### 7.2 Обучение

**`GET characters/{slug}/lora-train`** → 200; нет шва — `503 no_seam`, как у остальных ручек обучения (поля `trainer` в ответе нет):

```json
{
  "enabled": true,
  "available": true,
  "gpu": { "busy": true, "mine": false, "busyUntil": "2026-10-10T10:05:00Z" },
  "current": {
    "runId": "3f9a1c2b7d10", "jobId": "lm_5d0c…", "status": "running",
    "step": 640, "totalSteps": 2000, "etaSeconds": 3196, "queuePosition": null,
    "startedAt": "2026-10-10T08:01:30Z"
  },
  "defaultTrigger": "ccsandrey",
  "presets": [{ "key": "standard", "etaSeconds": 4850 }],
  "photosChanged": false
}
```

- `current` — запись этого персонажа со статусом `training`, иначе `null`.
- `photosChanged` — `photosHash` текущих фото отличается от хеша подключённой версии; подключённой нет → `false`.

**`POST characters/{slug}/lora-train`**, тело:

```json
{ "preset": "standard", "trigger": "ccsandrey", "confirm": true }
```

`trigger` необязателен (по умолчанию `defaultTrigger`). Успех — 202:

```json
{ "runId": "3f9a1c2b7d10", "jobId": "lm_5d0c…", "queuePosition": 0, "etaSeconds": 4850 }
```

Запись `training` создаётся **после** успешного `StartAsync`; сбой старта записи не оставляет.

| Статус | `code` | Когда |
|---|---|---|
| 503 | `no_seam` | нет шва (подсистема images выключена) |
| 503 | `unavailable` | ComfyUI не отвечает |
| 403 | `disabled` | `LocalMedia:LoraTrainEnabled = false` |
| 400 | `confirm_required` | нет `confirm: true` |
| 400 | `bad_preset` | пресет не из списка |
| 400 | `bad_trigger` | триггер не по регэкспу |
| 400 | `too_few_photos` | фото для обучения < 20 |
| 404 | `character_not_found` | |
| 409 | `owner_busy` | у владельца уже идёт обучение (любого персонажа) |
| 409 | `gpu_busy` | идёт чужое обучение; тело дополнительно `{ "busyUntil": "…" }` |
| 409 | `version_limit` | у персонажа 5 версий `ready` + `training` |
| 409 | `queue_full` | `MaxQueuedPerOwner` / `MaxComfyQueue` |

**`DELETE characters/{slug}/lora-train`** — отменить своё идущее обучение этого персонажа.
- 204 — отменено; запись станет `cancelled` на ближайшем `SettleAsync`.
- 404 `not_training` — у персонажа нет записи `training`.
- 503 `no_seam`.

### 7.3 Версии

**`GET characters/{slug}/lora-versions`** → 200:

```json
{
  "items": [
    { "runId": "3f9a1c2b7d10", "status": "ready", "title": null, "trainedAt": "2026-10-10T09:52:10Z",
      "photosCount": 34, "photosKept": 31, "trigger": "ccsandrey", "connected": true,
      "rejected": [{ "name": "0a1b2c3d4e5f6a7b.jpg", "reason": "no_face" }] },
    { "runId": "77ab01cd9e22", "status": "failed", "createdAt": "2026-10-09T22:10:00Z", "photosCount": 22,
      "connected": false, "error": { "code": "too_few_faces", "message": "Годных фото 12 из 22, нужно хотя бы 15" } }
  ],
  "own": null,
  "limit": 5,
  "used": 1
}
```

- `connected` = `loraFile == providers.local.lora`.
- `own` — `{ "lora": "<имя>" }`, если `providers.local.lora` задана и не совпала ни с одной версией («Сейчас подключена своя LoRA»), иначе `null`.

| Метод и путь | Успех | Ошибки |
|---|---|---|
| `POST characters/{slug}/lora-versions/{runId}/connect` | 200 `CharacterDto` (как у патча персонажа) | 404 `version_not_found`; 409 `version_not_ready` (статус не `ready`); 403 `lora_not_visible`; 409 `lora_missing` (файла нет в списке ComfyUI); 503 `no_seam` / `unavailable` (список ComfyUI не получен) |
| `PATCH characters/{slug}/lora-versions/{runId}` `{ "title": "До бороды" }` | 200 элемент списка | 404 `version_not_found`; 400 `bad_title`; 409 `version_training` |
| `DELETE characters/{slug}/lora-versions/{runId}` | 202 `{ "status": "deleting", "disconnected": true }` | 404 `version_not_found`; 409 `version_training` (сначала отмена) |

Подробности:
- `PATCH` с пустым `title` после trim — сброс к шаблону, не ошибка.
- `DELETE` подключённой сначала снимает LoRA с персонажа (`disconnected: true`), затем запись → `deleting`. Статусы `failed` и `cancelled` удаляются так же: каталог на GPU мог остаться.

### 7.4 Уровень инстанса

| Метод и путь | Кто | Ответ |
|---|---|---|
| `GET api/image-editor/lora-train/active` | любой | 200 `{ "busy": true, "mine": false, "busyUntil": "…" }`; у админа ещё `jobId` |
| `DELETE api/image-editor/lora-train/active` | только `admin`, иначе 403 `forbidden` | 204 — отменено; 404 `not_training` — обучения нет |

## 8. Фронт: типы

Файл `frontend/src/features/imageEditor/api.ts`, по одной функции на ручку §7.1–7.3. Типы повторяют JSON выше:

```ts
type LoraRunStatus = 'training' | 'ready' | 'failed' | 'cancelled'
type LoraTrainJobStatus = 'queued' | 'running'
interface LoraTrainCurrent {
  runId: string; jobId: string; status: LoraTrainJobStatus
  step?: number; totalSteps?: number; etaSeconds?: number; queuePosition?: number | null; startedAt?: string
}
interface LoraTrainStateDto {
  enabled: boolean; available: boolean
  gpu: { busy: boolean; mine: boolean; busyUntil?: string }
  current: LoraTrainCurrent | null
  defaultTrigger: string; presets: { key: 'standard'; etaSeconds: number }[]; photosChanged: boolean
}
interface LoraTrainStartResponse { runId: string; jobId: string; queuePosition: number; etaSeconds: number }
interface LoraActiveDto { busy: boolean; mine: boolean; busyUntil?: string; jobId?: string }
interface LoraVersionDto {
  runId: string; status: LoraRunStatus; title?: string | null; createdAt?: string; trainedAt?: string
  photosCount: number; photosKept?: number; trigger?: string; connected: boolean
  rejected?: { name: string; reason: 'no_face' | 'many_faces' | 'small_face' | 'unreadable' }[]
  error?: { code: string; message: string }
}
interface LoraVersionsDto { items: LoraVersionDto[]; own: { lora: string } | null; limit: number; used: number }
```

Отображение прогресса:

| Состояние | Что показывать |
|---|---|
| `running` и `step > 0` | «шаг N из M, осталось ≈ K мин» |
| `running` без `step` или `step === 0` | «готовим фото и загружаем модель», если нет `etaSeconds`; иначе «осталось ≈ K мин» |
| `queued` | «в очереди: позиция P» |

Опрос `GET lora-train` — раз в 15 с, пока `current` не `null`. Тексты — заметка «LoRA-обучение — тексты».

## 9. Конфиг

`LocalMediaOptions` (`backend/ClaudeHomeServer.Images/Services/LocalMedia/LocalMediaOptions.cs`):

```csharp
// Обучение LoRA персонажа (ADR-026): узел CcsAudioWorker с семейством lora на GPU-машине.
// Выключено по умолчанию — карта на ≈2 ч уходит одному владельцу
public bool LoraTrainEnabled { get; set; }
```

`appsettings.Local.json` на стенде: `"LocalMedia": { "LoraTrainEnabled": true }`.

`workers.json` на gpuhost — семейство пишет `deploy/comfyui/lora/install.sh`:

```json
{ "families": { "lora": {
  "python": "/home/<user>/ai-data/audio-workers/lora/.venv/bin/python",
  "script": "/home/<user>/ai-data/audio-workers/ccs/workers/worker_lora.py",
  "loras_root": "/home/<user>/ai-data/comfy-models/loras",
  "env": { "AI_TOOLKIT_DIR": "/home/<user>/ai-data/audio-workers/lora/ai-toolkit" }
} } }
```

Конкретные пути берёт `install.sh` машины; приведённые — форма, а не факт.

## 10. Карта (`CLAUDE.md`)

Одна строка в конец раздела «Генератор картинок» (шаг 8):

```
Обучение LoRA персонажа — op `lora_train` того же узла в общей очереди GPU, одно на инстанс, веса только в `loras/ccs/<владелец>/`, видимость — только `LoraNamespace.VisibleTo` ([ADR-026](docs/adr/ADR-026-character-lora-training.md)).
```
