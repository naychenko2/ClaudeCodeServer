using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.AudioEditor.Jobs;

// Следы задачи звука в нити чата (ADR-021 §2, как ImageThreadService у картинок): запуск пишется в
// нить, внизу ленты — якорь запуска (module_record с module "audioeditor"), итог задачи — новые версии
// нити; каждая запись нити уходит владельцу событием audio_thread_changed. Чужой чат или нить молча
// отбрасываются — задача идёт без нити, ответ не выдаёт существование чужого. Сбой следа задачу не
// роняет: она уже идёт или уже кончилась.
public sealed class AudioJobThreads(
    AudioThreadStore store,
    ILogger<AudioJobThreads> log,
    ISessionDirectory? directory = null,
    IChatFeed? feed = null,
    ISessionBroadcaster? broadcaster = null)
{
    public const string ModuleKey = "audioeditor";

    public static class RecordTypes
    {
        // Якорь нити — карточка звука: data { threadId, versionId }
        public const string Thread = "audio_thread";
        // Якорь запуска внизу ленты: data { threadId, jobId, mode, op, provider, model, count, price,
        // license, initiator, baseVersionId }. Версии запуска — версии нити с этим jobId
        public const string LaunchVersions = "audio_launch_versions";
    }

    public AudioThreadStore Store => store;

    // Своя нить чата своей области; без справочника чатов (тесты без DI) — только по хранилищу владельца
    public bool OwnThread(string ownerId, string scopeKey, string? sessionId, string? threadId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(threadId)) return false;
        if (directory is not null
            && (directory.GetById(sessionId) is not { } session || AudioEditScope.Of(session).Key != scopeKey))
            return false;
        return store.Get(ownerId, sessionId).Threads.Any(t => t.Id == threadId);
    }

    // Свой чат своей области; без справочника чатов (тесты без DI) — любой непустой id: нити всё равно
    // ложатся в хранилище этого владельца
    public bool OwnChat(string ownerId, string scopeKey, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (directory is null) return true;
        return directory.GetById(sessionId) is { } session
            && AudioEditScope.Of(session).Key == scopeKey
            && directory.ResolveOwnerId(session) == ownerId;
    }

    // Якорь нити в ленте — зовут ручки при заведении нити
    public Task AnchorAsync(string sessionId, AudioThread thread, CancellationToken ct) =>
        RecordAsync(sessionId, RecordTypes.Thread, $"Звук: {Name(thread)}",
            new { threadId = thread.Id, versionId = thread.CurrentVersionId }, ct);

    // Задача принята: запуск в нити с лицензией модели на этот момент, последние настройки нити,
    // якорь запуска в ленте и журнал для хода
    public async Task OnLaunchedAsync(string ownerId, string scopeKey, string sessionId, string threadId, string jobId,
        AudioQuoteDto quote, string? prompt, string? baseVersionId, AudioEditInitiator initiator,
        AudioThreadSettings settings, CancellationToken ct)
    {
        try
        {
            var agent = initiator == AudioEditInitiator.Agent;
            var who = agent ? SpendInitiators.Agent : SpendInitiators.Human;
            var what = string.IsNullOrWhiteSpace(prompt) ? quote.Op.ToString() : $"«{prompt.Trim()}»";
            store.SetSettings(ownerId, sessionId, threadId, settings, null);
            var written = store.AddLaunch(ownerId, sessionId, threadId,
                new AudioThreadLaunch(jobId, baseVersionId, store.Now(), AudioThreadLaunchStatus.Running, who,
                    prompt?.Trim(), quote.License),
                new AudioThreadEvent(store.Now(), AudioThreadEventKinds.Launched,
                    $"{(agent ? "Ты запустил" : "Человек запустил вручную")}: {what} · {quote.Provider}/{quote.Model} · " +
                    $"вариантов: {quote.Count}", threadId, jobId));
            if (written.Status != AudioThreadWriteStatus.Ok) return;

            await RecordAsync(sessionId, RecordTypes.LaunchVersions,
                $"{(agent ? "Claude запустил" : "Вы запустили")}: {what} · {quote.Model}",
                new
                {
                    threadId,
                    jobId,
                    mode = quote.Mode,
                    op = quote.Op,
                    provider = quote.Provider,
                    model = quote.Model,
                    count = quote.Count,
                    price = quote.Price,
                    license = quote.License,
                    initiator = who,
                    baseVersionId,
                }, ct);
            await BroadcastAsync(ownerId, scopeKey, sessionId, written.State);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Звук: запуск {JobId} не записан в нить {ThreadId}", jobId, threadId);
        }
    }

    // Задача кончилась: каждый готовый вариант — новая версия нити с файлами по ролям и лицензией
    // запуска; статус запуска — по итогу. Повтор ничего не дублирует (FinishLaunch идемпотентен)
    public async Task OnFinishedAsync(string ownerId, string scopeKey, string sessionId, string threadId, string jobId,
        AudioEditJobStatus status, IReadOnlyList<AudioJobVariantDto> variants, string? error)
    {
        try
        {
            var launchStatus = status switch
            {
                AudioEditJobStatus.Cancelled => AudioThreadLaunchStatus.Cancelled,
                _ when variants.Count == 0 => AudioThreadLaunchStatus.Failed,
                _ => AudioThreadLaunchStatus.Done,
            };
            var text = launchStatus switch
            {
                AudioThreadLaunchStatus.Cancelled => $"Запуск {jobId} отменён" + (variants.Count > 0 ? $", готово вариантов: {variants.Count}" : ""),
                AudioThreadLaunchStatus.Failed => "Запуск не получился" + (string.IsNullOrWhiteSpace(error) ? "" : $" ({error})"),
                _ => $"Готово: новых версий {variants.Count}",
            };
            var written = store.FinishLaunch(ownerId, sessionId, threadId, jobId, launchStatus,
                [.. variants.Select(v => (v.Variant, v.Files))],
                new AudioThreadEvent(store.Now(), AudioThreadEventKinds.Versions, text, threadId, jobId));
            if (written.Status == AudioThreadWriteStatus.Ok)
                await BroadcastAsync(ownerId, scopeKey, sessionId, written.State);
            else
                log.LogWarning("Звук: итог задачи {JobId} не лёг в нить {ThreadId}: {Status}", jobId, threadId, written.Status);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Звук: итог задачи {JobId} не записан в нить {ThreadId}", jobId, threadId);
        }
    }

    public async Task BroadcastAsync(string ownerId, string scopeKey, string sessionId, AudioThreadsState state)
    {
        if (broadcaster is null) return;
        try
        {
            await broadcaster.ToOwner(ownerId, new AudioThreadChangedMessage(scopeKey, state.Revision, state) { SessionId = sessionId });
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Звук: нити чата {SessionId} не разосланы", sessionId);
        }
    }

    private async Task RecordAsync(string sessionId, string recordType, string fallback, object data, CancellationToken ct)
    {
        if (feed is null) return;
        try
        {
            await feed.AppendRecordAsync(sessionId, new StoredModuleRecord
            {
                Module = ModuleKey,
                RecordType = recordType,
                Data = JsonSerializer.SerializeToElement(data, AudioThreadStore.Json),
                Fallback = fallback,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Звук: запись {RecordType} не легла в ленту чата {SessionId}", recordType, sessionId);
        }
    }

    public static string Name(AudioThread thread) =>
        thread.File is { Length: > 0 } file ? file : thread.Name is { Length: > 0 } name ? name : "новый звук";
}
