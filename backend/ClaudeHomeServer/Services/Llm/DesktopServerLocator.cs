using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// index.js встроенного MCP-сервера десктопной грани (ADR-008): рядом с exe (prod)
// или в корне репо (dev) — как у tasks/notes/memory.
public static class DesktopServerLocator
{
    public static string? FindDesktopServerPath()
    {
        var nearExe = Path.Combine(AppContext.BaseDirectory, "mcp", "desktop-server", "index.js");
        if (File.Exists(nearExe)) return nearExe;
        var nearCwd = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(), "..", "..", "mcp", "desktop-server", "index.js"));
        if (File.Exists(nearCwd)) return nearCwd;
        return null;
    }
}

// Контракт слоя устройств для доставки грани в ход (ADR-008, раздел «Два уровня»).
// Живёт рядом с рантаймом ходов, а не в слое устройств: решение о СОСТАВЕ хода принимает
// SessionManager, и зависимость обязана быть односторонней — рантайм знает контракт,
// реализацию (реестр устройств, сеансы рук, выпуск capability-токенов) подставляет DI.
// Реализация не зарегистрирована — грань просто не доставляется, ходы идут как раньше.
public interface IDesktopTurnGate
{
    // Десктопный ли чат (тип чата «Десктопный»). Свойство КОНФИГУРАЦИИ чата,
    // а не хода: от него зависит и состав инструментов, и запрет автопродолжения work-loop.
    bool IsDesktopChat(Session session);

    // Capability-токен грани на ход: audience desktop, claims ownerId + sessionId + deviceId,
    // TTL — минуты. null — грань чату не положена (не десктопный чат, грань выключена
    // в проекте, фич-флаг desktop-agent выключен, устройств у владельца нет).
    // Решение принимается ТОЛЬКО по конфигурации (владелец/проект/чат): активный сеанс рук
    // и онлайн устройства проверяет гейт КАЖДОГО вызова на бэкенде — от них состав
    // tools/list зависеть не смеет (иначе процесс CLI перезапускается между ходами).
    // В отпечаток запуска (BuildLaunchSignature) токен не входит — токены оттуда исключены.
    string? IssueTurnToken(string ownerId, Session session);
}

