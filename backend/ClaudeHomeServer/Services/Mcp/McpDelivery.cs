using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Чистое правило доставки сервера личного реестра в ход (allow-модель — единственная).
/// Вынесено отдельно от <see cref="SessionManager"/>,
/// чтобы OR-матрицу комбинаций гонять юнитами без поднимания сессии.
///
/// Правило (docs/research/mcp-allowlist-plan.md, раздел «Правило доставки»):
/// сервер едет в ход, если включён В ПРОЕКТЕ этого чата ИЛИ выдан ПЕРСОНЕ этого чата.
/// Чат вне проекта без персоны — только по <see cref="McpServerRecord.AllowOutsideProjects"/>.
/// Поверх — AND-гейт <see cref="McpServerRecord.AllowReadOnlyPersonas"/> для персон с профилем
/// «Только чтение»: он НЕ поглощается allow-list (выдача в проект широковещательна и иначе
/// вернула бы RO-персонам пишущие внешние инструменты).
///
/// Инвариант стабильности состава tools/list: все входы условия — свойства
/// owner/project/persona/записи реестра, ни один не смотрит на ход.
/// </summary>
public static class McpDelivery
{
    public static bool ShouldDeliver(
        McpServerRecord record,
        IReadOnlyCollection<string>? projectServersOn,
        bool isProjectChat,
        bool personaGranted,
        bool readOnly)
    {
        // Ось 0 — рубильник самой записи реестра (как в deny-модели)
        if (!record.Enabled) return false;

        // allow-list: включён «здесь» ИЛИ выдан персоне чата.
        // «Здесь» для проектного чата — ключ в McpServersOn; для внепроектного —
        // AllowOutsideProjects самой записи. Обе оси — не свойства хода.
        var grantedHere = isProjectChat
            ? projectServersOn is { Count: > 0 }
                && projectServersOn.Contains(record.Key, StringComparer.OrdinalIgnoreCase)
            : record.AllowOutsideProjects;
        if (!grantedHere && !personaGranted) return false;

        // Профиль «Только чтение»: имён инструментов чужого сервера мы не знаем, решение
        // принимается ЦЕЛИКОМ по серверу. AND-гейт поверх allow-list — без него RO-персона
        // получала бы произвольные (в т.ч. пишущие) внешние инструменты по проектной оси.
        if (readOnly && !record.AllowReadOnlyPersonas) return false;

        return true;
    }
}
