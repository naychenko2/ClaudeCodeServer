using System.Text;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Git;

namespace ClaudeHomeServer.Services.Turn;

// Хвост хода «Контекст хода» (ADR-023 §3.1): Где + С чем + Чем + Плюс одним блоком, ровно то, что человек
// видит в строке контекста. Единственная точка сборки: подписи «С чем» и «Плюс» берутся из того же
// ChatContextDto, что отдают ручки (его строит ChatContextDtoBuilder поверх Describe провайдера
// владельца), «Чем» — DescribeExecutor. Секция сама свои подписи не форматирует — она вставляет label
// как есть; это держит TurnContextParityTests. Вертикальные блоки (image-editor-state и др.) при
// флаге худеют и строк «В работе» / «Выбор человека» не повторяют.
//
// Едет хвостом хода всегда (InTurnTail): в системный блок не попадает, неизменный контекст в ход не
// повторяется хешем хвоста. «Где» — ветка без числа изменений: оно меняется каждый ход и раздувало бы хвост.
// «Чем» остаётся в хвосте намеренно (Дополнение 2, Р2): агенту исполнитель нужен для вызова *_generate.
//
// Стор контекста берётся из контейнера лениво, а не конструктором: он засевается через ISessionDirectory
// (SessionManager), а SessionManager сам собирает все контрибьюторы — прямая зависимость замкнула бы круг
// и повесила резолв при старте.
public sealed class TurnContextContributor(
    IServiceProvider services,
    ContextKindRegistry registry,
    IFeatureFlagGate flags,
    IProjectManager projects) : IPromptSectionContributor
{
    public const string SectionKey = "turn-context";

    private IChatContextStore store => services.GetRequiredService<IChatContextStore>();

    public string Key => SectionKey;
    public string Title => "Контекст хода";
    // После графа кода (600), до блоков вертикалей (700+)
    public int Order => 690;
    public string Group => "misc";

    // Существительные известных видов для строки «С чем»; неизвестный вид печатается ключом
    private static readonly Dictionary<string, string> KindNouns = new(StringComparer.Ordinal)
    {
        ["image"] = "картинка",
        ["audio"] = "звук",
        ["image-character"] = "персонаж",
        ["audio-voice"] = "голос",
        ["project-file"] = "файл проекта",
    };

    public const string Footer =
        "Строка контекста видна человеку; это ровно то, что в ней. Основной объект и референсы — входы "
        + "image_generate / audio_generate по умолчанию; явные аргументы инструмента их заменяют.";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.OwnerId is { Length: > 0 } ownerId
        && flags.IsEnabled(ownerId, FeatureFlagKeys.ComposerContextRow)
        && sessionContext.ServerContent
        && (HasContext(ownerId, sessionContext.Session) || HasGit(sessionContext));

    private bool HasContext(string ownerId, Session session)
    {
        var state = store.Get(ownerId, session.Id);
        return state.Primary is not null || state.Refs.Count > 0;
    }

    private static bool HasGit(PromptSessionContext ctx) =>
        ctx.RootPath is { Length: > 0 } root && GitRepo.IsRepo(root);

    private static string? Branch(PromptSessionContext ctx) =>
        ctx.Session.WorktreeBranch is { Length: > 0 } wb ? wb
        : ctx.RootPath is { Length: > 0 } root ? GitRepo.CurrentBranch(root) : null;

    public Task<PromptSectionContribution?> BuildAsync(PromptSessionContext sessionContext, string? turnText)
    {
        if (sessionContext.OwnerId is not { Length: > 0 } ownerId)
            return Task.FromResult<PromptSectionContribution?>(null);
        var session = sessionContext.Session;
        var project = session.ProjectId is { } pid ? projects.GetById(pid) : null;
        var scope = new ContextScope(ownerId, session, project);
        var state = store.Get(ownerId, session.Id);
        var dto = ChatContextDtoBuilder.Build(registry, scope, state);

        var sb = new StringBuilder();
        sb.AppendLine("## Контекст хода");
        if (WhereLine(sessionContext) is { } where) sb.AppendLine(where);

        var hasContext = dto.Primary is not null || dto.Refs.Count > 0;
        if (hasContext)
        {
            sb.AppendLine("С чем: " + (dto.Primary is { } primary ? PrimaryText(primary) : "ничего не выбрано"));
            if (state.Primary is { } p && registry.Find(p.Kind)?.DescribeExecutor(scope, p) is { Length: > 0 } executor)
                sb.AppendLine("Чем: " + executor);
            if (dto.Refs.Count > 0)
            {
                var accepted = state.Primary is { } ap && registry.Find(ap.Kind) is { } owner
                    ? owner.AcceptedRefs(scope, ap, null)
                    : [];
                sb.AppendLine("Плюс: " + string.Join("; ", dto.Refs.Select(r => RefText(r, accepted))));
            }
            sb.AppendLine(Footer);
        }

        var text = sb.ToString().TrimEnd();
        return Task.FromResult<PromptSectionContribution?>(new PromptSectionContribution(
            [new PromptSection(Key, text, Title, InTurnTail: true)]));
    }

    private static string? WhereLine(PromptSessionContext ctx)
    {
        if (!HasGit(ctx) && ctx.Session.WorktreeBranch is null) return null;
        var branch = Branch(ctx);
        var worktree = ctx.Session.WorktreePath is not null ? " (worktree чата)" : "";
        return branch is null ? "Где: HEAD отсоединён" + worktree : $"Где: ветка {branch}{worktree}";
    }

    private static string PrimaryText(ChatContextItemDto item)
    {
        var sb = new StringBuilder();
        sb.Append(Noun(item.Kind));
        if (IdOf(item.Ref) is { } id) sb.Append(' ').Append(id);
        sb.Append(" — ").Append(item.Label);
        if (VersionText(item.Version) is { } version) sb.Append(" · ").Append(version);
        sb.Append(item.Missing ? " (недоступен)" : "");
        sb.Append(item.By == ContextActor.Agent ? " (✦ поставил Claude)" : " (поставил человек)");
        return sb.ToString();
    }

    private static string RefText(ChatContextRefDto item, IReadOnlyList<ContextRoleSpec> accepted)
    {
        var sb = new StringBuilder(item.Label);
        if (VersionText(item.Version) is { } version) sb.Append(" · ").Append(version);
        if (item.Role is null)
            sb.Append(" — ").Append(Noun(item.Kind)).Append(", для тебя (не вход генератора)");
        else if (accepted.FirstOrDefault(a => a.Role == item.Role && a.Kinds.Contains(item.Kind)) is { } spec)
            sb.Append(" — ").Append(spec.Title.ToLowerInvariant()).Append(" (").Append(item.Role).Append(')');
        else
            sb.Append(" — роль ").Append(item.Role).Append(", основной объект её не принимает");
        if (item.Missing) sb.Append(" (недоступен)");
        if (item.By == ContextActor.Agent) sb.Append(" ✦ поставил Claude");
        return sb.ToString();
    }

    private static string Noun(string kind) => KindNouns.GetValueOrDefault(kind, kind);

    // Ref непрозрачен для спины; id нити/слага нужен агенту, чтобы назвать объект в вызове инструмента
    private static string? IdOf(System.Text.Json.Nodes.JsonObject reference) =>
        reference["threadId"] is { } t && t.GetValueKind() == System.Text.Json.JsonValueKind.String ? t.GetValue<string>()
        : reference["slug"] is { } s && s.GetValueKind() == System.Text.Json.JsonValueKind.String ? s.GetValue<string>()
        : null;

    private static readonly Regex VersionNumber = new(@"^v(\d+)$", RegexOptions.Compiled);

    private static string? VersionText(string? version) =>
        string.IsNullOrWhiteSpace(version) ? null
        : VersionNumber.Match(version) is { Success: true } m ? "версия " + m.Groups[1].Value
        : version;
}
