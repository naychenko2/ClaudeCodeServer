using System.Globalization;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ImageEditor.Chats;

// Блок «Картинки в этом чате» в каждом ходе чата проекта с нитями (ADR-019 §3): какая картинка
// в работе, у каждой файл, шаг и ожидающие выбора варианты, плюс журнал «с прошлого сообщения».
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
    ImageThreadStore? threads = null) : IPromptSectionContributor
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
        var block = RenderThreads(state, fresh, jobId => session.ProjectId is { } pid ? jobs?.Get(ownerId, pid, jobId) : null);
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, block, Title, InTurnTail: true)]));
    }

    // Нити картинок чата: «в работе» первой, у каждой файл, шаг и ожидающие варианты
    public static string RenderThreads(ImageThreadsState state, IReadOnlyList<ImageThreadEvent> fresh,
        Func<string, ImageEditJobDto?> job)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Картинки в этом чате");
        sb.AppendLine(state.Focus is null
            ? "В работе: ничего не выбрано"
            : $"В работе: картинка {state.Focus}");
        foreach (var t in state.Threads.OrderByDescending(t => t.Id == state.Focus))
        {
            var what = t.File is { Length: > 0 } file ? $"файл {file}"
                : $"новая картинка, ещё не сохранена (человек сохранит её в {DraftFolderText(t.DraftFolder)})";
            var steps = t.CurrentStack?.Steps ?? [];
            var at = t.CurrentStepId is { } s && steps.Contains(s) ? steps.ToList().IndexOf(s) + 1 : 0;
            var step = steps.Count == 0 ? "шагов нет" : at == 0 ? $"на холсте исходник, шагов {steps.Count}" : $"шаг {at} из {steps.Count}";
            var pending = t.PendingJobId is { } p ? "; варианты ждут выбора человека" + Outcome(job(p))
                : t.InterruptedJobId is not null ? "; последняя задача потеряна при перезапуске сервера, варианты недоступны" : "";
            sb.AppendLine($"- {t.Id}{(t.Id == state.Focus ? " (в работе)" : "")}: {what}; {step}{pending}");
        }
        if (fresh.Count > 0)
        {
            sb.AppendLine("С прошлого сообщения:");
            foreach (var e in fresh)
                sb.AppendLine("- " + e.Text + Outcome(e.JobId is { } id && e.Kind == ImageThreadEventKinds.Launched ? job(id) : null));
        }
        return sb.ToString().TrimEnd();
    }

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
