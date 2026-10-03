using System.Globalization;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
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
    Prefs.ImageProjectPrefsService? prefs = null,
    ChatContextFocusMirror? mirror = null)
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

    // Разбор вариантов в версии: событие Finished и догонка финала сборщиком запуска
    // (ImageEditLaunchAssembler) могут прийти вместе — второй обязан увидеть уже закрытый запуск,
    // иначе варианты легли бы лишними шагами рабочей папки
    private readonly SemaphoreSlim _versionsGate = new(1, 1);

    public ImageThreadsState Get(string ownerId, string sessionId) => store.Get(ownerId, sessionId);

    // Состояние для DTO (ручка GET и событие): при флаге строки контекста фокус — проекция из контекста
    // чата (основной объект своего вида), без флага — собственное поле. Читатели хода (хвост, MCP) берут
    // хранилище напрямую и проекции не видят
    public ImageThreadsState View(string ownerId, string sessionId) => Project(ownerId, sessionId, store.Get(ownerId, sessionId));

    private ImageThreadsState Project(string ownerId, string sessionId, ImageThreadsState state)
    {
        if (mirror is null) return state;
        var focus = mirror.ProjectFocus(ownerId, sessionId, ChatContext.ImageContextKind.Kind, state.Focus,
            id => state.Threads.Any(t => t.Id == id));
        return focus == state.Focus ? state : state with { Focus = focus };
    }

    // Запись, которая может сменить фокус: смена попадает в стор контекста (при флаге)
    private ImageThreadWrite Tracked(string ownerId, string sessionId, Func<ImageThreadWrite> write, ContextActor by)
    {
        var before = store.Get(ownerId, sessionId).Focus;
        var written = write();
        if (written.Status == ImageThreadWriteStatus.Ok)
            mirror?.Sync(ownerId, sessionId, ChatContext.ImageContextKind.Kind, before, written.State.Focus, by);
        return written;
    }

    // Усыновление открывает нить с фокусом и возвращает выбор человека; в контекст чата (при флаге) идёт только
    // итоговая разница: вернули прежний фокус — контекст не трогаем, остался на усыновлённой — отражаем
    private void SyncNetFocus(string ownerId, string sessionId, string? before, string? after) =>
        mirror?.Sync(ownerId, sessionId, ChatContext.ImageContextKind.Kind, before, after, ContextActor.Agent);

    // Чат этой области (область уже своя — её проверил вызывающий): владение следует из области.
    // scopeKey — id проекта или ImageEditScope.Personal у личного чата вне проекта. Ключ Personal
    // общий у всех владельцев, и владения он не доказывает: его держат гейт личного маршрута и
    // хранилище нитей владельца в OwnThread

    public bool OwnChat(string scopeKey, string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && directory?.GetById(sessionId.Trim()) is { } s
        && ImageEditScope.Of(s).Key == scopeKey;

    // Своя нить своего чата: для запуска и сохранения, где чужое молча отбрасывается
    public bool OwnThread(string ownerId, string scopeKey, string? sessionId, string? threadId) =>
        OwnChat(scopeKey, sessionId) && !string.IsNullOrWhiteSpace(threadId)
        && store.Get(ownerId, sessionId!.Trim()).Threads.Any(t => t.Id == threadId.Trim());

    // Взять картинку в работу: новая нить с фокусом и якорем её стопки в ленте; нить по этому
    // файлу уже есть — только фокус, второго якоря нет
    public async Task<ImageThreadWrite> OpenAsync(string ownerId, string projectId, string sessionId,
        string? file, string? draftFolder, long revision, CancellationToken ct)
    {
        var written = Tracked(ownerId, sessionId,
            () => store.Open(ownerId, sessionId, file, draftFolder, revision, NewThreadSettings(ownerId, projectId)), ContextActor.Human);
        if (written.Status == ImageThreadWriteStatus.Ok && written is { Existing: false, Thread: { } thread })
            await AnchorAsync(sessionId, thread, ct);
        return await AfterAsync(ownerId, projectId, sessionId, written);
    }

    public Task<ImageThreadWrite> FocusAsync(string ownerId, string projectId, string sessionId, string? threadId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId,
            Tracked(ownerId, sessionId, () => store.SetFocus(ownerId, sessionId, threadId, revision), ContextActor.Human));

    public Task<ImageThreadWrite> RemoveAsync(string ownerId, string projectId, string sessionId, string threadId, long revision)
    {
        var written = store.Remove(ownerId, sessionId, threadId, revision);
        // Нить исчезла — из контекста чата уходит и она сама, и её референсы
        if (written.Status == ImageThreadWriteStatus.Ok)
            mirror?.Forget(ownerId, sessionId, ChatContext.ImageContextKind.Kind, threadId);
        return AfterAsync(ownerId, projectId, sessionId, written);
    }

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
            Tracked(ownerId, sessionId,
                () => store.SetCurrentVersion(ownerId, sessionId, threadId, versionId, stepId, revision, focus: true),
                ContextActor.Human));

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
            var written = Tracked(ownerId, sessionId, () => store.SetFocus(ownerId, sessionId, threadId, before.Revision), ContextActor.Agent);
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
            var written = Tracked(ownerId, sessionId, () => store.Open(ownerId, sessionId, file, draftFolder, before.Revision,
                NewThreadSettings(ownerId, projectId)), ContextActor.Agent);
            if (written.Status == ImageThreadWriteStatus.Conflict && attempt < AgentAttempts - 1) continue;
            if (written.Status == ImageThreadWriteStatus.Ok && written.State.Revision != before.Revision)
                await FocusLineAsync(sessionId, written.State, before, ct);
            if (written is { Status: ImageThreadWriteStatus.Ok, Existing: false, Thread: { } thread })
                await AnchorAsync(sessionId, thread, ct);
            return await AfterAsync(ownerId, projectId, sessionId, written);
        }
    }

    // Агент позвал local_* напрямую, мимо image_*: картинка легла файлом в проект. Нить по файлу и якорь в
    // ленте дают ту же карточку, что запуск через редактор. Выбор человека не трогаем (Open отдаёт фокус
    // новой нити — возвращаем прежний), тихой строки «взял в работу» нет: Claude картинку не выбирал.
    // Нить по этому файлу уже есть — второго якоря нет
    public async Task AdoptFileAsync(string ownerId, string projectId, string sessionId, string file, CancellationToken ct)
    {
        for (var attempt = 0; attempt < AgentAttempts; attempt++)
        {
            var before = store.Get(ownerId, sessionId);
            var written = store.Open(ownerId, sessionId, file, null, before.Revision, NewThreadSettings(ownerId, projectId));
            if (written.Status == ImageThreadWriteStatus.Conflict) continue;
            if (written is not { Status: ImageThreadWriteStatus.Ok, Thread: { } thread }) return;
            if (!written.Existing) await AnchorAsync(sessionId, thread, ct);
            var state = written.State;
            if (before.Focus is not null && state.Focus != before.Focus
                && store.SetFocus(ownerId, sessionId, before.Focus, state.Revision) is { Status: ImageThreadWriteStatus.Ok } back)
                state = back.State;
            SyncNetFocus(ownerId, sessionId, before.Focus, state.Focus);
            await BroadcastAsync(ownerId, projectId, sessionId, state);
            return;
        }
    }

    // Результат local_* с несколькими картинками (count > 1) — как у кнопки: ОДНА нить-черновик, запуск с
    // якорем image_launch_versions и вариант каждой картинки версией. Повторное усыновление той же задачи
    // (нить с её запуском уже есть) ничего не пишет. Одна картинка или нет рабочей папки шагов — по нити на
    // файл (AdoptFileAsync)
    public async Task AdoptFilesAsync(string ownerId, string projectId, string sessionId, string jobId,
        IReadOnlyList<ProjectImage> images, CancellationToken ct)
    {
        if (images.Count == 0) return;
        if (images.Count == 1 || steps is null)
        {
            foreach (var image in images)
                await AdoptFileAsync(ownerId, projectId, sessionId, image.RelativePath, ct);
            return;
        }
        if (store.Get(ownerId, sessionId).Threads.Any(t => t.Launches.Any(l => l.JobId == jobId))) return;

        var first = images[0].RelativePath;
        var folder = first.Contains('/') ? first[..first.LastIndexOf('/')] : "";
        ImageThreadWrite? opened = null;
        ImageThreadsState before = ImageThreadsState.Empty;
        for (var attempt = 0; attempt < AgentAttempts && opened is not { Status: ImageThreadWriteStatus.Ok }; attempt++)
        {
            before = store.Get(ownerId, sessionId);
            opened = store.Open(ownerId, sessionId, null, folder, before.Revision, NewThreadSettings(ownerId, projectId));
        }
        if (opened is not { Status: ImageThreadWriteStatus.Ok, Thread: { } thread }) return;

        var text = $"Claude получил картинки напрямую из local-media: {images.Count} {ImageEditorStateContributor.Variants(images.Count)}";
        var launched = store.AddLaunch(ownerId, sessionId, thread.Id,
            new ImageThreadLaunch(jobId, null, null, store.Now(), ImageThreadLaunchStatus.Running, SpendInitiators.Agent, null),
            new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Launched, text, thread.Id, jobId));
        if (launched.Status != ImageThreadWriteStatus.Ok) return;

        List<(int Variant, string StepId)> taken = [];
        for (var n = 0; n < images.Count; n++)
        {
            var step = steps.TakeFile(ownerId, projectId, jobId, n + 1, images[n]);
            if (step.Value is { } s) taken.Add((n + 1, s.StepId));
            else log.LogWarning("Редактор картинок: файл {Path} задачи {JobId} не стал версией: {Error}",
                images[n].RelativePath, jobId, step.Error);
        }
        var finished = store.FinishLaunch(ownerId, sessionId, thread.Id, jobId,
            taken.Count == 0 ? ImageThreadLaunchStatus.Failed : ImageThreadLaunchStatus.Done, taken,
            (_, all) => new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Versions,
                all.Count == 0 ? "Картинки local-media не стали версиями"
                    : $"Готово: картинки local-media, {(all.Count == 1 ? $"версия {all[0].Number}" : $"версии {all[0].Number}–{all[^1].Number}")}",
                thread.Id, jobId));
        await RecordAsync(sessionId, RecordTypes.LaunchVersions, $"Claude получил картинки: {images.Count} {ImageEditorStateContributor.Variants(images.Count)}",
            new
            {
                threadId = thread.Id,
                jobId,
                prompt = "local-media",
                provider = "local",
                model = "local-media",
                count = images.Count,
                initiator = SpendInitiators.Agent,
                baseVersionId = (string?)null,
            }, ct);

        var state = finished.Status == ImageThreadWriteStatus.Ok ? finished.State : launched.State;
        // Выбор человека не трогаем: возвращаем, пока ревизию никто не двигал (конфликт — человек выбрал сам)
        if (before.Focus is not null && state.Focus != before.Focus
            && store.SetFocus(ownerId, sessionId, before.Focus, state.Revision) is { Status: ImageThreadWriteStatus.Ok } back)
            state = back.State;
        SyncNetFocus(ownerId, sessionId, before.Focus, state.Focus);
        await BroadcastAsync(ownerId, projectId, sessionId, state);
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

    // Вариант готов, а задача ещё идёт (зовёт исполнитель, событие VariantReady): варианты, ещё не
    // ставшие версиями, ложатся шагами и версиями внизу. Статус запуска и журнал не трогаются —
    // это дело финала. Сбой здесь не роняет исполнителя: финал подберёт вариант ещё раз
    public async Task OnVariantReadyAsync(string ownerId, ImageEditJobDto job)
    {
        if (job is not { ThreadId: { } threadId, ChatSessionId: { } sessionId } || steps is null) return;
        try
        {
            ImageThreadWrite written;
            await _versionsGate.WaitAsync();
            try
            {
                if (RunningLaunch(ownerId, sessionId, threadId, job) is not ({ } thread, { } launch)) return;
                var taken = TakeVariants(ownerId, job, thread, launch);
                if (taken.Count == 0) return;
                written = store.AddLaunchVersions(ownerId, sessionId, threadId, job.JobId, taken);
            }
            finally { _versionsGate.Release(); }
            if (written.Status == ImageThreadWriteStatus.Ok)
                await AfterAsync(ownerId, job.ProjectId, sessionId, written);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Редактор картинок: готовые варианты задачи {JobId} не стали версиями нити {ThreadId}",
                job.JobId, threadId);
        }
    }

    // Задача кончилась (зовёт исполнитель): варианты, ещё не ставшие версиями по ходу, ложатся
    // шагами (байты как есть, родитель — шаг-основа запуска) и становятся версиями внизу — при
    // любом исходе, отмена тоже оставляет готовое. Статус запуска меняется только здесь; Failed —
    // только если у запуска нет ни одной версии. Задача вне нити, чужой чат или запуск, которого
    // нить не знает, — ничего. Сбой здесь не роняет исполнителя: варианты остаются в задаче до TTL
    public async Task OnJobFinishedAsync(string ownerId, ImageEditJobDto job)
    {
        if (job is not { ThreadId: { } threadId, ChatSessionId: { } sessionId }) return;
        try
        {
            ImageThreadWrite written;
            await _versionsGate.WaitAsync();
            try
            {
                if (RunningLaunch(ownerId, sessionId, threadId, job) is not ({ } thread, { } launch)) return;

                var taken = steps is null ? [] : TakeVariants(ownerId, job, thread, launch);
                var versions = thread.Versions.Count(v => v.JobId == job.JobId) + taken.Count;
                var status = job.Status switch
                {
                    ImageEditJobStatus.Cancelled => ImageThreadLaunchStatus.Cancelled,
                    _ when versions == 0 => ImageThreadLaunchStatus.Failed,
                    _ => ImageThreadLaunchStatus.Done,
                };

                written = store.FinishLaunch(ownerId, sessionId, threadId, job.JobId, status, taken,
                    (after, all) => new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Versions,
                        VersionsText(after, launch, all, job), threadId, job.JobId));
            }
            finally { _versionsGate.Release(); }
            if (written.Status == ImageThreadWriteStatus.Ok)
                await AfterAsync(ownerId, job.ProjectId, sessionId, written);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Редактор картинок: варианты задачи {JobId} не стали версиями нити {ThreadId}", job.JobId, threadId);
        }
    }

    // Идущий запуск задачи в нити своего чата; иначе null
    private (ImageThread Thread, ImageThreadLaunch Launch)? RunningLaunch(string ownerId, string sessionId, string threadId,
        ImageEditJobDto job)
    {
        if (directory?.GetById(sessionId) is not { } session || ImageEditScope.Of(session).Key != job.ProjectId) return null;
        var thread = store.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == threadId);
        return thread?.Launches.FirstOrDefault(l => l.JobId == job.JobId) is { Status: ImageThreadLaunchStatus.Running } launch
            ? (thread, launch)
            : null;
    }

    // Варианты задачи, ещё не ставшие версиями, — шагами рабочей папки. Уже взятые пропускаются ДО
    // TakeVariant: иначе в рабочей папке остался бы лишний шаг
    private List<(int Variant, string StepId)> TakeVariants(string ownerId, ImageEditJobDto job, ImageThread thread,
        ImageThreadLaunch launch)
    {
        var taken = new List<(int Variant, string StepId)>();
        foreach (var n in job.Variants)
        {
            if (thread.Versions.Any(v => v.JobId == job.JobId && v.Variant == n)) continue;
            var step = steps!.TakeVariant(ownerId, job.ProjectId, job.JobId, n, launch.BaseStepId, thread.File);
            if (step.Value is { } s) taken.Add((n, s.StepId));
            else log.LogWarning("Редактор картинок: вариант {Variant} задачи {JobId} не стал версией: {Error}",
                n, job.JobId, step.Error);
        }
        return taken;
    }

    // «Готово «синий фон»: версии 3–4 картинки hero.png (от исходника)» / «… не получилось» /
    // «… отменён; готовы версии 3–4». all — все версии запуска, а не только прирост финала
    private static string VersionsText(ImageThread thread, ImageThreadLaunch launch, IReadOnlyList<ImageThreadVersion> all,
        ImageEditJobDto job)
    {
        var what = $"«{launch.Prompt}» в картинку {Name(thread)}";
        var cancelled = job.Status == ImageEditJobStatus.Cancelled;
        if (all.Count == 0)
            return cancelled ? $"Запуск {what} отменён"
                : $"Запуск {what} не получился" + (string.IsNullOrWhiteSpace(job.Error) ? "" : $" ({job.Error})");
        var numbers = all.Count == 1 ? $"версия {all[0].Number}" : $"версии {all[0].Number}–{all[^1].Number}";
        if (cancelled) return $"Запуск {what} отменён; {(all.Count == 1 ? "готова" : "готовы")} {numbers}";
        var from = thread.Version(launch.BaseVersionId) is { } b ? $", от: {ImageThread.Label(b)}" : "";
        var current = thread.CurrentVersion is { } c && all.Contains(c) ? $"; в работе {ImageThread.Label(c)}" : "";
        return $"Готово {what}: {numbers}{from}{current}";
    }

    // Варианты запуска становятся версиями, как только исполнитель их скачал (VariantReady), хвост и
    // статус — по завершению (Finished): подписку ставит регистрация модуля (ImageEditorSubsystem), в
    // тестах — сам тест
    public void Watch(ImageEditJobService jobs)
    {
        jobs.VariantReady += OnVariantReadyAsync;
        jobs.Finished += OnJobFinishedAsync;
    }

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
    public const string InterruptedPartialText = "Запуск прерван перезапуском сервера";

    // После перезапуска сервера (ADR-019): реестр задач живёт в памяти, и PendingJobId, которого
    // в нём нет, не дождётся ни вариантов, ни отказа — карточка висела бы в «Рисуем…». Такая
    // задача снимается с нити с пометкой InterruptedJobId и записью журнала для хода, владельцу
    // уходит image_thread_changed. Варианты, ставшие версиями по ходу (VariantReady), остаются —
    // текст тогда называет их; не дорисованные и у старых нитей ждавшие «Взять» потеряны вместе с
    // реестром. Трату не трогает: поставщик принял задачу до перезапуска, и
    // «не списано» от него уже не придёт (инвариант учёта — отмена после принятия трату не отменяет)
    public async Task<int> RecoverInterruptedAsync(IImageEditJobs? jobs, CancellationToken ct)
    {
        var dropped = 0;
        foreach (var (ownerId, sessionId) in store.Chats())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Чата уже нет — задачи считаются мёртвыми, рассылать некому
                var scopeKey = directory?.GetById(sessionId) is { } session ? ImageEditScope.Of(session).Key : null;
                var state = store.DropDeadPending(ownerId, sessionId,
                    jobId => scopeKey is not null && jobs?.Get(ownerId, scopeKey, jobId) is not null,
                    (thread, jobId) => new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Interrupted,
                        InterruptedEventText(thread, jobId), thread.Id, jobId));
                if (state is null) continue;
                dropped++;
                if (scopeKey is not null) await BroadcastAsync(ownerId, scopeKey, sessionId, state);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Редактор картинок: прерванные задачи нитей чата {SessionId} не сняты", sessionId);
            }
        }
        return dropped;
    }

    // «…: картинка hero.png, часть вариантов сохранена (версии 3–4), остальные не дорисованы — запусти заново»
    private static string InterruptedEventText(ImageThread thread, string jobId)
    {
        var kept = thread.Versions.Where(v => v.JobId == jobId).ToList();
        if (kept.Count == 0)
            return $"{InterruptedText}: картинка {Name(thread)}, варианты недоступны — запусти заново";
        var numbers = kept.Count == 1 ? $"версия {kept[0].Number}" : $"версии {kept[0].Number}–{kept[^1].Number}";
        return $"{InterruptedPartialText}: картинка {Name(thread)}, часть вариантов сохранена ({numbers}), " +
               "остальные не дорисованы — запусти заново";
    }

    public async Task BroadcastAsync(string ownerId, string projectId, string sessionId, ImageThreadsState state)
    {
        if (broadcaster is null) return;
        try
        {
            await broadcaster.ToOwner(ownerId, new ImageThreadChangedMessage(projectId, state.Revision, Project(ownerId, sessionId, state)) { SessionId = sessionId });
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
        return written.Status == ImageThreadWriteStatus.Ok ? written with { State = Project(ownerId, sessionId, written.State) } : written;
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
