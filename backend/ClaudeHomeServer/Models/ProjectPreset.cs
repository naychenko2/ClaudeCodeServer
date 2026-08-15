namespace ClaudeHomeServer.Models;

/// <summary>
/// Зарезервированные значения <see cref="Project.PresetKey"/> — дискриминатора «нового
/// проекта» для каркаса знакомства v2. Каталог пресетов (п.2 плана) обязан держать свои
/// ключи вне <see cref="ReservedKeys"/>: иначе логика идемпотентности эндпоинта применения
/// (409 при PresetKey != pending) примет отказ или предложение за пресет.
/// </summary>
public static class ProjectPreset
{
    // Новый проект (ставит ProjectManager.Create) — каркас можно предложить
    public const string Pending = "pending";

    // Человек отказался от каркаса — больше не предлагаем
    public const string None = "none";

    public static readonly IReadOnlySet<string> ReservedKeys =
        new HashSet<string>([Pending, None], StringComparer.Ordinal);

    // Служебное значение PresetKey (не ключ пресета)? Ключи каталога пресетов сюда попадать не должны
    public static bool IsReserved(string? key) => key is not null && ReservedKeys.Contains(key);
}
