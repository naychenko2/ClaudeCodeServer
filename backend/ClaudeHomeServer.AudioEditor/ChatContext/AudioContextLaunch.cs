using System.Text.Json.Nodes;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.AudioEditor.ChatContext;

// Результат сверки ревизии: либо состояние стора на этой ревизии, либо отказ. Ревизия не совпала —
// ErrorCode = context_changed и свежий DTO (ADR-023 §Д2.1, тело 409)
public sealed record AudioContextRead(
    ChatContextState? State, string? ErrorCode = null, string? Error = null, ChatContextDto? Fresh = null)
{
    public bool Ok => State is not null;

    public AudioEditCallResult<T> Fail<T>() => new(default, ErrorCode, Error) { Context = Fresh };
}

// Входы операции, разложенные из контекста чата (ADR-023 §Д2.1): основной объект → нить и версия;
// audio-voice → голос; reference → образец (версия нити или файл проекта); piece → куски склейки по
// AddedAt. Референс, которого операция не берёт (AcceptedRefs по её имени), пропускается. VoiceKind
// выводится из голоса: образцы — клон, обученная модель — RVC
public sealed record AudioContextInputs(
    ChatContextState State,
    string? ThreadId,
    string? VersionId,
    string? VoiceSlug,
    AudioVoiceKind? VoiceKind,
    AudioConcatPiece? Reference,
    IReadOnlyList<AudioConcatPiece> Pieces)
{
    public string? VoiceRef => VoiceSlug is null ? null : AudioVoiceRefs.Prefix + VoiceSlug;
}

// Запуск звука по ревизии контекста чата (КТ-3): единственная точка, где вход запуска берётся из стора, а
// не из тела запроса. Котировка, запуск, сведение и склейка зовут её одинаково; агент (без ревизии) —
// только за голосом контекста. Без стора (тесты без DI, модуль без спины) ревизия — отказ, а не молчаливо
// старое поведение: клиент, приславший её, ждёт входов из контекста
public sealed class AudioContextLaunch(
    IChatContextStore? store = null,
    ContextKindRegistry? registry = null,
    ISessionDirectory? directory = null)
{
    public const string UnavailableText = "Контекст чата на этом сервере недоступен";
    public const string StaleText = "Контекст чата изменился — перечитайте его";
    public const string StaleQuoteText = "Котировка выписана на другую ревизию контекста — запросите цену заново";
    public const string NoSessionText = "Для запуска по ревизии контекста нужен чат (sessionId)";

    // Сверить ревизию клиента со стором. Чат обязан быть из области запроса: чужой — как несуществующий
    public AudioContextRead Read(string ownerId, AudioEditScope scope, string? sessionId, long revision)
    {
        if (store is null || registry is null) return new(null, AudioEditErrorCodes.Unavailable, UnavailableText);
        if (string.IsNullOrWhiteSpace(sessionId)) return new(null, AudioEditErrorCodes.InvalidRequest, NoSessionText);
        var id = sessionId.Trim();
        Models.Session? session = null;
        if (directory is not null
            && ((session = directory.GetById(id)) is null || AudioEditScope.Of(session).Key != scope.Key))
            return new(null, AudioEditErrorCodes.ChatNotFound, "Чат не найден");
        var state = store.Get(ownerId, id);
        return state.Revision == revision ? new(state) : Stale(ownerId, scope, id, session, state);
    }

    // Свежий DTO для тела 409 (ревизия котировки не совпала с запуском и т. п.)
    public AudioContextRead Stale(string ownerId, AudioEditScope scope, string sessionId)
    {
        if (store is null || registry is null) return new(null, AudioEditErrorCodes.Unavailable, UnavailableText);
        var id = sessionId.Trim();
        return Stale(ownerId, scope, id, directory?.GetById(id), store.Get(ownerId, id));
    }

    private AudioContextRead Stale(string ownerId, AudioEditScope scope, string sessionId, Models.Session? session,
        ChatContextState state)
    {
        session ??= directory?.GetById(sessionId);
        if (session is null || registry is null)
            return new(null, AudioEditErrorCodes.ContextChanged, StaleText);
        var dto = ChatContextDtoBuilder.Build(registry, new ContextScope(ownerId, session, scope.Project), state);
        return new(null, AudioEditErrorCodes.ContextChanged, StaleText, dto);
    }

    // Разложить состояние по входам операции op (имя операции, null — только основной объект)
    public static AudioContextInputs Extract(AudioEditScope scope, ChatContextState state, string? op)
    {
        string? threadId = null, versionId = null;
        if (state.Primary is { Kind: AudioContextKind.Kind } primary)
        {
            threadId = Text(primary.Ref, "threadId");
            versionId = Text(primary.Ref, "versionId");
        }

        var accepted = op is null ? [] : AudioContextKind.RolesOf(op);
        // Роль и вид должны совпасть с таблицей операции — иначе референс для неё не существует
        IEnumerable<ContextItem> Of(string role) => state.Refs
            .Where(r => r.Role == role && accepted.Any(a => a.Role == role && a.Kinds.Contains(r.Kind)))
            .OrderBy(r => r.AddedAt);

        string? slug = null;
        AudioVoiceKind? voiceKind = null;
        if (Of(AudioContextRoles.Voice).FirstOrDefault(r => r.Kind == AudioContextKind.VoiceKind) is { } voice
            && Text(voice.Ref, "slug") is { } found)
        {
            slug = found;
            var manifest = scope.Project is { } project && VoiceStore.IsValidSlug(found)
                ? VoiceStore.Get(project.RootPath, found) : null;
            voiceKind = manifest?.Kind == VoiceKinds.Rvc ? AudioVoiceKind.Rvc : AudioVoiceKind.Clone;
        }

        var reference = Of(AudioContextRoles.Reference).Select(Piece).FirstOrDefault(p => p is not null);
        var pieces = Of(AudioContextRoles.Piece).Select(Piece).Where(p => p is not null).Select(p => p!).ToList();
        return new AudioContextInputs(state, threadId, versionId, slug, voiceKind, reference, pieces);
    }

    // Голос контекста для агента: audio_generate без voice берёт его (явный аргумент заменяет). Операция
    // голос не берёт — null
    public string? AgentVoice(string ownerId, string sessionId, AudioOp op)
    {
        if (store is null) return null;
        return Extract(new AudioEditScope(AudioEditScope.Personal, null), store.Get(ownerId, sessionId),
            AudioEditJobService.OpName(op)).VoiceRef;
    }

    private static AudioConcatPiece? Piece(ContextItem item) => item.Kind switch
    {
        AudioContextKind.Kind when Text(item.Ref, "threadId") is { } thread =>
            new AudioConcatPiece(thread, Text(item.Ref, "versionId")),
        ProjectFileContextKind.Kind when Text(item.Ref, "path") is { } path => new AudioConcatPiece(ProjectFile: path),
        _ => null,
    };

    private static string? Text(JsonObject reference, string name) =>
        reference[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
