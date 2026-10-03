using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Mcp;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.AudioEditor.Chats;

// Блок «Звук в этом чате» (ADR-021 §5, вопрос 1): правило приоритета audio_* над прямыми local_*
// плюс коротко — какой звук в работе и выбор человека в полосе «Звук». Прямые local_* для звука
// не скрываем: local-media общий с картинками и видео, и вычитание инструментов по второму флагу
// сделало бы его tools/list зависимым от него; поэтому приоритет держит текст, а не состав.
//
// Блок есть, только когда сервер audio-editor доехал до хода (HasAudioEditorMcp): иначе правило
// звало бы инструменты, которых у хода нет. Едет хвостом хода ВСЕГДА (InTurnTail): фокус и выбор
// меняются от хода к ходу, в системном блоке секция обнуляла бы prefix cache всей истории.
public sealed class AudioEditorStateContributor(
    IFeatureFlagGate flags,
    AudioThreadStore? threads = null,
    AudioPrefsService? prefs = null,
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
        var state = threads?.Get(ownerId, session.Id) ?? AudioThreadsState.Empty;
        // При строке контекста «В работе» и «Выбор человека» отдаёт хвост «Контекст хода»; остаётся правило приоритета
        var row = flags.IsEnabled(ownerId, FeatureFlagKeys.ComposerContextRow);
        var block = Render(state, mode => prefs?.Get(ownerId, scope, mode), _agentLaunch, scope.IsPersonal, row);
        if (block is null) return Task.FromResult<PromptSectionContribution?>(null);
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, block, Title, InTurnTail: true)]));
    }

    // null — блок пуст: при строке контекста без audio_generate не остаётся ни одной строки
    public static string? Render(AudioThreadsState state, Func<string, AudioModePrefs?> prefs, bool agentLaunch,
        bool personal, bool contextRow = false)
    {
        if (contextRow)
            return agentLaunch ? "## Звук в этом чате\n" + PriorityRuleFor(personal, contextRow: true) : null;
        var lines = new List<string> { "## Звук в этом чате", FocusText(state), ChoiceText(prefs) };
        if (agentLaunch) lines.Add(PriorityRuleFor(personal, contextRow: false));
        return string.Join("\n", lines);
    }

    // При строке контекста ссылка на полосу «Звук» переписана на контекст хода; без неё — константы как были
    public static string PriorityRuleFor(bool personal, bool contextRow)
    {
        var rule = personal ? PersonalPriorityRule : PriorityRule;
        return contextRow
            ? rule.Replace("уважают выбор человека в полосе «Звук»", "уважают выбор человека в строке контекста")
            : rule;
    }

    // «В работе: звук t1 — файл voice/intro.mp3 · версия 3» — без списка нитей: его отдаёт audio_state
    private static string FocusText(AudioThreadsState state)
    {
        if (state.Threads.FirstOrDefault(t => t.Id == state.Focus) is not { } t)
            return "В работе: ничего не выбрано";
        var what = t.File is { Length: > 0 } file ? $"файл {file}" : "новый звук, ещё не сохранён";
        var version = t.CurrentVersion is { } cv ? $" · {AudioThread.Label(cv)}" : "";
        return $"В работе: звук {t.Id} — {what}{version} ({FocusIsNotBindingText})";
    }

    // «Выбор человека в полосе «Звук»: голос — поставщик yandex, модель по умолчанию; музыка — …»
    public static string ChoiceText(Func<string, AudioModePrefs?> prefs)
    {
        var modes = new[] { (AudioModes.Voice, "голос"), (AudioModes.Music, "музыка"), (AudioModes.Process, "обработка") };
        var parts = modes.Select(m => (Title: m.Item2, Prefs: prefs(m.Item1)))
            .Where(m => m.Prefs is { Provider: not null } or { Model: not null })
            .Select(m => $"{m.Title} — поставщик {m.Prefs!.Provider ?? "по умолчанию"}, модель {m.Prefs.Model ?? "по умолчанию"}")
            .ToList();
        return "Выбор человека в полосе «Звук»: " + (parts.Count == 0 ? "по умолчанию" : string.Join("; ", parts));
    }

    public const string FocusIsNotBindingText = "остался с прошлых сообщений и не обязывает его продолжать";

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
