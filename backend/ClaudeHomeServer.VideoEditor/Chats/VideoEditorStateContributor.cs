using System.Globalization;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Mcp;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Chats;

// Блок «Видео в этом чате» (ADR-022 §5): состояние — какая сцена в работе, открытый фильм и «Потрачено на
// фильм», журнал «с прошлого сообщения»; правило приоритета (видео идёт ТОЛЬКО через video_*, прямые
// local_*_to_video, generate_video Higgsfield и fal — по прямой просьбе: иначе получится ролик без карточки
// сцены в ленте); правило темпа «после каждой сцены спроси „Снимать следующую?“, подряд — только по явному
// „сними все“». Потолка трат у агента нет — темп держит этот текст, а не счётчик.
//
// Прямые local_* для видео не скрываем: local-media общий с картинками и звуком, и вычитание инструментов по
// второму флагу сделало бы его tools/list зависимым от него; приоритет держит текст, а не состав.
//
// Блок есть, только когда сервер video-editor доехал до хода (HasVideoEditorMcp): иначе правило звало бы
// инструменты, которых у хода нет. Едет хвостом хода ВСЕГДА (InTurnTail): сцена в работе и траты меняются от
// хода к ходу, в системном блоке секция обнуляла бы prefix cache всей истории.
public sealed class VideoEditorStateContributor(
    IFeatureFlagGate flags,
    VideoThreadStore? threads = null,
    VideoPrefsService? prefs = null,
    FilmSideStore? side = null,
    IConfiguration? config = null) : IPromptSectionContributor
{
    // Без video_shoot правила про него сослались бы на несуществующий инструмент
    private readonly bool _agentLaunch = config?.GetValue(VideoEditorToolset.AgentLaunchKey, true) ?? true;

    public const string SectionKey = "video-editor-state";

    public string Key => SectionKey;
    public string Title => "Видео в этом чате";
    // Между блоком звука (705) и правилом «локальная модель по умолчанию» (710): оно на блок ссылается
    public int Order => 707;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.HasVideoEditorMcp
        && sessionContext.OwnerId is { Length: > 0 } ownerId
        && flags.IsEnabled(ownerId, FeatureFlagKeys.VideoEditor);

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        if (sessionContext.OwnerId is not { Length: > 0 } ownerId)
            return Task.FromResult<PromptSectionContribution?>(null);
        var session = sessionContext.Session;
        var scope = VideoEditScope.Of(session);
        var (state, fresh) = threads?.TakeForTurn(ownerId, session.Id) ?? (VideoThreadsState.Empty, []);
        var spent = SpentOf(ownerId, scope, state);
        var block = Render(state, fresh, prefs?.Get(ownerId, scope), spent, _agentLaunch, scope.IsPersonal);
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, block, Title, InTurnTail: true)]));
    }

    // «Потрачено на фильм» открытого фильма: отображение, не потолок. Берётся из состояния фильма вне файла
    // (там же накоплено по всем чатам) плюс версии сцен ЭТОГО чата, которые счёт ещё не подхватил. Читает только
    // хранилища: тянуть сюда FilmService нельзя — он ведёт к SessionManager, а тот — к реестру секций, то есть
    // к этому же контрибьютору (цикл при сборке контейнера, тихое зависание старта)
    private VideoSpentDto? SpentOf(string ownerId, VideoEditScope scope, VideoThreadsState state)
    {
        if (side is null || scope.Project is null || state.Focus.FilmPath is not { } path) return null;
        var spends = side.Get(ownerId, scope.Key, path).Spends.ToDictionary(s => s.VersionId);
        var folder = FilmPaths.FolderOf(path);
        foreach (var scene in state.Scenes.Where(s => s.FilmRef?.Path == path || FilmPaths.Normalize(s.Folder) == folder))
            foreach (var version in scene.Versions)
                if (version.Cost is { } cost && !spends.ContainsKey(version.VersionId))
                    spends[version.VersionId] = new FilmSpendEntry(version.VersionId, cost.Currency, cost.Amount, 0, version.CreatedAt);
        return new VideoSpentDto(
            spends.Values.Where(s => s.Currency == "usd").Sum(s => s.Amount),
            spends.Values.Where(s => s.Currency == "credits").Sum(s => s.Amount),
            spends.Values.Sum(s => s.LocalSeconds));
    }

    public static string Render(VideoThreadsState state, IReadOnlyList<VideoThreadEvent> fresh, VideoPrefsDto? prefs,
        VideoSpentDto? spent, bool agentLaunch, bool personal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Видео в этом чате");
        sb.AppendLine(FocusText(state));
        if (state.Focus.FilmPath is { } film)
            sb.AppendLine($"Открыт фильм: {film}" + (spent is null ? "" : " · Потрачено на фильм: " + SpentText(spent)));
        sb.AppendLine(ChoiceText(prefs));
        if (agentLaunch) sb.AppendLine(personal ? PersonalPriorityRule : PriorityRule);
        if (agentLaunch) sb.AppendLine(PaceRule);
        if (fresh.Count > 0)
        {
            sb.AppendLine("С прошлого сообщения:");
            foreach (var e in fresh) sb.AppendLine("- " + e.Text);
        }
        return sb.ToString().TrimEnd();
    }

    // «В работе: сцена sc1 «Сцена 1» · версия 2 (остался с прошлых сообщений…)» — без списка сцен: его отдаёт video_state
    private static string FocusText(VideoThreadsState state)
    {
        if (state.Scenes.FirstOrDefault(s => s.SceneId == state.Focus.SceneId) is not { } s)
            return "В работе: ничего не выбрано";
        var version = s.Versions.FirstOrDefault(v => v.VersionId == s.CurrentVersionId) is { } cv ? $" · версия {cv.Number}" : " · ещё не снята";
        var running = s.Launches.Any(l => l.Status == VideoLaunchStatus.Running) ? " · идёт съёмка" : "";
        return $"В работе: сцена {s.SceneId} «{s.Name}»{version}{running} (остался с прошлых сообщений и не обязывает его продолжать). "
            + $"Сцен в чате: {state.Scenes.Count}";
    }

    public static string SpentText(VideoSpentDto spent)
    {
        var parts = new List<string>();
        if (spent.Usd > 0) parts.Add("$" + spent.Usd.ToString("0.00", CultureInfo.InvariantCulture));
        if (spent.Credits > 0) parts.Add(spent.Credits.ToString("0.##", CultureInfo.InvariantCulture) + " кредитов");
        if (spent.GpuSeconds > 0) parts.Add(spent.GpuSeconds.ToString("0", CultureInfo.InvariantCulture) + " GPU-с");
        return parts.Count == 0 ? "0" : string.Join(" + ", parts);
    }

    // «Выбор человека в «Видео»: поставщик fal, модель по умолчанию, 5 с»
    public static string ChoiceText(VideoPrefsDto? prefs)
    {
        if (prefs is null or { Provider: null, Model: null, DurationSec: null, Count: null })
            return "Выбор человека в «Видео»: по умолчанию";
        var parts = new List<string> { $"поставщик {prefs.Provider ?? "по умолчанию"}", $"модель {prefs.Model ?? "по умолчанию"}" };
        if (prefs.DurationSec is { } d) parts.Add($"{d} с");
        if (prefs.Count is { } c) parts.Add($"вариантов {c}");
        return "Выбор человека в «Видео»: " + string.Join(", ", parts);
    }

    // Общая часть правила приоритета для проекта и личного чата
    private const string EditorFirst =
        "Видео — сцены между двумя кадрами и фильм из них — делай только через video_new → video_scene_set → video_shoot "
        + "(сохранить в проект — video_save_scene, фильм — video_film_edit и video_film_build): они ведут сцену и версии, "
        + "показывают цену и пишут в ленту ту же карточку, что кнопка человека, и уважают его выбор. "
        + "Не передавай provider/model, если человек сам не просил сменить.";

    public const string PriorityRule = EditorFirst
        + " Просьба снять локально / на своей видеокарте / бесплатно — video_shoot с provider local. "
        + "Прямые local_text_to_video, local_image_to_video, local_reference_to_video, generate_video Higgsfield и fal "
        + "(run_model, submit_job) для видео — только если человек явно попросил сделать напрямую, мимо редактора: "
        + "иначе получится ролик без карточки сцены.";

    // Личный чат: локальных моделей для видео и фильмов нет
    public const string PersonalPriorityRule = EditorFirst
        + " Локальных моделей для видео, фильмов и сохранения в проект в этом чате нет. "
        + "Прямые generate_video Higgsfield и fal для видео — только по явной просьбе, мимо редактора.";

    // Темп съёмки: потолка трат нет, поэтому правило держит текст
    public const string PaceRule =
        "После каждой снятой сцены спроси человека «Снимать следующую?». Снимать подряд, без вопроса, можно только "
        + "если он прямо сказал «сними все». Результат съёмки ищи в video_state (запуск running → done), не выдумывай его.";
}
