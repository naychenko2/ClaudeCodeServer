using System.Text.Json.Nodes;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;

namespace ClaudeHomeServer.Services.VideoEditor.ChatContext;

// Результат сверки ревизии: либо состояние стора на этой ревизии, либо отказ. Ревизия не совпала —
// ErrorCode = context_changed и свежий DTO (ADR-023 §Д2.1, тело 409)
public sealed record VideoContextRead(
    ChatContextState? State, string? ErrorCode = null, string? Error = null, ChatContextDto? Fresh = null)
{
    public bool Ok => State is not null;

    public VideoEditCallResult<T> Fail<T>() => new(default, ErrorCode, Error) { Context = Fresh };
}

// Входы съёмки и сборки, разложенные из контекста чата (ADR-023 §Д2.1): основной video-scene → сцена,
// основной video-film → фильм, референсы frame-a/frame-b → кадры сцены. Problem — вход годится не для
// всякого запуска (кадр из «Картинок» без версии)
public sealed record VideoContextInputs(
    ChatContextState State,
    string? SceneId,
    string? FilmPath,
    FrameRef? FrameA,
    FrameRef? FrameB,
    string? Problem);

// Запуск «Видео» по ревизии контекста чата (КТ-5): единственная точка, где вход съёмки и сборки берётся из
// стора, а не из тела запроса. Котировка, запуск и сборка фильма зовут её одинаково. Без стора (тесты без DI,
// модуль без спины) ревизия — отказ, а не молчаливо старое поведение
public sealed class VideoContextLaunch(
    IChatContextStore? store = null,
    ContextKindRegistry? registry = null,
    ISessionDirectory? directory = null)
{
    public const string UnavailableText = "Контекст чата на этом сервере недоступен";
    public const string StaleText = "Контекст чата изменился — перечитайте его";
    public const string StaleQuoteText = "Котировка выписана на другую ревизию контекста — запросите цену заново";
    public const string NoSessionText = "Для запуска по ревизии контекста нужен чат (sessionId)";
    public const string NotSceneText = "Основной объект контекста — не сцена";
    public const string NotFilmText = "Основной объект контекста — не этот фильм";

    // Сверить ревизию клиента со стором. Чат обязан быть из области запроса: чужой — как несуществующий
    public VideoContextRead Read(string ownerId, VideoEditScope scope, string? sessionId, long revision)
    {
        if (store is null || registry is null) return new(null, VideoEditorErrors.ProviderUnavailable, UnavailableText);
        if (string.IsNullOrWhiteSpace(sessionId)) return new(null, VideoEditorErrors.InvalidRequest, NoSessionText);
        var id = sessionId.Trim();
        Models.Session? session = null;
        if (directory is not null
            && ((session = directory.GetById(id)) is null || VideoEditScope.Of(session).Key != scope.Key))
            return new(null, VideoEditorErrors.ChatNotFound, "Чат не найден");
        var state = store.Get(ownerId, id);
        return state.Revision == revision ? new(state) : Stale(ownerId, scope, id, session, state);
    }

    // Свежий DTO для тела 409 (ревизия котировки не совпала с запуском и т. п.)
    public VideoContextRead Stale(string ownerId, VideoEditScope scope, string sessionId)
    {
        if (store is null || registry is null) return new(null, VideoEditorErrors.ProviderUnavailable, UnavailableText);
        var id = sessionId.Trim();
        return Stale(ownerId, scope, id, directory?.GetById(id), store.Get(ownerId, id));
    }

    private VideoContextRead Stale(string ownerId, VideoEditScope scope, string sessionId, Models.Session? session,
        ChatContextState state)
    {
        session ??= directory?.GetById(sessionId);
        if (session is null || registry is null)
            return new(null, VideoEditorErrors.ContextChanged, StaleText);
        var dto = ChatContextDtoBuilder.Build(registry, new ContextScope(ownerId, session, scope.Project), state);
        return new(null, VideoEditorErrors.ContextChanged, StaleText, dto);
    }

    // Разложить состояние: сцена или фильм — основной объект; кадр роли — самый поздно добавленный из подходящих
    // по таблице сцены (вид и роль должны совпасть, иначе референса для съёмки не существует)
    public static VideoContextInputs Extract(ChatContextState state)
    {
        string? sceneId = null, filmPath = null;
        if (state.Primary is { Kind: VideoContextKind.SceneKind } scene) sceneId = Text(scene.Ref, VideoContextKind.SceneKey);
        else if (state.Primary is { Kind: VideoContextKind.FilmKind } film) filmPath = Text(film.Ref, VideoContextKind.FilmKey);

        string? problem = null;
        FrameRef? Frame(string role)
        {
            if (sceneId is null) return null;
            var spec = VideoContextKind.RolesOf().First(r => r.Role == role);
            var item = state.Refs.Where(r => r.Role == role && spec.Kinds.Contains(r.Kind))
                .OrderByDescending(r => r.AddedAt).FirstOrDefault();
            if (item is null) return null;
            switch (item.Kind)
            {
                case ProjectFileContextKind.Kind when Text(item.Ref, "path") is { } path:
                    return FrameRef.File(path);
                case "image" when Text(item.Ref, "threadId") is { } thread && Text(item.Ref, "versionId") is { } version:
                    return FrameRef.Image(thread, version);
                case "image":
                    problem ??= "У кадра из «Картинок» не указана версия — выберите версию картинки";
                    return null;
                default:
                    return null;
            }
        }

        var a = Frame(VideoContextRoles.FrameA);
        var b = Frame(VideoContextRoles.FrameB);
        return new VideoContextInputs(state, sceneId, filmPath, a, b, problem);
    }

    private static string? Text(JsonObject reference, string name) =>
        reference[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
