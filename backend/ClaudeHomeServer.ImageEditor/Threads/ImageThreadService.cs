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
// Взять вариант, откатиться и сохранить в проект может только человек (решение Андрея 1): эти
// методы зовут только ручки, тулсет агента их не видит.
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
    ImageEditSteps? steps = null)
{
    public const string ModuleKey = "imageeditor";

    public static class RecordTypes
    {
        // Якорь стопки: data { threadId, stackId }, содержимое карточки рисуется из хранилища
        public const string Thread = "image_thread";
        // Тихие строки: data { threadId, … }
        public const string Launch = "image_launch";
        public const string Saved = "image_saved";
        public const string StackForked = "image_stack_forked";
    }

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
        var written = store.Open(ownerId, sessionId, file, draftFolder, revision);
        if (written.Status == ImageThreadWriteStatus.Ok && written is { Existing: false, Thread: { } thread })
            await AnchorAsync(sessionId, thread, thread.CurrentStackId!, ct);
        return await AfterAsync(ownerId, projectId, sessionId, written);
    }

    public Task<ImageThreadWrite> FocusAsync(string ownerId, string projectId, string sessionId, string? threadId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.SetFocus(ownerId, sessionId, threadId, revision));

    public Task<ImageThreadWrite> RemoveAsync(string ownerId, string projectId, string sessionId, string threadId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.Remove(ownerId, sessionId, threadId, revision));

    public Task<ImageThreadWrite> RollbackAsync(string ownerId, string projectId, string sessionId, string threadId,
        string? stepId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.Rollback(ownerId, sessionId, threadId, stepId, revision));

    public Task<ImageThreadWrite> DismissAsync(string ownerId, string projectId, string sessionId, string threadId,
        string jobId, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.Dismiss(ownerId, sessionId, threadId, jobId, revision));

    public Task<ImageThreadWrite> SetSettingsAsync(string ownerId, string projectId, string sessionId, string threadId,
        ImageThreadSettings settings, long revision) =>
        AfterAsync(ownerId, projectId, sessionId, store.SetSettings(ownerId, sessionId, threadId, settings, revision));

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
            await AnchorAsync(sessionId, after, fork.NewStack.StackId, ct);
            await RecordAsync(sessionId, RecordTypes.StackForked, ForkText(fork),
                new { threadId, stackId = fork.NewStack.StackId, oldStackId = fork.FrozenStack.StackId }, ct);
        }
        return new ImageThreadTake(await AfterAsync(ownerId, projectId, sessionId, written));
    }

    // Задача запущена в нить: её варианты ждут выбора, ход узнаёт о запуске из журнала. Ручной
    // запуск ещё и тихой строкой в ленте — модель её не видит, это след для человека
    public async Task OnLaunchedAsync(string ownerId, string projectId, string sessionId, string threadId,
        ImageEditJobDto? job, string jobId, string prompt, ImageEditInitiator initiator, CancellationToken ct)
    {
        try
        {
            var model = job?.Model ?? "модель по котировке";
            var count = job?.Count ?? 0;
            var variants = count > 0 ? $" · {count} {ImageEditorStateContributor.Variants(count)}" : "";
            var who = initiator == ImageEditInitiator.Agent ? "Ты запустил" : "Человек запустил вручную";
            var written = store.SetPending(ownerId, sessionId, threadId, jobId,
                new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Launched,
                    $"{who}: «{prompt.Trim()}» · {model}{variants}{EstimateText(job?.Estimate)}", threadId, jobId));
            if (written.Status != ImageThreadWriteStatus.Ok) return;

            if (initiator == ImageEditInitiator.Human)
                await RecordAsync(sessionId, RecordTypes.Launch, $"Вы запустили: «{prompt.Trim()}» · {model}{variants}",
                    new
                    {
                        threadId,
                        jobId,
                        prompt,
                        provider = job?.Provider,
                        model = job?.Model,
                        count,
                        estimate = job?.Estimate,
                    }, ct);
            await AfterAsync(ownerId, projectId, sessionId, written);
        }
        catch (Exception ex)
        {
            // Задача уже идёт: сбой следа не должен превращать 202 в ошибку
            log.LogWarning(ex, "Редактор картинок: запуск {JobId} не записан в нить {ThreadId}", jobId, threadId);
        }
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

    private Task AnchorAsync(string sessionId, ImageThread thread, string stackId, CancellationToken ct) =>
        RecordAsync(sessionId, RecordTypes.Thread, $"Картинка: {Name(thread)}", new { threadId = thread.Id, stackId }, ct);

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
