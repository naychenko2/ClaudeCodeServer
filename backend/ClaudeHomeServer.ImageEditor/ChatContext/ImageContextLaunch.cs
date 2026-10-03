using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ImageEditor.ChatContext;

// Раскладка контекста чата по входам запуска картинки (ADR-023 §Д2.1, Дополнение 3): основной объект →
// нить и версия, референсы по ролям → образцы, путь файла проекта или персонаж. Роль принимает
// операция основного объекта (AcceptedRefs владельца), референс, который она не берёт, пропускается.
// Это единственная точка раскладки: ручки человека (по ревизии) и агент (без ревизии, из текущего
// состояния) идут через неё, иначе два пути разошлись бы в том, какой референс на что годится.
public sealed class ImageContextLaunch(
    IChatContextStore store,
    ContextKindRegistry registry,
    ISessionDirectory directory,
    ImageThreadStore threads,
    ImageEditSteps? steps = null,
    ImageEditWorkspace? workspace = null)
{
    // Вход запуска из контекста. Samples — образцы, которые надо прочитать (картинка нити или загрузка);
    // ReferencePaths — файлы проекта; ThreadId == null — основного объекта-картинки в контексте нет
    public sealed record Inputs(
        string? ThreadId,
        string? VersionId,
        IReadOnlyList<(ContextItem Item, ReferenceRole Role)> Samples,
        IReadOnlyList<(string Path, ReferenceRole Role)> ReferencePaths,
        string? CharacterSlug,
        bool BaseHasImage)
    {
        public int ReferenceCount => Samples.Count + ReferencePaths.Count;
    }

    // Запись по ревизии клиента: устарела — context_changed со свежим DTO; чат чужой или не этой области — 404
    public ImageEditCallResult<(Inputs Inputs, ContextScope Scope, string SessionId)> Resolve(
        string ownerId, ImageEditScope scope, string? sessionId, long revision, ImageEditOp op)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Fail(ImageEditErrorCodes.InvalidRequest, "Не указан чат: запуск по ревизии идёт в чат контекста");
        var id = sessionId.Trim();
        if (directory.GetById(id) is not { } session
            || directory.ResolveOwnerId(session) != ownerId
            || (session.ProjectId ?? ImageEditScope.Personal) != scope.Key)
            return Fail(ImageEditErrorCodes.ChatNotFound, "Чат не найден");

        var ctx = new ContextScope(ownerId, session, scope.Project);
        var state = store.Get(ownerId, id);
        if (state.Revision != revision)
            return ImageEditCallResult<(Inputs, ContextScope, string)>.Fail(
                ImageEditErrorCodes.ContextChanged, "Контекст чата изменился", Conflict(ctx, state));

        if (state.Primary is not { Kind: ImageContextKind.Kind } primary || Text(primary.Ref, "threadId") is not { } threadId)
            return Fail(ImageEditErrorCodes.InvalidRequest, "В контексте чата нет основной картинки");
        var inputs = Layout(ctx, state, op);
        var thread = threads.Get(ownerId, id).Threads.FirstOrDefault(t => t.Id == threadId);
        if (thread is null) return Fail(ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате");
        return ImageEditCallResult<(Inputs, ContextScope, string)>.Ok((inputs, ctx, id));
    }

    // Свежее состояние контекста телом отказа: сверка у запуска сработала не на ревизии клиента, а на ревизии котировки
    public ImageEditCallResult<T> Stale<T>(ContextScope ctx, string sessionId) =>
        ImageEditCallResult<T>.Fail(ImageEditErrorCodes.ContextChanged, "Контекст чата изменился",
            Conflict(ctx, store.Get(ctx.OwnerId, sessionId)));

    // Раскладка без сверки ревизии: так читает агент (стор пишет без ревизии) и так же раскладывает Resolve
    public Inputs Layout(ContextScope ctx, ChatContextState state, ImageEditOp op)
    {
        string? threadId = null, versionId = null;
        var hasImage = false;
        var samples = new List<(ContextItem, ReferenceRole)>();
        var paths = new List<(string, ReferenceRole)>();
        string? character = null;

        if (state.Primary is { Kind: ImageContextKind.Kind } primary && registry.Find(primary.Kind) is { } owner)
        {
            threadId = Text(primary.Ref, "threadId");
            versionId = Text(primary.Ref, "versionId");
            if (threadId is not null
                && threads.Get(ctx.OwnerId, ctx.Session.Id).Threads.FirstOrDefault(t => t.Id == threadId) is { } thread)
            {
                var version = versionId is null ? thread.CurrentVersion : thread.Version(versionId);
                hasImage = version is not null && (thread.ImageStepOf(version) is { Length: > 0 } || thread.File is { Length: > 0 });
            }

            var accepted = owner.AcceptedRefs(ctx, primary, OpName(op));
            foreach (var item in state.Refs)
            {
                if (!accepted.Any(a => a.Role == item.Role && a.Kinds.Contains(item.Kind))) continue;
                switch (item.Kind)
                {
                    case ImageContextKind.Kind:
                        samples.Add((item, RoleOf(item.Role)));
                        break;
                    case ProjectFileContextKind.Kind when Text(item.Ref, "path") is { } path:
                        paths.Add((path, RoleOf(item.Role)));
                        break;
                    case ImageContextKind.CharacterKind when character is null && Text(item.Ref, "slug") is { } slug:
                        character = slug;
                        break;
                }
            }
        }
        return new Inputs(threadId, versionId, samples, paths, character, hasImage);
    }

    // Образец с диска человека → рабочая папка модуля (ref {upload})
    public string SaveUpload(string ownerId, byte[] bytes) =>
        workspace?.SaveUpload(ownerId, bytes) ?? throw new InvalidOperationException("Рабочая папка редактора не подключена");

    // Текущее состояние чата — для агента, у которого ревизии нет
    public ChatContextState Current(string ownerId, string sessionId) => store.Get(ownerId, sessionId);

    // Картинка версии: шаг истории, иначе файл нити в проекте; null — картинки ещё нет (черновик)
    public async Task<ImageEditCallResult<ImageBytes?>> ReadVersionAsync(
        string ownerId, ImageEditScope scope, ImageThread thread, ImageThreadVersion version, CancellationToken ct)
    {
        if (thread.ImageStepOf(version) is { Length: > 0 } stepId && steps?.Open(ownerId, scope.Key, stepId) is { } step)
            return ImageEditCallResult<ImageBytes?>.Ok(new ImageBytes(step.Image.Bytes, step.Image.ContentType));
        if (thread.File is not { Length: > 0 } file) return ImageEditCallResult<ImageBytes?>.Ok(null);
        if (scope.Project is not { } project)
            return ImageEditCallResult<ImageBytes?>.Fail(ImageEditErrorCodes.InvalidRequest, "У чата вне проекта нет файлов проекта");
        var read = await ImageEditLaunchAssembler.ReadProjectImageAsync(project.RootPath, file, "Файл картинки", ct);
        return read.Value is { } image
            ? ImageEditCallResult<ImageBytes?>.Ok(image)
            : ImageEditCallResult<ImageBytes?>.Fail(read.ErrorCode ?? ImageEditErrorCodes.InvalidRequest, read.Error ?? "Файл не прочитан");
    }

    // Размер картинки основной версии для котировки (цена по мегапикселям); null — картинки ещё нет
    public async Task<(int Width, int Height)?> BaseSizeAsync(
        string ownerId, ImageEditScope scope, string sessionId, Inputs inputs, CancellationToken ct)
    {
        var thread = threads.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == inputs.ThreadId);
        if (thread is null) return null;
        var version = inputs.VersionId is { } v ? thread.Version(v) : thread.CurrentVersion;
        if (version is null) return null;
        var read = await ReadVersionAsync(ownerId, scope, thread, version, ct);
        return read.Value is { } image ? ImageDimensions.Read(image.Bytes) : null;
    }

    // Образцы раскладки байтами: картинка версии нити этого чата или образец, загруженный в рабочую папку
    public async Task<ImageEditCallResult<IReadOnlyList<ReferenceImage>>> LoadSamplesAsync(
        string ownerId, ImageEditScope scope, string sessionId, Inputs inputs, CancellationToken ct)
    {
        var list = new List<ReferenceImage>();
        foreach (var (item, role) in inputs.Samples)
        {
            if (Text(item.Ref, "upload") is { } upload)
            {
                if (workspace?.OpenUpload(ownerId, upload) is not { } sample)
                    return SampleFail("Образец контекста не найден или истёк");
                list.Add(new ReferenceImage(sample.Bytes, sample.ContentType, role, "sample"));
                continue;
            }
            if (Text(item.Ref, "threadId") is not { } threadId
                || threads.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == threadId) is not { } thread)
                return SampleFail("Картинка-образец не найдена в этом чате");
            var version = Text(item.Ref, "versionId") is { } v ? thread.Version(v) : thread.CurrentVersion;
            if (version is null) return SampleFail("У картинки-образца нет такой версии");
            var read = await ReadVersionAsync(ownerId, scope, thread, version, ct);
            if (read.Value is not { } image) return SampleFail(read.Error ?? "У картинки-образца ещё нет изображения");
            list.Add(new ReferenceImage(image.Bytes, image.ContentType, role, ImageThreadService.Name(thread)));
        }
        return ImageEditCallResult<IReadOnlyList<ReferenceImage>>.Ok(list);
    }

    private ChatContextConflictDto Conflict(ContextScope ctx, ChatContextState state) =>
        new(ChatContextErrors.ContextChanged, ChatContextDtoBuilder.Build(registry, ctx, state));

    // face у контекста — «Лицо», драйверы картинки знают три роли: лицо идёт образцом объекта
    private static ReferenceRole RoleOf(string? role) => role switch
    {
        ImageContextRoles.Style => ReferenceRole.Style,
        ImageContextRoles.Character => ReferenceRole.Character,
        _ => ReferenceRole.Object,
    };

    public static string OpName(ImageEditOp op) => JsonNamingPolicy.CamelCase.ConvertName(op.ToString());

    private static ImageEditCallResult<IReadOnlyList<ReferenceImage>> SampleFail(string error) =>
        ImageEditCallResult<IReadOnlyList<ReferenceImage>>.Fail(ImageEditErrorCodes.InvalidRequest, error);

    private static ImageEditCallResult<(Inputs, ContextScope, string)> Fail(string code, string error) =>
        ImageEditCallResult<(Inputs, ContextScope, string)>.Fail(code, error);

    private static string? Text(JsonObject reference, string name) =>
        reference[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
