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
        // Черновик без файла и без имени: «Новый звук · черновик» (подпись для человека, агентский текст берёт Name)
        var label = thread.File is not { Length: > 0 } && thread.Name is not { Length: > 0 } ? "Новый звук · черновик" : AudioJobThreads.Name(thread);
        return new ContextItemSummary(label,
            version is { IsOrigin: false } ? $"v{version.Number}" : null, null, false);
    }

    // Что принимает основной объект «audio»: op == null — таблица по всем операциям (из неё считается
    // usedBy), иначе только роли, которые берёт эта операция. Другие виды основного — пусто
    public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) =>
        primary.Kind != Kind ? [] : RolesOf(op);

    // Таблица ролей без привязки к экземпляру: её же читает запуск по ревизии (AudioContextLaunch),
    // чтобы «референс, который операция не берёт» пропускался по той же таблице, что рисует серый чип
    internal static IReadOnlyList<ContextRoleSpec> RolesOf(string? op)
    {
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
        // Как цепочка запуска audio_generate (AudioEditorToolset.HumanChoice): нить → префы режима → каталог
        var thread = Find(scope, threadId);
        var mode = thread?.Settings?.Mode ?? AudioModes.Voice;
        var chain = prefs is null
            ? AudioPrefsResolver.Resolve(mode, thread?.Settings, null, AudioEditJobService.CatalogDefault(mode))
            : prefs.Resolve(scope.OwnerId, AudioEditScope.Of(scope.Session), mode, thread?.Settings,
                AudioEditJobService.CatalogDefault(mode));
        var provider = string.IsNullOrWhiteSpace(chain.Provider) || AudioCatalog.IsAuto(chain.Provider) ? null : chain.Provider;
        var modelId = string.IsNullOrWhiteSpace(chain.Model) || AudioCatalog.IsAuto(chain.Model) ? null : chain.Model;
        var engine = provider is null ? null : engines?.FirstOrDefault(e => e.Key == provider);
        var model = modelId is null ? null : (engine?.Models ?? engines?.SelectMany(e => e.Models) ?? []).FirstOrDefault(m => m.Id == modelId);
        var parts = new List<string>
        {
            provider is null ? AudioCatalog.AutoModelLabel : engine?.Label ?? provider,
            modelId is null ? AudioCatalog.AutoModelLabel : model?.Label ?? modelId,
        };
        if (model?.PriceHint is { } price)
            parts.Add(PriceText(price));
        return string.Join(" · ", parts);
    }

    // AudioPriceHint создают только fal и локальный каталог (free). У fal Unit — единица тарификации
    // (chars|sec|min|run), валюта подразумевается usd; у free Unit — «free». Символы пересчитываются на 1000: «$0.09 / 1000 симв.» вместо «$0.00009 / симв.»
    internal static string PriceText(AudioPriceHint price)
    {
        var (amount, unit, per) = PriceParts(price);
        return ContextPriceText.Format(amount, unit, per);
    }

    // Цена числом, валютой и «за что» — те же числа, что в подписи PriceText; валюта (free | usd | credits | rub),
    // единица тарификации fal в неё не попадает
    internal static (double Amount, string Unit, string Per) PriceParts(AudioPriceHint price) => price.Unit switch
    {
        AudioPriceUnits.Chars => (price.Amount * 1000, AudioPriceUnits.Usd, "1000 симв."),
        AudioPriceUnits.Sec or AudioPriceUnits.Min or AudioPriceUnits.Run => (price.Amount, AudioPriceUnits.Usd, price.Unit),
        _ => (price.Amount, price.Unit, price.Per),
    };

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
