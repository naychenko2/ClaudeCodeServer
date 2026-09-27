using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Chats;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Нить картинки в чате проекта (ADR-019 §1, §3): записи ImageThreadStore плюс их следы снаружи —
// якорь стопки и тихие строки в ленте через IChatFeed, событие image_thread_changed владельцу,
// журнал для блока хвоста хода. Ручки нитей, запуск задачи и сохранение идут сюда, чтобы следы
// не разошлись между ними.
//
// Каждый вариант запуска — версия нити (изменение 27.09): OnJobFinishedAsync по событию
// исполнителя. Правку без ИИ, «продолжить от версии» и «Взять» старых нитей зовут только ручки
// человека. Агенту доступны AgentFocusAsync, AgentOpenAsync и AgentContinueAsync — взять картинку в
// работу, снять выбор, завести черновик, выбрать версию-основу; смена картинки видна в ленте тихой
// строкой «Claude взял в работу: …».
//
// Чат проверяет вызывающий там, где отказ — часть ответа (ручки нитей: 404). Запуск и
// сохранение с чужим чатом или нитью не падают — OwnChat молча отбрасывает, ответ не выдаёт
// существование чужого.
public sealed class ImageThreadService(
    ImageThreadStore store,
    ILogger<ImageThreadService> log,
    ISessionDirectory? directory = null,
    IChatFeed? feed = null,
    ISessionBroadcaster? broadcaster = null,
    ImageEditSteps? steps = null,
    Prefs.ImageProjectPrefsService? prefs = null)
{
    public const string ModuleKey = "imageeditor";

    public static class RecordTypes
    {
        // Якорь нити — карточка исходника: data { threadId, versionId: "origin" }; у нитей до 27.09
        // якорь стопки data { threadId, stackId }. Содержимое карточки рисуется из хранилища
        public const string Thread = "image_thread";
        // Якорь запуска ИИ внизу ленты: data { threadId, jobId, prompt, provider, model, count,
        // initiator, baseVersionId }. Варианты запуска — версии нити с этим jobId
        public const string LaunchVersions = "image_launch_versions";
        // Тихие строки: data { threadId, … }. image_launch — ручной запуск до 27.09, больше не пишется
        public const string Launch = "image_launch";
        public const string Saved = "image_saved";
        public const string StackForked = "image_stack_forked";
        // Агент сменил картинку в работе: data { threadId, by: "agent" }
        public const string Focus = "image_focus";
    }

    // Сколько раз агент перечитывает ревизию, если человек записал своё между чтением и записью
    private const int AgentAttempts = 3;

    public ImageThreadsState Get(string ownerId, string sessionId) => store.Get(ownerId, sessionId);

    // Чат этого проекта (проект уже свой — его проверил вызывающий): владение следует из проекта
    public bool OwnChat(string projectId, string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && directory?.GetById(sessionId.Trim()) is { } s && s.ProjectId == projectId;

    // Своя нить своего чата: для запуска и сохранения, где чужое молча отбрасывается
    public bool OwnThread(string ownerId, string projectId, string? sessionId, string? threadId) =>
        OwnChat(projectId, sessionId) && !string.IsNullOrWhiteSpace(threadId)
        && store.Get(ownerId, sessionId!.Trim()).Threads.Any(t => t.Id == threadId.Trim());

    // Взять картинку в работу: новая нить с фокусом и якорем её стопки в ленте; нить по этому
    // файлу уже есть — только фокус, второго якоря нет
    public async Task<ImageThreadWrite> OpenAsync(string ownerId, string projectId, string sessionId,
        string? file, string? draftFolder, long revision, CancellationToken ct)
    {
        var written = store.Open(ownerId, sessionId, file, draftFolder, revision, NewThreadSettings(ownerId, projectId));
        if (written.Status == ImageThreadWriteStatus.Ok && written is { Existing: false, Thread: { } thread })
            await AnchorAsync(sessionId, thread, ct);
        return await AfterAsync(ownerId, projectId, sessionId, written);
    }

    public Task<ImageThreadWrite> FocusAsync(string ownerId, string projectId, string sessionId, string? threadId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.SetFocus(ownerId, sessionId, threadId, revision));

    public Task<ImageThreadWrite> RemoveAsync(string ownerId, string projectId, string sessionId, string threadId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.Remove(ownerId, sessionId, threadId, revision));

    // Откат стопки — только у нити до 27.09: у новой нити стопок нет, её «откат» — продолжить от версии
    public Task<ImageThreadWrite> RollbackAsync(string ownerId, string projectId, string sessionId, string threadId,
        string? stepId, long revision) =>
        store.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == threadId) is { Stacks.Count: 0 }
            ? Task.FromResult(new ImageThreadWrite(ImageThreadWriteStatus.Invalid, store.Get(ownerId, sessionId)))
            : AfterAsync(ownerId, projectId, sessionId, store.Rollback(ownerId, sessionId, threadId, stepId, revision));

    // «Продолжить от версии» (человек): версия становится текущей, нить — в работе. Ничего не удаляет
    public Task<ImageThreadWrite> ContinueAsync(string ownerId, string projectId, string sessionId, string threadId,
        string versionId, string? stepId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId,
            store.SetCurrentVersion(ownerId, sessionId, threadId, versionId, stepId, revision, focus: true));

    // Агент выбрал версию («поправь вторую»): она становится текущей. Ревизию агент не держит
    public Task<ImageThreadWrite> AgentContinueAsync(string ownerId, string projectId, string sessionId, string threadId,
        string versionId) =>
        AfterAsync(ownerId, projectId, sessionId,
            store.SetCurrentVersion(ownerId, sessionId, threadId, versionId, null, null));

    // Правка без ИИ (готовый шаг POST …/transform) ложится шагом текущей версии: новой версии и
    // карточки в ленте нет. Шаг обязан быть своим — чужой неотличим от отсутствующего
    public async Task<ImageThreadTake> AddStepAsync(string ownerId, string projectId, string sessionId, string threadId,
        string stepId, long revision)
    {
        if (steps is null)
            return ImageThreadTake.Fail(ImageEditErrorCodes.RasterUnavailable, "Обработка картинок выключена на этом сервере");
        if (steps.Open(ownerId, projectId, stepId) is null)
            return ImageThreadTake.Fail(ImageEditErrorCodes.StepNotFound, "Шаг истории не найден — возможно, он устарел");
        return new ImageThreadTake(await AfterAsync(ownerId, projectId, sessionId,
            store.AddVersionStep(ownerId, sessionId, threadId, stepId, revision)));
    }

    public Task<ImageThreadWrite> DismissAsync(string ownerId, string projectId, string sessionId, string threadId,
        string jobId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.Dismiss(ownerId, sessionId, threadId, jobId, revision));

    public Task<ImageThreadWrite> SetSettingsAsync(string ownerId, string projectId, string sessionId, string threadId,
        ImageThreadSettings settings, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.SetSettings(ownerId, sessionId, threadId, settings, revision));

    // Агент берёт в работу нить этого чата (threadId) или снимает выбор (null). Ревизию агент не
    // держит — берётся актуальная, гонка с человеком лечится перечитыванием. Фокус сменился —
    // тихая строка в ленте; тот же фокус повторно ничего не пишет
    public async Task<ImageThreadWrite> AgentFocusAsync(string ownerId, string projectId, string sessionId,
        string? threadId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var before = store.Get(ownerId, sessionId);
            var written = store.SetFocus(ownerId, sessionId, threadId, before.Revision);
            if (written.Status == ImageThreadWriteStatus.Conflict && attempt < AgentAttempts - 1) continue;
            if (written.Status == ImageThreadWriteStatus.Ok && written.State.Revision != before.Revision)
                await FocusLineAsync(sessionId, written.State, before, ct);
            return await AfterAsync(ownerId, projectId, sessionId, written);
        }
    }

    // Агент берёт в работу файл проекта (нить по нему — новая или уже существующая) или заводит
    // черновик «Новая картинка» в папке. Сначала тихая строка, следом якорь новой стопки: в ленте
    // «Claude взял в работу: logo.png», под ней карточка
    public async Task<ImageThreadWrite> AgentOpenAsync(string ownerId, string projectId, string sessionId,
        string? file, string? draftFolder, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var before = store.Get(ownerId, sessionId);
            var written = store.Open(ownerId, sessionId, file, draftFolder, before.Revision,
                NewThreadSettings(ownerId, projectId));
            if (written.Status == ImageThreadWriteStatus.Conflict && attempt < AgentAttempts - 1) continue;
            if (written.Status == ImageThreadWriteStatus.Ok && written.State.Revision != before.Revision)
                await FocusLineAsync(sessionId, written.State, before, ct);
            if (written is { Status: ImageThreadWriteStatus.Ok, Existing: false, Thread: { } thread })
                await AnchorAsync(sessionId, thread, ct);
            return await AfterAsync(ownerId, projectId, sessionId, written);
        }
    }

    // Новая нить (от человека и от агента) начинает с выбора человека в полосе «Картинки» проекта
    private ImageThreadSettings? NewThreadSettings(string ownerId, string projectId) =>
        prefs?.SettingsFor(ownerId, projectId);

    // «Claude взял в работу: logo.png» или «Claude снял выбор: logo.png»
    private Task FocusLineAsync(string sessionId, ImageThreadsState after, ImageThreadsState before, CancellationToken ct)
    {
        if (after.Focus is { } focus && after.Threads.FirstOrDefault(t => t.Id == focus) is { } taken)
            return RecordAsync(sessionId, RecordTypes.Focus, FocusText(taken),
                new { threadId = taken.Id, by = SpendInitiators.Agent }, ct);
        var dropped = before.Threads.FirstOrDefault(t => t.Id == before.Focus);
        return RecordAsync(sessionId, RecordTypes.Focus, UnfocusText(dropped),
            new { threadId = (string?)null, by = SpendInitiators.Agent }, ct);
    }

    public static string FocusText(ImageThread thread) => $"Claude взял в работу: {Name(thread)}";

    public static string UnfocusText(ImageThread? thread) =>
        thread is null ? "Claude снял выбор картинки" : $"Claude снял выбор: {Name(thread)}";

    // «Взять»: вариант задачи этой нити (jobId + variant) или готовый шаг правки без ИИ (stepId)
    // становится шагом нити. Откат и новая правка заводят новую стопку: её якорь и строка
    // «Шаги … не пропали» ложатся в конец ленты, старая стопка остаётся на своём месте
    public async Task<ImageThreadTake> TakeAsync(string ownerId, string projectId, string sessionId, string threadId,
        string? jobId, int? variant, string? stepId, long revision, IImageEditJobs? jobs, CancellationToken ct)
    {
        var state = store.Get(ownerId, sessionId);
        if (state.Revision != revision)
            return new ImageThreadTake(new ImageThreadWrite(ImageThreadWriteStatus.Conflict, state));
        if (state.Threads.FirstOrDefault(t => t.Id == threadId) is not { } thread)
            return new ImageThreadTake(new ImageThreadWrite(ImageThreadWriteStatus.ThreadNotFound, state));
        if (steps is null)
            return ImageThreadTake.Fail(ImageEditErrorCodes.RasterUnavailable, "Обработка картинок выключена на этом сервере");
        // «Взять» — только у нити до 27.09 со стопками: у новой нити варианты сразу версии, а
        // правка без ИИ ложится через AddStepAsync
        if (thread.Stacks.Count == 0)
            return ImageThreadTake.Fail(ImageEditErrorCodes.InvalidRequest,
                "У этой картинки версии: варианты уже в ленте, «Взять» не нужно");

        string newStep;
        var label = "правку";
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            // Только задача, запущенная в эту нить этого чата: чужая неотличима от несуществующей
            var job = jobs?.Get(ownerId, projectId, jobId.Trim());
            if (job is null || job.ThreadId != threadId || job.ChatSessionId != sessionId || variant is not { } n)
                return ImageThreadTake.Fail(ImageEditErrorCodes.JobNotFound, "Задача не найдена");
            var taken = steps.TakeVariant(ownerId, projectId, job.JobId, n, thread.CurrentStepId, thread.File);
            if (taken.Value is not { } step) return ImageThreadTake.Fail(taken.ErrorCode, taken.Error);
            newStep = step.StepId;
            label = $"вариант {n}";
        }
        else if (!string.IsNullOrWhiteSpace(stepId) && steps.Open(ownerId, projectId, stepId.Trim()) is not null)
        {
            newStep = stepId.Trim();
        }
        else
        {
            return ImageThreadTake.Fail(ImageEditErrorCodes.StepNotFound, "Шаг истории не найден — возможно, он устарел");
        }

        var logText = $"Человек взял {label} в картинку {Name(thread)}";
        var written = store.AddStep(ownerId, sessionId, threadId, newStep, revision, jobId?.Trim(),
            new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Taken, logText, threadId, jobId?.Trim()));
        if (written is { Status: ImageThreadWriteStatus.Ok, Forked: { } fork, Thread: { } after })
        {
            await RecordAsync(sessionId, RecordTypes.Thread, $"Картинка: {Name(after)}",
                new { threadId = after.Id, stackId = fork.NewStack.StackId }, ct);
            await RecordAsync(sessionId, RecordTypes.StackForked, ForkText(fork),
                new { threadId, stackId = fork.NewStack.StackId, oldStackId = fork.FrozenStack.StackId }, ct);
        }
        return new ImageThreadTake(await AfterAsync(ownerId, projectId, sessionId, written));
    }

    // Задача запущена в нить: запуск записан в нити (его основа — версия baseVersionId, она же
    // становится текущей), внизу ленты — якорь запуска, где потом появятся его версии. Ход узнаёт о
    // запуске из журнала; якорь модель не видит
    public async Task OnLaunchedAsync(string ownerId, string projectId, string sessionId, string threadId,
        ImageEditJobDto? job, string jobId, string prompt, ImageEditInitiator initiator, string? baseVersionId,
        string? baseStepId, CancellationToken ct)
    {
        try
        {
            var model = job?.Model ?? "модель по котировке";
            var count = job?.Count ?? 0;
            var variants = count > 0 ? $" · {count} {ImageEditorStateContributor.Variants(count)}" : "";
            var agent = initiator == ImageEditInitiator.Agent;
            var who = agent ? "Ты запустил" : "Человек запустил вручную";
            var thread = store.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
            var from = thread?.Version(baseVersionId) is { } baseVersion ? $" · от: {ImageThread.Label(baseVersion)}" : "";
            var written = store.AddLaunch(ownerId, sessionId, threadId,
                new ImageThreadLaunch(jobId, baseVersionId, baseStepId, store.Now(), ImageThreadLaunchStatus.Running,
                    agent ? SpendInitiators.Agent : SpendInitiators.Human, prompt.Trim()),
                new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Launched,
                    $"{who}: «{prompt.Trim()}» · {model}{variants}{from}{EstimateText(job?.Estimate)}", threadId, jobId));
            if (written.Status != ImageThreadWriteStatus.Ok) return;

            await RecordAsync(sessionId, RecordTypes.LaunchVersions,
                $"{(agent ? "Claude запустил" : "Вы запустили")}: «{prompt.Trim()}» · {model}{variants}",
                new
                {
                    threadId,
                    jobId,
                    prompt,
                    provider = job?.Provider,
                    model = job?.Model,
                    count,
                    estimate = job?.Estimate,
                    initiator = agent ? SpendInitiators.Agent : SpendInitiators.Human,
                    baseVersionId,
                }, ct);
            await AfterAsync(ownerId, projectId, sessionId, written);
        }
        catch (Exception ex)
        {
            // Задача уже идёт: сбой следа не должен превращать 202 в ошибку
            log.LogWarning(ex, "Редактор картинок: запуск {JobId} не записан в нить {ThreadId}", jobId, threadId);
        }
    }

    // Задача кончилась (зовёт исполнитель): у готовой каждый вариант ложится шагом (байты как
    // есть, родитель — шаг-основа запуска) и становится версией нити внизу; у сбоя и отмены запуск
    // просто закрывается. Задача вне нити, чужой чат или запуск, которого нить не знает, —
    // ничего. Сбой здесь не роняет исполнителя: варианты остаются в задаче до TTL
    public async Task OnJobFinishedAsync(string ownerId, ImageEditJobDto job)
    {
        if (job is not { ThreadId: { } threadId, ChatSessionId: { } sessionId }) return;
        try
        {
            if (directory?.GetById(sessionId) is not { } session || session.ProjectId != job.ProjectId) return;
            var thread = store.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
            if (thread?.Launches.FirstOrDefault(l => l.JobId == job.JobId) is not { Status: ImageThreadLaunchStatus.Running } launch)
                return;

            var taken = new List<(int Variant, string StepId)>();
            var status = job.Status switch
            {
                ImageEditJobStatus.Completed => ImageThreadLaunchStatus.Done,
                ImageEditJobStatus.Cancelled => ImageThreadLaunchStatus.Cancelled,
                _ => ImageThreadLaunchStatus.Failed,
            };
            if (status == ImageThreadLaunchStatus.Done && steps is not null)
            {
                foreach (var n in job.Variants)
                {
                    var step = steps.TakeVariant(ownerId, job.ProjectId, job.JobId, n, launch.BaseStepId, thread.File);
                    if (step.Value is { } s) taken.Add((n, s.StepId));
                    else log.LogWarning("Редактор картинок: вариант {Variant} задачи {JobId} не стал версией: {Error}",
                        n, job.JobId, step.Error);
                }
                if (taken.Count == 0) status = ImageThreadLaunchStatus.Failed;
            }

            var written = store.FinishLaunch(ownerId, sessionId, threadId, job.JobId, status, taken,
                (after, added) => new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Versions,
                    VersionsText(after, launch, added, job), threadId, job.JobId));
            if (written.Status == ImageThreadWriteStatus.Ok)
                await AfterAsync(ownerId, job.ProjectId, sessionId, written);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Редактор картинок: варианты задачи {JobId} не стали версиями нити {ThreadId}", job.JobId, threadId);
        }
    }

    // «Готово «синий фон»: версии 3–4 картинки hero.png (от исходника)» / «… не получилось»
    private static string VersionsText(ImageThread thread, ImageThreadLaunch launch, IReadOnlyList<ImageThreadVersion> added,
        ImageEditJobDto job)
    {
        var what = $"«{launch.Prompt}» в картинку {Name(thread)}";
        if (added.Count == 0)
            return job.Status == ImageEditJobStatus.Cancelled ? $"Запуск {what} отменён"
                : $"Запуск {what} не получился" + (string.IsNullOrWhiteSpace(job.Error) ? "" : $" ({job.Error})");
        var numbers = added.Count == 1 ? $"версия {added[0].Number}" : $"версии {added[0].Number}–{added[^1].Number}";
        var from = thread.Version(launch.BaseVersionId) is { } b ? $", от: {ImageThread.Label(b)}" : "";
        var current = thread.CurrentVersion is { } c && added.Contains(c) ? $"; в работе {ImageThread.Label(c)}" : "";
        return $"Готово {what}: {numbers}{from}{current}";
    }

    // Варианты запуска становятся версиями, как только исполнитель их скачал: подписку ставит
    // регистрация модуля (ImageEditorSubsystem), в тестах — сам тест
    public void Watch(ImageEditJobService jobs) => jobs.Finished += OnJobFinishedAsync;

    // Человек сохранил картинку нити: нить идёт за новым файлом, в ленте тихая строка
    public async Task OnSavedAsync(string ownerId, string projectId, string sessionId, string threadId, string path,
        CancellationToken ct)
    {
        try
        {
            var written = store.MoveToFile(ownerId, sessionId, threadId, path,
                new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Saved, $"Человек сохранил картинку в проект: {path}", threadId));
            if (written.Status != ImageThreadWriteStatus.Ok) return;
            await RecordAsync(sessionId, RecordTypes.Saved, $"Сохранено как {path}", new { threadId, path }, ct);
            await AfterAsync(ownerId, projectId, sessionId, written);
        }
        catch (Exception ex)
        {
            // Файл уже в проекте: сбой следа не должен превращать 200 в ошибку
            log.LogWarning(ex, "Редактор картинок: сохранение {Path} не записано в нить {ThreadId}", path, threadId);
        }
    }

    public const string InterruptedText = "Задача потеряна при перезапуске сервера";

    // После перезапуска сервера (ADR-019): реестр задач живёт в памяти, и PendingJobId, которого
    // в нём нет, не дождётся ни вариантов, ни отказа — карточка висела бы в «Рисуем…». Такая
    // задача снимается с нити с пометкой InterruptedJobId и записью журнала для хода, владельцу
    // уходит image_thread_changed. Текст покрывает и задачу, которая до рестарта уже отрисовала
    // варианты и ждала «Взять»: они тоже потеряны вместе с реестром. Трату не трогает: поставщик принял задачу до перезапуска, и
    // «не списано» от него уже не придёт (инвариант учёта — отмена после принятия трату не отменяет)
    public async Task<int> RecoverInterruptedAsync(IImageEditJobs? jobs, CancellationToken ct)
    {
        var dropped = 0;
        foreach (var (ownerId, sessionId) in store.Chats())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var projectId = directory?.GetById(sessionId)?.ProjectId;
                var state = store.DropDeadPending(ownerId, sessionId,
                    jobId => projectId is not null && jobs?.Get(ownerId, projectId, jobId) is not null,
                    (thread, jobId) => new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Interrupted,
                        $"{InterruptedText}: картинка {Name(thread)}, варианты недоступны — запусти заново", thread.Id, jobId));
                if (state is null) continue;
                dropped++;
                if (projectId is not null) await BroadcastAsync(ownerId, projectId, sessionId, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Редактор картинок: прерванные задачи нитей чата {SessionId} не сняты", sessionId);
            }
        }
        return dropped;
    }

    public async Task BroadcastAsync(string ownerId, string projectId, string sessionId, ImageThreadsState state)
    {
        if (broadcaster is null) return;
        try
        {
            await broadcaster.ToOwner(ownerId, new ImageThreadChangedMessage(projectId, state.Revision, state) { SessionId = sessionId });
        }
        catch (Exception ex)
        {
            // Потерянное событие фронт догоняет GET …/threads
            log.LogDebug(ex, "Редактор картинок: нити чата {SessionId} не разосланы", sessionId);
        }
    }

    // Запись поменяла состояние — событие владельцу; отказы и «без изменений» тишина
    private async Task<ImageThreadWrite> AfterAsync(string ownerId, string projectId, string sessionId, ImageThreadWrite written)
    {
        if (written.Status == ImageThreadWriteStatus.Ok)
            await BroadcastAsync(ownerId, projectId, sessionId, written.State);
        return written;
    }

    // Якорь нити — карточка исходника
    private Task AnchorAsync(string sessionId, ImageThread thread, CancellationToken ct) =>
        RecordAsync(sessionId, RecordTypes.Thread, $"Картинка: {Name(thread)}",
            new { threadId = thread.Id, versionId = ImageThreadVersion.OriginId }, ct);

    // Следы в ленте не должны ронять запись нити: она уже сделана
    private async Task RecordAsync(string sessionId, string recordType, string fallback, object data, CancellationToken ct)
    {
        if (feed is null) return;
        try
        {
            await feed.AppendRecordAsync(sessionId, new StoredModuleRecord
            {
                Module = ModuleKey,
                RecordType = recordType,
                Data = JsonSerializer.SerializeToElement(data, ImageThreadStore.Json),
                Fallback = fallback,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Редактор картинок: запись {RecordType} не легла в ленту чата {SessionId}", recordType, sessionId);
        }
    }

    public static string Name(ImageThread thread) =>
        thread.File is { Length: > 0 } file ? file : "новая картинка";

    // «Шаги 3–5 не пропали — они в старой стопке выше»
    public static string ForkText(ImageThreadFork fork) => fork switch
    {
        { KeptFrom: { } a, KeptTo: { } b } when a == b => $"Шаг {a} не пропал — он в старой стопке выше",
        { KeptFrom: { } a, KeptTo: { } b } => $"Шаги {a}–{b} не пропали — они в старой стопке выше",
        _ => "Прежние шаги не пропали — они в старой стопке выше",
    };

    private static string EstimateText(ImageEditEstimateDto? e) => e?.Amount is not { } amount ? ""
        : e.Unit == ImageEditPriceUnits.Usd
            ? string.Create(CultureInfo.InvariantCulture, $" · ≈ ${amount:0.##}")
            : string.Create(CultureInfo.InvariantCulture, $" · ≈ {amount:0.##} кр.");
}

// Итог «Взять»: запись нити или отказ до неё (задача, шаг, растр) с кодом ImageEditErrorCodes.*
public sealed record ImageThreadTake(ImageThreadWrite? Write, string? ErrorCode = null, string? Error = null)
{
    public static ImageThreadTake Fail(string? code, string? error) =>
        new(null, code ?? ImageEditErrorCodes.InvalidRequest, error ?? "Запрос не выполнен");
}
