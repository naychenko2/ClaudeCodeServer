using System.Globalization;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ImageEditor.Chats;

// Блок «Состояние редактора» в каждом ходе чата картинки (ADR-018 §2): файл, промпт, чем
// рисовать, образцы, пометки словами и журнал «с прошлого сообщения». Ручной запуск агент
// узнаёт именно отсюда: строку image_launch в ленте модель не видит.
//
// Секция едет хвостом хода ВСЕГДА (PromptSection.InTurnTail): она меняется от хода к ходу, и в
// системном блоке обнуляла бы prefix cache всей истории у любого провайдера, а не только у
// провайдера с RecallInTurnText (ADR-018 §10.4, риск 1 плана v2).
//
// В обычном чате проекта (ADR-019 §3) тот же блок перечисляет нити картинок: какая в работе,
// файл, шаг, ожидающие выбора варианты и журнал. Нить в транскрипт CLI не входит, поэтому после
// компакции и --resume модель узнаёт о ней только отсюда. Чат без нитей блока не получает.
public sealed class ImageEditorStateContributor(
    ImageChatStateStore store,
    IFeatureFlagGate flags,
    IImageEditJobs? jobs = null,
    ImageThreadStore? threads = null) : IPromptSectionContributor
{
    public const string SectionKey = "image-editor-state";

    public string Key => SectionKey;
    public string Title => "Состояние редактора картинки";
    // Порядок среди секций хвоста значения почти не имеет; после графа кода (600)
    public int Order => 700;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is { Length: > 0 } ownerId
        && (sessionContext.Session.ImageChat is not null || HasThreads(ownerId, sessionContext.Session))
        && flags.IsEnabled(ownerId, FeatureFlagKeys.ImageEditor);

    private bool HasThreads(string ownerId, Session session) =>
        threads is not null && session.ProjectId is not null && threads.Get(ownerId, session.Id).Threads.Count > 0;

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        var session = sessionContext.Session;
        if (sessionContext.OwnerId is not { Length: > 0 } ownerId)
            return Task.FromResult<PromptSectionContribution?>(null);
        if (session.ImageChat is not { } chat)
        {
            if (threads is null || !HasThreads(ownerId, session))
                return Task.FromResult<PromptSectionContribution?>(null);
            var (threadState, freshEvents) = threads.TakeForTurn(ownerId, session.Id);
            var block = RenderThreads(threadState, freshEvents, jobId => session.ProjectId is { } pid ? jobs?.Get(ownerId, pid, jobId) : null);
            return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
                [new PromptSection(Key, block, Title, InTurnTail: true)]));
        }

        var (state, fresh) = store.TakeForTurn(ownerId, session.Id);
        var text = Render(chat.CurrentPath, state, fresh, jobId => session.ProjectId is { } projectId
            ? jobs?.Get(ownerId, projectId, jobId)
            : null, chat.DraftFolder);
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, text, Title, InTurnTail: true)]));
    }

    // currentPath == null — черновик «Нарисовать картинку»: файла ещё нет, есть папка назначения
    public static string Render(string? currentPath, ImageChatState state, IReadOnlyList<ImageChatEvent> fresh,
        Func<string, ImageEditJobDto?> job, string? draftFolder = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Состояние редактора картинки");
        sb.AppendLine(currentPath is { Length: > 0 } ? $"Файл: {currentPath}"
            : state.CurrentStepId is { Length: > 0 }
                ? $"Файл: ещё не сохранён — новая картинка уже открыта в редакторе, человек сохранит её в "
                  + $"{DraftFolderText(draftFolder)}. image_generate правит открытую картинку"
                : $"Файл: картинки ещё нет — это новая картинка, человек сохранит её в {DraftFolderText(draftFolder)}. "
                  + "image_generate нарисует её по тексту");
        sb.AppendLine(string.IsNullOrWhiteSpace(state.Prompt)
            ? "Промпт: пусто"
            : $"Промпт ({(state.PromptAuthor == ImageEditInitiator.Agent ? "написал ты" : "написал человек")}): «{state.Prompt.Trim()}»");

        var drawer = state.Provider is { Length: > 0 } provider
            ? $"{provider} · {state.Model ?? "авто"}"
            : "по умолчанию";
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Чем рисовать: {drawer} · режим {state.Mode.ToString().ToLowerInvariant()} · вариантов: {state.Count}"));
        if (state.References.Count > 0)
            sb.AppendLine("Образцы: " + string.Join("; ", state.References.Select(r => $"{r.Path} ({RoleName(r.Role)})")));
        if (!string.IsNullOrWhiteSpace(state.CharacterSlug))
            sb.AppendLine($"Персонаж: {state.CharacterSlug}");
        var marks = state.Marks is { } m ? EditMarksPrompt.Describe(m.GetRawText()) : "";
        if (!string.IsNullOrWhiteSpace(marks))
            sb.AppendLine("Пометки на холсте: " + marks.Trim());

        if (fresh.Count > 0)
        {
            sb.AppendLine("С прошлого сообщения:");
            foreach (var e in fresh)
                sb.AppendLine("- " + e.Text + Outcome(e.JobId is { } id ? job(id) : null));
        }
        return sb.ToString().TrimEnd();
    }

    // Нити картинок обычного чата: «в работе» первой, у каждой файл, шаг и ожидающие варианты
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
            var pending = t.PendingJobId is { } p ? "; варианты ждут выбора человека" + Outcome(job(p)) : "";
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

    private static string RoleName(ReferenceRole role) => role switch
    {
        ReferenceRole.Character => "персонаж",
        ReferenceRole.Style => "стиль",
        _ => "объект",
    };
}
