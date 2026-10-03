using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Mcp;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.AudioEditor.Chats;

// Блок «Звук в этом чате» (ADR-021 §5, вопрос 1): правило приоритета audio_* над прямыми local_*
// (какой звук в работе и выбор человека отдаёт хвост «Контекст хода»). Прямые local_* для звука
// не скрываем: local-media общий с картинками и видео, и вычитание инструментов по второму флагу
// сделало бы его tools/list зависимым от него; поэтому приоритет держит текст, а не состав.
//
// Блок есть, только когда сервер audio-editor доехал до хода (HasAudioEditorMcp): иначе правило
// звало бы инструменты, которых у хода нет. Едет хвостом хода ВСЕГДА (InTurnTail): фокус и выбор
// меняются от хода к ходу, в системном блоке секция обнуляла бы prefix cache всей истории.
public sealed class AudioEditorStateContributor(
    IFeatureFlagGate flags,
    IConfiguration? config = null) : IPromptSectionContributor
{
    // Без audio_generate правило приоритета сослалось бы на несуществующий инструмент
    private readonly bool _agentLaunch = config?.GetValue(AudioEditorToolset.AgentLaunchKey, true) ?? true;

    public const string SectionKey = "audio-editor-state";

    public string Key => SectionKey;
    public string Title => "Звук в этом чате";
    // Между блоком картинок (700) и правилом «локальная модель по умолчанию» (710): оно на блок ссылается
    public int Order => 705;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.HasAudioEditorMcp
        && sessionContext.OwnerId is { Length: > 0 } ownerId
        && flags.IsEnabled(ownerId, FeatureFlagKeys.AudioEditor);

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        if (sessionContext.OwnerId is not { Length: > 0 } ownerId)
            return Task.FromResult<PromptSectionContribution?>(null);
        var session = sessionContext.Session;
        var scope = AudioEditScope.Of(session);
        var block = Render(_agentLaunch, scope.IsPersonal);
        if (block is null) return Task.FromResult<PromptSectionContribution?>(null);
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, block, Title, InTurnTail: true)]));
    }

    // null — блок пуст: без audio_generate не остаётся ни одной строки («В работе» и «Выбор человека» отдаёт
    // хвост «Контекст хода»)
    public static string? Render(bool agentLaunch, bool personal) =>
        agentLaunch ? "## Звук в этом чате\n" + PriorityRuleFor(personal) : null;

    // Ссылка на полосу «Звук» переписана на контекст хода
    public static string PriorityRuleFor(bool personal) =>
        (personal ? PersonalPriorityRule : PriorityRule)
            .Replace("уважают выбор человека в полосе «Звук»", "уважают выбор человека в строке контекста");

    // Общая часть правила для проекта и личного чата
    private const string EditorFirst =
        "Озвучку, музыку и обработку звука делай через audio_new → audio_generate (склейка — audio_concat): "
        + "они ведут нить и версии, показывают цену и уважают выбор человека в полосе «Звук». "
        + "Не передавай provider/model, если человек сам не просил сменить.";

    public const string PriorityRule = EditorFirst
        + " Просьба сделать звук локально / на своей видеокарте / бесплатно — audio_generate с provider local. "
        + "Прямые local_* для звука (local_speech, local_music_generate, local_music_edit, local_audio_separate, "
        + "local_audio_enhance, local_audio_to_midi, local_transcribe, local_voice_convert, local_voice_train) — "
        + "только если человек явно попросил сделать напрямую, мимо редактора.";

    // Личный чат: local-media здесь нет, локальных моделей для звука тоже
    public const string PersonalPriorityRule = EditorFirst
        + " Локальных моделей для звука в этом чате нет.";
}
