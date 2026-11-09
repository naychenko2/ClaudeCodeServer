namespace ClaudeHomeServer.Services.Plugins;

/// <summary>
/// Одна проблема валидации пакета плагина: <see cref="Code"/> — машиночитаемый код проверки
/// (например <c>deny-hooks</c>, <c>no-symlink</c>, <c>deny-manifest-field</c>),
/// <see cref="Message"/> — текст для человека.
/// </summary>
public sealed record PluginPackageProblem(string Code, string Message);

/// <summary>
/// Итог валидации пакета плагина (ADR-031 §2.3, шаг 1.3, rework C3/C4/M9).
/// <see cref="IsValid"/> — пакет соответствует allow-list; <see cref="Problems"/> — список
/// проблем (пусто, если валиден); <see cref="Sha256"/> — хеш пакета (hex) для записи
/// <see cref="ClaudeHomeServer.Models.PluginInstall"/>; <see cref="ScannedFileCount"/> —
/// сколько файлов перечислено до обрыва по лимиту (M9).
///
/// Живёт в <c>ClaudeHomeServer.Services.Plugins</c> (не в <c>Models.Plugins</c>): сторож
/// границ (SubsystemBoundaryTests) сканирует только namespace <c>Services.*</c>, поэтому
/// тип результата валидатора должен быть под ним, чтобы кросс-вертикальные ссылки ловились.
/// </summary>
public sealed class PluginPackageValidationResult
{
    /// <summary>Пакет валиден (нет проблем).</summary>
    public bool IsValid { get; }

    /// <summary>Проблемы валидации; пусто, если <see cref="IsValid"/>.</summary>
    public IReadOnlyList<PluginPackageProblem> Problems { get; }

    /// <summary>
    /// SHA-256 пакета (hex): детерминированный хеш содержимого всех файлов. Неизменяемость —
    /// обновление пакета = новый hash. Вычисляется только для валидного пакета (M9: до лимита);
    /// для невалидного — <c>null</c>.
    /// </summary>
    public string? Sha256 { get; }

    /// <summary>
    /// Сколько файлов перечислено до обрыва по лимиту (M9): <c>MaxFileCount</c> /
    /// <c>MaxTotalSizeBytes</c> проверяются во время обхода, не после. Для валидного пакета
    /// равно общему числу файлов; при обрыве — числу, перечисленному до срабатывания лимита.
    /// </summary>
    public int ScannedFileCount { get; }

    public PluginPackageValidationResult(
        bool isValid,
        IReadOnlyList<PluginPackageProblem> problems,
        string? sha256,
        int scannedFileCount)
    {
        IsValid = isValid;
        Problems = problems;
        Sha256 = sha256;
        ScannedFileCount = scannedFileCount;
    }
}
