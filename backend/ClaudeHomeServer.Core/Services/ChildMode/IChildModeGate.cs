using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Детский гейт И1 для MCP и запуска хода (ADR-041, разрез §И1, KM-B3a): allow-list пар
/// (MCP-сервер, инструмент), fail-closed. Для обычного владельца гейт прозрачен (всё разрешено
/// ровно как раньше). Детский владелец получает только пары из <see cref="ChildModeAllowList"/>;
/// владелец с DenyAll — ничего.
/// </summary>
public interface IChildModeGate
{
    /// <summary>
    /// Текст отказа вызова инструмента или null, если вызов разрешён (владелец не ребёнок либо пара
    /// есть в allow-list). Отказ едет content-ошибкой, как у соседних гейтов транспорта (п.9 таблицы И1).
    /// </summary>
    string? Denied(string server, string tool, string? ownerId);

    /// <summary>
    /// Объявлять ли серверу место в конфиге хода (п.10): для не-ребёнка true, для ребёнка —
    /// только если у сервера есть хоть одна пара в allow-list. Решение зависит ТОЛЬКО от владельца
    /// (правило McpToolsetStabilityTests): профиль — свойство пользователя, а не хода.
    /// </summary>
    bool AllowsServer(string? ownerId, string server);

    /// <summary>
    /// Ребёнок ли владелец (профиль включён, в том числе нечитаемый — DenyAll): потребители
    /// внешних источников MCP (реестр владельца) по нему не объявляются вовсе.
    /// </summary>
    bool IsChild(string? ownerId);

    /// <summary>
    /// И1-бис, последний рубеж: сверка полей чата с профилем на запуске хода. Возвращает описание
    /// расхождения (ход не стартует, тихой подстановки нет) либо null, если владелец не ребёнок или
    /// всё сошлось. Профиль с DenyAll — всегда отказ.
    /// </summary>
    string? LaunchMismatch(string? ownerId, Session session);

    /// <summary>
    /// Снимок политики владельца (null — обычный пользователь). Нужен запуску хода, чтобы CLI читал
    /// признак ребёнка на каждом ходу; отдельной зависимостью политика в фабрику не едет.
    /// </summary>
    ChildPolicySnapshot? PolicyFor(string ownerId);
}

/// <summary>
/// Детский allow-list пар (сервер, инструмент). В волне 1 ПУСТ: веб-поиск выключен решением
/// Андрея, а websearch/notes/memory/tasks/wsp/медиа/внешние серверы вне списка. Студия волны 2
/// добавит сюда инструменты image-editor и local-media — без нового гейта (разрез, «Что дальше»).
/// Расширение списка — осознанная правка сторожа ChildModeMcpGuardTests.
/// </summary>
public static class ChildModeAllowList
{
    public static readonly IReadOnlySet<(string Server, string Tool)> Pairs =
        new HashSet<(string, string)>();

    public static bool AllowsServer(string server) => Pairs.Any(p => p.Server == server);

    public static bool Allows(string server, string tool) => Pairs.Contains((server, tool));
}

/// <summary>
/// Режим хода ребёнка. В ChildProfile поля режима нет, поэтому «режим из профиля» (разрез §И1-бис)
/// пока выражен единственным значением политики: Default — разрешения решает автоотказ (KM-B3b),
/// а не «принять правки» и не «обойти».
/// </summary>
public static class ChildModeLaunch
{
    public const ClaudeMode RequiredMode = ClaudeMode.Default;
}
