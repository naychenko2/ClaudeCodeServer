using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;

namespace ClaudeHomeServer.Services.ImageEditor.ChatContext;

// Роли референсов картинки (ADR-023 §1). Роль принадлежит ПРИНИМАЮЩЕЙ операции: style/object/face и
// character принимает основной объект «image»; frame-a/frame-b — роли, под которыми картинка-версия входит
// в сцену видео (владелец — VideoEditor, фаза 3), здесь они лишь объявлены, «image» их не принимает
public static class ImageContextRoles
{
    public const string Style = "style";
    public const string Object = "object";
    public const string Face = "face";
    public const string Character = "character";
    public const string FrameA = "frame-a";
    public const string FrameB = "frame-b";
}

// Виды «image» и «image-character» контекста чата (ADR-023).
// image: Ref = {threadId, versionId?} — нить картинки этого чата; основной объект, принимает референсы
// (AcceptedRefs) и описывает «Чем» (DescribeExecutor). Референсом бывает и Ref = {upload} — образец,
// загруженный с диска человека в рабочую папку модуля (ADR-023 §2.3). Он же засевает контекст чата без файла из фокуса
// картинки — с приоритетом над звуком.
// image-character: Ref = {slug} — персонаж проекта (characters/<slug>); только референс с ролью character,
// в личном чате персонажей нет.
public sealed class ImageContextKind(
    ImageThreadStore store,
    ImageProjectPrefsStore? prefs = null,
    IEnumerable<IImageEditor>? editors = null,
    ImageEditWorkspace? workspace = null) : IContextKindProvider, IChatContextSeedSource
{
    public const string Kind = "image";
    public const string CharacterKind = "image-character";

    // Операции, берущие образцы: у остальных (фон, апскейл, дорисовка, лица) образцов нет.
    private static readonly string[] RefOps = ImageEditorToolset.RefOps;

    public IReadOnlyList<string> Kinds { get; } = [Kind, CharacterKind];

    public int SeedPriority => 0;

    public bool CanBePrimary(string kind) => kind == Kind;

    // Нить обязана быть в хранилище этого владельца и этого чата — чужая нить недостижима по построению;
    // персонаж — в папке проекта чата
    public string? Validate(ContextScope scope, string kind, JsonObject reference)
    {
        if (kind == CharacterKind) return ValidateCharacter(scope, reference);
        if (kind != Kind) return $"Вид «{kind}» не принадлежит редактору картинок";
        if (Text(reference, "upload") is { } upload)
            return workspace?.UploadExists(scope.OwnerId, upload) == true ? null : "Образец не найден или истёк";
        if (Text(reference, "threadId") is not { } threadId) return "Не указана нить картинки";
        if (Find(scope, threadId) is not { } thread) return "Нить картинки не найдена в этом чате";
        if (Text(reference, "versionId") is { } versionId && thread.Version(versionId) is null)
            return "У картинки нет такой версии";
        return null;
    }

    private static string? ValidateCharacter(ContextScope scope, JsonObject reference)
    {
        if (scope.Project is null) return "В личном чате нет персонажей";
        if (!CharacterStore.IsValidSlug(Text(reference, "slug"))) return "Не указан персонаж";
        return CharacterStore.Get(scope.Project.RootPath, Text(reference, "slug")!) is null
            ? "Персонаж не найден в проекте"
            : null;
    }

    public ContextItemSummary Describe(ContextScope scope, ContextItem item)
    {
        if (item.Kind == CharacterKind)
        {
            var character = scope.Project is { } project && CharacterStore.IsValidSlug(Text(item.Ref, "slug"))
                ? CharacterStore.Get(project.RootPath, Text(item.Ref, "slug")!)
                : null;
            return character is null
                ? new ContextItemSummary("персонаж недоступен", null, null, true)
                : new ContextItemSummary(character.Name, null, null, false);
        }
        if (Text(item.Ref, "upload") is { } upload)
            return workspace?.UploadExists(scope.OwnerId, upload) == true
                ? new ContextItemSummary("образец", null, null, false)
                : new ContextItemSummary("образец недоступен", null, null, true);
        if (Text(item.Ref, "threadId") is not { } threadId || Find(scope, threadId) is not { } thread)
            return new ContextItemSummary("картинка недоступна", null, null, true);
        var version = (Text(item.Ref, "versionId") is { } v ? thread.Version(v) : null) ?? thread.CurrentVersion;
        return new ContextItemSummary(ImageThreadService.Name(thread),
            version is { IsOrigin: false } ? $"v{version.Number}" : null, null, false);
    }

    // Что принимает основной объект «image»: op == null — таблица по всем операциям (из неё считается
    // usedBy), иначе только роли, которые берёт эта операция. Другие виды основного — пусто
    public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op)
    {
        if (primary.Kind != Kind) return [];
        IReadOnlyList<string> ops = op is null ? RefOps : RefOps.Where(o => o == op).ToList();
        if (ops.Count == 0) return [];
        return
        [
            new(ImageContextRoles.Style, "Стиль", [Kind, ProjectFileContextKind.Kind], ops),
            new(ImageContextRoles.Object, "Объект", [Kind, ProjectFileContextKind.Kind], ops),
            new(ImageContextRoles.Face, "Лицо", [Kind, ProjectFileContextKind.Kind], ops),
            new(ImageContextRoles.Character, "Персонаж", [CharacterKind], ops),
        ];
    }

    // «Чем»: поставщик и модель нити (иначе выбор «Править» проекта), цена — по курируемому каталогу
    public string? DescribeExecutor(ContextScope scope, ContextItem primary)
    {
        if (primary.Kind != Kind || Text(primary.Ref, "threadId") is not { } threadId) return null;
        var settings = Find(scope, threadId)?.Settings
            ?? (scope.Project is { } project && prefs is not null ? prefs.Get(scope.OwnerId, project.Id).EditSettings() : null);
        var provider = string.IsNullOrWhiteSpace(settings?.Provider) ? null : settings.Provider;
        var modelId = string.IsNullOrWhiteSpace(settings?.Model) || settings.Model == ImageEditCatalog.AutoModelId ? null : settings.Model;
        var editor = provider is null ? null : editors?.FirstOrDefault(e => e.Key == provider);
        var model = modelId is null ? null : (editor?.Models ?? editors?.SelectMany(e => e.Models) ?? []).FirstOrDefault(m => m.Id == modelId);
        var parts = new List<string>
        {
            provider is null ? ImageEditCatalog.AutoModelLabel : editor?.Label ?? provider,
            modelId is null ? ImageEditCatalog.AutoModelLabel : model?.Label ?? modelId,
        };
        if (model?.PriceHint is { } price)
            parts.Add(ContextPriceText.Format(price.Amount, price.Unit, price.Per));
        return string.Join(" · ", parts);
    }

    public ContextItem? SeedPrimary(ContextScope scope)
    {
        var state = store.Get(scope.OwnerId, scope.Session.Id);
        if (state.Focus is not { } focus || state.Threads.All(t => t.Id != focus)) return null;
        return new ContextItem("seed_image", Kind, new JsonObject { ["threadId"] = focus }, null,
            ContextActor.Human, scope.Session.CreatedAt);
    }

    private ImageThread? Find(ContextScope scope, string threadId) =>
        store.Get(scope.OwnerId, scope.Session.Id).Threads.FirstOrDefault(t => t.Id == threadId);

    private static string? Text(JsonObject reference, string name) =>
        reference[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
