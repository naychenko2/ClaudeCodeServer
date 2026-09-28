using System.Globalization;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ImageEditor.Chats;

// Блок «Картинки в этом чате» в каждом ходе чата проекта с нитями (ADR-019 §3): какая картинка
// в работе, у каждой файл, версии (id, «версия N», какая текущая, откуда выросла — чтобы агент
// понимал «поправь предыдущую / вторую»), идущие запуски, плюс журнал «с прошлого сообщения».
// Ручной запуск агент узнаёт именно отсюда: тихую строку image_launch в ленте модель не видит.
// Нить в транскрипт CLI не входит, поэтому после компакции и --resume модель узнаёт о ней только
// отсюда. Чат без нитей блока не получает.
//
// Секция едет хвостом хода ВСЕГДА (PromptSection.InTurnTail): она меняется от хода к ходу, и в
// системном блоке обнуляла бы prefix cache всей истории у любого провайдера, а не только у
// провайдера с RecallInTurnText (ADR-018 §10.4, риск 1 плана v2).
public sealed class ImageEditorStateContributor(
    IFeatureFlagGate flags,
    IImageEditJobs? jobs = null,
    ImageThreadStore? threads = null,
    Prefs.ImageProjectPrefsService? prefs = null) : IPromptSectionContributor
{
    public const string SectionKey = "image-editor-state";

    public string Key => SectionKey;
    public string Title => "Картинки в этом чате";
    // Порядок среди секций хвоста значения почти не имеет; после графа кода (600)
    public int Order => 700;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is { Length: > 0 } ownerId
        && HasThreads(ownerId, sessionContext.Session)
        && flags.IsEnabled(ownerId, FeatureFlagKeys.ImageEditor);

    private bool HasThreads(string ownerId, Session session) =>
        threads is not null && session.ProjectId is not null && threads.Get(ownerId, session.Id).Threads.Count > 0;

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        var session = sessionContext.Session;
        if (sessionContext.OwnerId is not { Length: > 0 } ownerId || threads is null || !HasThreads(ownerId, session))
            return Task.FromResult<PromptSectionContribution?>(null);

        var (state, fresh) = threads.TakeForTurn(ownerId, session.Id);
        var projectPrefs = session.ProjectId is { } projectId ? prefs?.Get(ownerId, projectId) : null;
        var block = RenderThreads(state, fresh, jobId => session.ProjectId is { } pid ? jobs?.Get(ownerId, pid, jobId) : null,
            projectPrefs);
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, block, Title, InTurnTail: true)]));
    }

    // Нити картинок чата: «в работе» первой, у каждой файл, шаг и ожидающие варианты
    // prefs — выбор человека в полосе «Картинки» проекта: для картинки в работе показываются её
    // настройки (а без них и без фокуса — проекта), персонаж всегда из проекта
    public static string RenderThreads(ImageThreadsState state, IReadOnlyList<ImageThreadEvent> fresh,
        Func<string, ImageEditJobDto?> job, Prefs.ImageProjectPrefs? prefs = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Картинки в этом чате");
        sb.AppendLine(state.Focus is null
            ? "В работе: ничего не выбрано"
            : $"В работе: картинка {state.Focus}");
        if (prefs is not null)
        {
            var focused = state.Threads.FirstOrDefault(t => t.Id == state.Focus);
            sb.AppendLine(ChoiceText(focused?.Settings ?? prefs.ToThreadSettings(), prefs.CharacterSlug));
            sb.AppendLine(ChoiceRule);
        }
        foreach (var t in state.Threads.OrderByDescending(t => t.Id == state.Focus))
        {
            var what = t.File is { Length: > 0 } file ? $"файл {file}"
                : $"новая картинка, ещё не сохранена (человек сохранит её в {DraftFolderText(t.DraftFolder)})";
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

    // «Выбор человека в полосе «Картинки»: поставщик fal, модель auto, вариантов 2, персонаж anya»
    public static string ChoiceText(ImageThreadSettings settings, string? character) =>
        "Выбор человека в полосе «Картинки»: "
        + $"поставщик {settings.Provider ?? "по умолчанию"}, "
        + $"модель {settings.Model ?? ImageEditCatalog.AutoModelId}, "
        + $"вариантов {settings.Count}, "
        + $"персонаж {character ?? "не подключён"}";

    public static string DraftFolderText(string? folder) =>
        string.IsNullOrEmpty(folder) ? "корень проекта" : $"папку {folder}";

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
