using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChatContext;

// Вертикаль объявляет виды объектов контекста (ADR-023 §2.1). Спина знает только строки Kind
// и вызывает владельца; два провайдера с одним Kind — ошибка сборки реестра.
public interface IContextKindProvider
{
    IReadOnlyList<string> Kinds { get; }

    // Может ли объект этого вида стать основным (ADR-023 §1, «Основным?»). По умолчанию нет: основными
    // бывают только image и audio, остальные виды — лишь референсы
    bool CanBePrimary(string kind) => false;

    // Проверка Ref до записи: объект существует, принадлежит владельцу, путь внутри проекта
    // (ProjectLinkGuard), в личном чате — без проектных путей. null — годится, иначе текст отказа 400
    string? Validate(ContextScope scope, string kind, JsonObject reference);

    // ОДНА функция сводки: её текст и чип строки, и строка «Работаем с» панели, и строка хвоста хода.
    // Missing — объект пропал (чип серый «нет файла», в хвосте — «недоступен»)
    ContextItemSummary Describe(ContextScope scope, ContextItem item);

    // Для основного объекта своего вида: какие референсы принимает операция и как их зовут.
    // op — операция панели (null — режим по умолчанию). Пусто — референсы не принимаются
    IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op);

    // «Чем»: исполнитель и модель для основного объекта своего вида
    // («Авто · локально · Qwen-Image Edit · бесплатно»). null — не применимо
    string? DescribeExecutor(ContextScope scope, ContextItem primary);
}

public sealed record ContextScope(string OwnerId, Session Session, Project? Project);

public sealed record ContextItemSummary(string Label, string? Version, string? Thumb, bool Missing);

public sealed record ContextRoleSpec(string Role, string Title, IReadOnlyList<string> Kinds, IReadOnlyList<string> Ops);
