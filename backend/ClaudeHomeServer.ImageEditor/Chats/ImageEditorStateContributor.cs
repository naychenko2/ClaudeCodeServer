using System.Globalization;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.ImageEditor.Chats;

// Блок «Картинки в этом чате» в каждом ходе чата с нитями (ADR-019 §3): какая картинка
// в работе, у каждой файл, версии (id, «версия N», какая текущая, откуда выросла — чтобы агент
// понимал «поправь предыдущую / вторую»), идущие запуски, плюс журнал «с прошлого сообщения».
// Ручной запуск агент узнаёт именно отсюда: тихую строку image_launch в ленте модель не видит.
// Нить в транскрипт CLI не входит, поэтому после компакции и --resume модель узнаёт о ней только
// отсюда. Чат ПРОЕКТА без нитей получает короткий блок (выбор в полосе и правило приоритета),
// только если человек явно сохранил выбор в полосе проекта; без файла выбора и без нитей блока нет.
// ЛИЧНЫЙ чат вне проекта при ImageEditor:AgentLaunch получает блок всегда (решение Андрея 29.09):
// иначе «нарисуй» без настроек уходит мимо image_generate. Развилка — вид чата, свойство сессии.
//
// Секция едет хвостом хода ВСЕГДА (PromptSection.InTurnTail): она меняется от хода к ходу, и в
// системном блоке обнуляла бы prefix cache всей истории у любого провайдера, а не только у
// провайдера с RecallInTurnText (ADR-018 §10.4, риск 1 плана v2).
public sealed class ImageEditorStateContributor(
    IFeatureFlagGate flags,
    IImageEditJobs? jobs = null,
    ImageThreadStore? threads = null,
    Prefs.ImageProjectPrefsService? prefs = null,
    IConfiguration? config = null) : IPromptSectionContributor
{
    // Правило про image_generate — только когда инструмент есть, иначе агент сошлётся на несуществующий
    private readonly bool _agentLaunch = config?.GetValue(ImageEditorToolset.AgentLaunchKey, true) ?? true;

    public const string SectionKey = "image-editor-state";

    public string Key => SectionKey;
    public string Title => "Картинки в этом чате";
    // Порядок среди секций хвоста значения почти не имеет; после графа кода (600)
    public int Order => 700;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is { Length: > 0 } ownerId
        && flags.IsEnabled(ownerId, FeatureFlagKeys.ImageEditor)
        && (PersonalAlways(sessionContext.Session)
            || HasThreads(ownerId, sessionContext.Session) || HasSavedPrefs(ownerId, sessionContext.Session));

    // Личный чат без нитей и выбора — блок ради правила приоритета, а оно есть только при image_generate
    private bool PersonalAlways(Session session) => _agentLaunch && session.ProjectId is null;

    private bool HasThreads(string ownerId, Session session) =>
        threads is not null && threads.Get(ownerId, session.Id).Threads.Count > 0;

    private bool HasSavedPrefs(string ownerId, Session session) =>
        prefs is not null && prefs.HasSaved(ownerId, ImageEditScope.Of(session));

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        var session = sessionContext.Session;
        if (sessionContext.OwnerId is not { Length: > 0 } ownerId)
            return Task.FromResult<PromptSectionContribution?>(null);

        var scope = ImageEditScope.Of(session);
        // У проекта — через проект: персонаж проверяется на его диске; у личной области персонажа нет
        var scopePrefs = session.ProjectId is { } projectId ? prefs?.Get(ownerId, projectId) : prefs?.Get(ownerId, scope);
        string block;
        if (threads is not null && HasThreads(ownerId, session))
        {
            var (state, fresh) = threads.TakeForTurn(ownerId, session.Id);
            block = RenderThreads(state, fresh, jobId => jobs?.Get(ownerId, scope.Key, jobId), scopePrefs, _agentLaunch,
                scope.IsPersonal);
        }
        else if (scopePrefs is not null && HasSavedPrefs(ownerId, session))
            block = RenderChoice(scopePrefs, _agentLaunch, scope.IsPersonal);
        else if (PersonalAlways(session))
            block = RenderEmpty();
        else
            return Task.FromResult<PromptSectionContribution?>(null);
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, block, Title, InTurnTail: true)]));
    }

    // Нити картинок чата: «в работе» первой, у каждой файл, шаг и ожидающие варианты
    // prefs — выбор человека в полосе «Картинки» проекта: для картинки в работе показываются её
    // настройки (а без них и без фокуса — проекта), персонаж всегда из проекта
    public static string RenderThreads(ImageThreadsState state, IReadOnlyList<ImageThreadEvent> fresh,
        Func<string, ImageEditJobDto?> job, Prefs.ImageProjectPrefs? prefs = null, bool priorityRule = false,
        bool personal = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Картинки в этом чате");
        sb.AppendLine(state.Focus is null
            ? "В работе: ничего не выбрано"
            : $"В работе: картинка {state.Focus} — {FocusIsNotBindingText}");
        if (prefs is not null)
        {
            var focused = state.Threads.FirstOrDefault(t => t.Id == state.Focus);
            sb.AppendLine(ChoiceText(prefs, focused?.Settings));
            sb.AppendLine(ChoiceRule);
        }
        if (priorityRule) sb.AppendLine(personal ? PersonalPriorityRule : PriorityRule);
        foreach (var t in state.Threads.OrderByDescending(t => t.Id == state.Focus))
        {
            var what = t.File is { Length: > 0 } file ? $"файл {file}"
                : $"новая картинка, ещё не сохранена ({DraftSaveText(t.DraftFolder, personal)})";
            var current = t.CurrentVersion is { } cv ? $"; правка пойдёт от: {ImageThread.Label(cv)}" : "";
            // Стопка — формат нитей до 27.09: её шаг показывается, пока она есть
            var steps = t.CurrentStack?.Steps ?? [];
            var at = t.CurrentStepId is { } s && steps.Contains(s) ? steps.ToList().IndexOf(s) + 1 : 0;
            var stack = steps.Count == 0 ? "" : at == 0 ? $"; старая стопка: на холсте исходник, шагов {steps.Count}"
                : $"; старая стопка: шаг {at} из {steps.Count}";
            var pending = t.PendingJobId is { } p ? "; варианты старой стопки ждут выбора человека" + Outcome(job(p))
                : t.InterruptedJobId is not null ? "; последняя задача потеряна при перезапуске сервера, варианты недоступны" : "";
            sb.AppendLine($"- {t.Id}{(t.Id == state.Focus ? " (в работе)" : "")}: {what}{current}{stack}{pending}");
            RenderVersions(sb, t, job);
        }
        if (fresh.Count > 0)
        {
            sb.AppendLine("С прошлого сообщения:");
            foreach (var e in fresh)
                sb.AppendLine("- " + e.Text + Outcome(e.JobId is { } id && e.Kind == ImageThreadEventKinds.Launched ? job(id) : null));
        }
        return sb.ToString().TrimEnd();
    }

    // Чат без нитей при явно сохранённом выборе: без списка картинок и журнала «с прошлого сообщения»
    public static string RenderChoice(Prefs.ImageProjectPrefs prefs, bool priorityRule, bool personal = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Картинки в этом чате");
        sb.AppendLine("В работе: ничего не выбрано");
        sb.AppendLine(ChoiceText(prefs, null));
        sb.AppendLine(ChoiceRule);
        if (priorityRule) sb.AppendLine(personal ? PersonalPriorityRule : PriorityRule);
        return sb.ToString().TrimEnd();
    }

    // Личный чат без нитей и без сохранённого выбора: выбора человека нет, поэтому ни ChoiceText,
    // ни ChoiceRule («это выбор человека») — только правило приоритета
    public static string RenderEmpty() =>
        "## Картинки в этом чате\nВ работе: ничего не выбрано\n" + PersonalPriorityRule;

    // Сколько последних версий показывать у картинки: исходник и текущая видны всегда
    public const int MaxVersionsShown = 8;

    // «  версии: …» и «  рисуется: …» под строкой картинки
    private static void RenderVersions(StringBuilder sb, ImageThread t, Func<string, ImageEditJobDto?> job)
    {
        var ai = t.Versions.Where(v => !v.IsOrigin).ToList();
        var shown = ai.TakeLast(MaxVersionsShown).ToList();
        if (t.CurrentVersion is { IsOrigin: false } cv && !shown.Contains(cv)) shown.Insert(0, cv);
        if (ai.Count > 0 || t.Versions.Any(v => v.IsOrigin && v.Steps.Count > 0))
        {
            sb.AppendLine("  версии:");
            foreach (var v in t.Versions.Where(v => v.IsOrigin).Concat(shown))
                sb.AppendLine("  - " + VersionLine(t, v));
            if (ai.Count > shown.Count) sb.AppendLine($"  - (ещё {ai.Count - shown.Count} раньше — полный список в image_state)");
        }
        foreach (var l in t.Launches.Where(l => l.Status == ImageThreadLaunchStatus.Running))
            sb.AppendLine($"  рисуется: «{l.Prompt}», от: {(t.Version(l.BaseVersionId) is { } b ? ImageThread.Label(b) : "исходника")}"
                + Outcome(job(l.JobId)));
        foreach (var l in t.Launches.Where(l => l.Status == ImageThreadLaunchStatus.Interrupted).TakeLast(1))
            sb.AppendLine(t.Versions.Any(v => v.JobId == l.JobId)
                ? $"  запуск «{l.Prompt}» прерван перезапуском сервера: часть вариантов уже версии, остальные не дорисованы — запусти заново"
                : $"  запуск «{l.Prompt}» потерян при перезапуске сервера, вариантов не будет — запусти заново");
    }

    // «исходник [origin] (в работе), правок без ИИ 2» / «версия 3 [id] — вариант 1 запуска «фон», от: исходник»
    private static string VersionLine(ImageThread t, ImageThreadVersion v)
    {
        var head = $"{ImageThread.Label(v)} [{v.Id}]{(v.Id == t.CurrentVersionId ? " (в работе)" : "")}";
        var edits = v.IsOrigin ? v.Steps.Count : Math.Max(0, v.Steps.Count - 1);
        var editsText = edits > 0 ? $", правок без ИИ {edits}" : "";
        if (v.IsOrigin) return head + editsText;
        var prompt = t.Launches.FirstOrDefault(l => l.JobId == v.JobId)?.Prompt;
        var from = t.Version(v.BaseVersionId) is { } b ? ImageThread.Label(b) : "исходник";
        return $"{head} — вариант {v.Variant}{(prompt is null ? "" : $" запуска «{prompt}»")}, от: {from}{editsText}";
    }

    public const string ChoiceRule =
        "Это выбор человека — не передавай provider/model/character в image_generate, если он сам не просил сменить.";

    // Фокус остаётся с прошлых сообщений, человек его мог и не выбирать: без оговорки агент
    // принимал любую следующую просьбу за правку картинки в работе (баг 30.09)
    public const string FocusIsNotBindingText =
        "осталась с прошлых сообщений и не обязывает её продолжать";

    // Критерий «новая картинка или продолжение той, что в работе» — общий для проекта и личного чата
    public const string NewOrContinueRule =
        "Новая картинка или продолжение: просьба с новым сюжетом или предметом («нарисуй собаку», "
        + "«а теперь закат над морем», «сделай логотип») — всегда image_new и image_generate с threadId "
        + "нового черновика, даже если другая картинка в работе. image_generate по картинке в работе — "
        + "только когда просьба явно про неё и нового сюжета не описывает: «поправь», «ещё вариант», "
        + "«сделай ярче», «убери фон», «как вторую, но…». Сомневаешься — новая картинка.";

    // Выбор в полосе важнее правила проекта о сервисе для картинок (решение Андрея, план 21492bb8)
    public const string PriorityRule =
        "Картинки в этом проекте рисуй через image_new → image_generate: они уважают выбор человека в полосе "
        + "(включая local — свою видеокарту) и цену. Этот выбор важнее правил проекта о сервисе для картинок. "
        + "Есть в ходе правило хвостовой секции «Картинки и видео: локальная модель по умолчанию» — сервис и provider выбирай по нему; без него "
        + "fal-ai, glif, higgsfield и local-media для картинок — только если человек в своей просьбе прямо назвал этот сервис. "
        + "Видео, аудио и музыка — как раньше.\n" + NewOrContinueRule;

    // То же для личного чата вне проекта: local-media там нет, локальная картинка — драйвер local
    // редактора, а черновик человек скачивает, а не сохраняет в проект
    public const string PersonalPriorityRule =
        "Картинки в этом чате рисуй через image_new → image_generate: они уважают выбор человека в полосе "
        + "«Картинки» и цену. Просьба нарисовать локально / на своей видеокарте / бесплатно — это "
        + "image_generate с provider local. Есть в ходе правило хвостовой секции «Картинки и видео: локальная модель по умолчанию» — сервис и provider "
        + "выбирай по нему; без него fal-ai, glif и higgsfield для картинок — только если человек "
        + "в своей просьбе прямо назвал этот сервис. Видео, аудио и музыка — как раньше.\n" + NewOrContinueRule;

    // «Выбор человека в полосе «Картинки»: поставщик fal, модель auto, вариантов 2, персонаж anya»
    public static string ChoiceText(ImageThreadSettings settings, string? character) =>
        "Выбор человека в полосе «Картинки»: " + SettingsText(settings) + $", персонаж {character ?? "не подключён"}";

    // С выбором по режимам — оба: «Создать» берёт генерация по тексту, «Править» — правка (у
    // картинки в работе — её настройки). Без режимов — прежняя строка, как у старого фронта
    public static string ChoiceText(Prefs.ImageProjectPrefs prefs, ImageThreadSettings? focused)
    {
        if (!prefs.HasModes)
            return ChoiceText(focused ?? prefs.EditSettings(), prefs.CharacterSlug);
        var edit = SettingsText(focused ?? prefs.EditSettings());
        var op = prefs.Edit?.Op is { } o ? $", операция {o}" : "";
        return "Выбор человека в полосе «Картинки»: "
            + $"новая картинка (генерация по тексту) — {SettingsText(prefs.CreateSettings())}; "
            + $"правка{(focused is null ? "" : " картинки в работе")} — {edit}{op}; "
            + $"персонаж {prefs.CharacterSlug ?? "не подключён"}";
    }

    private static string SettingsText(ImageThreadSettings settings) =>
        $"поставщик {settings.Provider ?? "по умолчанию"}, "
        + $"модель {settings.Model ?? ImageEditCatalog.AutoModelId}, "
        + $"вариантов {settings.Count}";

    public static string DraftFolderText(string? folder) =>
        string.IsNullOrEmpty(folder) ? "корень проекта" : $"папку {folder}";

    // Куда денется черновик: в проекте человек сохранит его в папку, в личном чате — скачает
    public static string DraftSaveText(string? folder, bool personal) =>
        personal ? "человек скачает её" : $"человек сохранит её в {DraftFolderText(folder)}";

    private static string Outcome(ImageEditJobDto? job) => job switch
    {
        null => "",
        { Status: ImageEditJobStatus.Completed } => string.Create(CultureInfo.InvariantCulture,
            $"; итог: {job.Variants.Count} {Variants(job.Variants.Count)}{Cost(job.Cost)}"),
        { Status: ImageEditJobStatus.Failed or ImageEditJobStatus.Interrupted } =>
            "; итог: не получилось" + (string.IsNullOrWhiteSpace(job.Error) ? "" : $" ({job.Error})"),
        { Status: ImageEditJobStatus.Cancelled } => "; итог: отменено",
        _ => "; ещё рисуется",
    };

    private static string Cost(EditCost? cost) => cost is null ? ""
        : string.Create(CultureInfo.InvariantCulture,
            $", {(cost.Unit == ImageEditPriceUnits.Usd ? "$" + cost.Amount.ToString("0.##", CultureInfo.InvariantCulture) : cost.Amount.ToString("0.##", CultureInfo.InvariantCulture) + " кр.")}");

    public static string Variants(int n) => (n % 100 is >= 11 and <= 14) ? "вариантов"
        : (n % 10) switch { 1 => "вариант", 2 or 3 or 4 => "варианта", _ => "вариантов" };
}
