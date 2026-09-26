using System.Globalization;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ImageEditor.Chats;

// Блок «Состояние редактора» в каждом ходе чата картинки (ADR-018 §2): файл, промпт, чем
// рисовать, образцы, пометки словами и журнал «с прошлого сообщения». Ручной запуск агент
// узнаёт именно отсюда: строку image_launch в ленте модель не видит.
//
// Секция едет хвостом хода ВСЕГДА (PromptSection.InTurnTail): она меняется от хода к ходу, и в
// системном блоке обнуляла бы prefix cache всей истории у любого провайдера, а не только у
// провайдера с RecallInTurnText (ADR-018 §10.4, риск 1 плана v2).
public sealed class ImageEditorStateContributor(
    ImageChatStateStore store,
    IFeatureFlagGate flags,
    IImageEditJobs? jobs = null) : IPromptSectionContributor
{
    public const string SectionKey = "image-editor-state";

    public string Key => SectionKey;
    public string Title => "Состояние редактора картинки";
    // Порядок среди секций хвоста значения почти не имеет; после графа кода (600)
    public int Order => 700;
    public string Group => "misc";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.Session.ImageChat is not null
        && sessionContext.OwnerId is { Length: > 0 } ownerId
        && flags.IsEnabled(ownerId, FeatureFlagKeys.ImageEditor);

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        var session = sessionContext.Session;
        if (session.ImageChat is not { } chat || sessionContext.OwnerId is not { Length: > 0 } ownerId)
            return Task.FromResult<PromptSectionContribution?>(null);

        var (state, fresh) = store.TakeForTurn(ownerId, session.Id);
        var text = Render(chat.CurrentPath, state, fresh, jobId => session.ProjectId is { } projectId
            ? jobs?.Get(ownerId, projectId, jobId)
            : null);
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, text, Title, InTurnTail: true)]));
    }

    public static string Render(string currentPath, ImageChatState state, IReadOnlyList<ImageChatEvent> fresh,
        Func<string, ImageEditJobDto?> job)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Состояние редактора картинки");
        sb.AppendLine($"Файл: {currentPath}");
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
