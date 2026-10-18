namespace ClaudeHomeServer.Models;

// Рубильник «медиа-панель для обычных пользователей» (ADR-030, изменение правила приватности
// 2026-10-18): секция LocalModelStats, ключ MediaForUsers, по умолчанию true.
// false — не-админ получает 404 на /stats/media и /history, как будто функции нет (админ не
// меняется). Читается ОДНОЙ точкой: связывание секции в Program.cs, не инлайн config.GetValue
// в контроллерах.
public class LocalModelStatsMediaOptions
{
    public const string Section = "LocalModelStats";

    // true (по умолчанию) — не-админ видит вкладку «Медиа» со своими генерациями;
    // false — не-админ получает 404, как будто функции нет.
    public bool MediaForUsers { get; set; } = true;
}
