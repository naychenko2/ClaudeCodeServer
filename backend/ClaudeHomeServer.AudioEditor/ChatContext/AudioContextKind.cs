using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;

namespace ClaudeHomeServer.Services.AudioEditor.ChatContext;

// Роли референсов звука (ADR-023 §1): принимает основной объект «audio»; роль принадлежит операции
public static class AudioContextRoles
{
    public const string Reference = "reference";
    public const string Piece = "piece";
    public const string Voice = "voice";
}

// Виды «audio» и «audio-voice» контекста чата (ADR-023).
// audio: Ref = {threadId, versionId?} — нить звука этого чата; основной объект, принимает референсы
// (AcceptedRefs) и описывает «Чем» (DescribeExecutor). Он же засевает контекст чата без файла из фокуса
// звука — после картинки.
// audio-voice: Ref = {slug} — голос из библиотеки «Голоса» проекта; только референс с ролью voice,
// в личном чате библиотеки нет.
public sealed class AudioContextKind(
    AudioThreadStore store,
    IEnumerable<IAudioEngine>? engines = null,
    AudioPrefsService? prefs = null) : IContextKindProvider, IChatContextSeedSource
{
    public const string Kind = "audio";
    public const string VoiceKind = "audio-voice";

    public IReadOnlyList<string> Kinds { get; } = [Kind, VoiceKind];

    public int SeedPriority => 1;

    public bool CanBePrimary(string kind) => kind == Kind;

    // Нить обязана быть в хранилище этого владельца и этого чата — чужая нить недостижима по построению;
    // голос — в библиотеке проекта чата
    public string? Validate(ContextScope scope, string kind, JsonObject reference)
    {
        if (kind == VoiceKind) return ValidateVoice(scope, reference);
        if (kind != Kind) return $"Вид «{kind}» не принадлежит редактору звука";
        if (Text(reference, "threadId") is not { } threadId) return "Не указана нить звука";
        if (Find(scope, threadId) is not { } thread) return "Нить звука не найдена в этом чате";
        if (Text(reference, "versionId") is { } versionId && thread.Version(versionId) is null)
            return "У звука нет такой версии";
        return null;
    }

    private static string? ValidateVoice(ContextScope scope, JsonObject reference)
    {
        if (scope.Project is null) return "В личном чате нет библиотеки голосов";
        if (!VoiceStore.IsValidSlug(Text(reference, "slug"))) return "Не указан голос";
        return VoiceStore.Get(scope.Project.RootPath, Text(reference, "slug")!) is null
            ? "Голос не найден в проекте"
            : null;
    }

    public ContextItemSummary Describe(ContextScope scope, ContextItem item)
    {
        if (item.Kind == VoiceKind)
        {
            var voice = scope.Project is { } project && VoiceStore.IsValidSlug(Text(item.Ref, "slug"))
                ? VoiceStore.Get(project.RootPath, Text(item.Ref, "slug")!)
                : null;
            return voice is null
                ? new ContextItemSummary("голос недоступен", null, null, true)
                : new ContextItemSummary(voice.Name, null, null, false);
        }
        if (Text(item.Ref, "threadId") is not { } threadId || Find(scope, threadId) is not { } thread)
            return new ContextItemSummary("звук недоступен", null, null, true);
        var version = (Text(item.Ref, "versionId") is { } v ? thread.Version(v) : null) ?? thread.CurrentVersion;
        return new ContextItemSummary(AudioJobThreads.Name(thread),
            version is { IsOrigin: false } ? $"v{version.Number}" : null, null, false);
    }

    // Что принимает основной объект «audio»: op == null — таблица по всем операциям (из неё считается
    // usedBy), иначе только роли, которые берёт эта операция. Другие виды основного — пусто
    public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op)
    {
        if (primary.Kind != Kind) return [];
        var table = new (string Role, string Title, string[] Kinds, AudioOp[] Ops)[]
        {
            (AudioContextRoles.Voice, "Голос", [VoiceKind], [AudioOp.Speak, AudioOp.Dialogue, AudioOp.ConvertVoice]),
            (AudioContextRoles.Reference, "Образец", [Kind, ProjectFileContextKind.Kind],
                [AudioOp.CloneVoice, AudioOp.ConvertVoice, AudioOp.Cover, AudioOp.Master]),
            (AudioContextRoles.Piece, "Кусок", [Kind, ProjectFileContextKind.Kind], [AudioOp.Concat]),
        };
        return
        [
            .. table.Select(t => new ContextRoleSpec(t.Role, t.Title, t.Kinds,
                    [.. t.Ops.Select(AudioEditJobService.OpName).Where(o => op is null || o == op)]))
                .Where(r => r.Ops.Count > 0),
        ];
    }

    // «Чем»: поставщик и модель из настроек нити, цена — по курируемому каталогу; без настроек — «Авто»
    public string? DescribeExecutor(ContextScope scope, ContextItem primary)
    {
        if (primary.Kind != Kind || Text(primary.Ref, "threadId") is not { } threadId) return null;
        // Как цепочка запуска: нить → префы режима → каталог. Режим нити без настроек неизвестен,
        // берётся первый режим, где человек что-то выбрал
        var settings = Find(scope, threadId)?.Settings ?? PrefsSettings(scope);
        var provider = string.IsNullOrWhiteSpace(settings?.Provider) ? null : settings.Provider;
        var modelId = string.IsNullOrWhiteSpace(settings?.Model) || AudioCatalog.IsAuto(settings.Model) ? null : settings.Model;
        var engine = provider is null ? null : engines?.FirstOrDefault(e => e.Key == provider);
        var model = modelId is null ? null : (engine?.Models ?? engines?.SelectMany(e => e.Models) ?? []).FirstOrDefault(m => m.Id == modelId);
        var parts = new List<string>
        {
            provider is null ? AudioCatalog.AutoModelLabel : engine?.Label ?? provider,
            modelId is null ? AudioCatalog.AutoModelLabel : model?.Label ?? modelId,
        };
        if (model?.PriceHint is { } price)
            parts.Add(ContextPriceText.Format(price.Amount, price.Unit, price.Per));
        return string.Join(" · ", parts);
    }

    private AudioThreadSettings? PrefsSettings(ContextScope scope)
    {
        if (prefs is null) return null;
        var area = AudioEditScope.Of(scope.Session);
        return new[] { AudioModes.Voice, AudioModes.Music, AudioModes.Process }
            .Select(mode => prefs.ForNewThread(scope.OwnerId, area, mode))
            .FirstOrDefault(s => s is not null);
    }

    public ContextItem? SeedPrimary(ContextScope scope)
    {
        var state = store.Get(scope.OwnerId, scope.Session.Id);
        if (state.Focus is not { } focus || state.Threads.All(t => t.Id != focus)) return null;
        return new ContextItem("seed_audio", Kind, new JsonObject { ["threadId"] = focus }, null,
            ContextActor.Human, scope.Session.CreatedAt);
    }

    private AudioThread? Find(ContextScope scope, string threadId) =>
        store.Get(scope.OwnerId, scope.Session.Id).Threads.FirstOrDefault(t => t.Id == threadId);

    private static string? Text(JsonObject reference, string name) =>
        reference[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
