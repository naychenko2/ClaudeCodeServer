namespace ClaudeHomeServer.Models.Plugins;

/// <summary>
/// Одна проблема валидации пакета плагина: <see cref="Code"/> — машиночитаемый код проверки
/// (например <c>deny-hooks</c>, <c>no-symlink</c>), <see cref="Message"/> — текст для человека.
/// </summary>
public sealed record PluginPackageProblem(string Code, string Message);

/// <summary>
/// Итог валидации пакета плагина (ADR-031 §2.3, шаг 1.3). <see cref="IsValid"/> — пакет
/// соответствует allow-list; <see cref="Problems"/> — список проблем (пусто, если валиден);
/// <see cref="Sha256"/> — хеш пакета (hex) для записи <see cref="PluginInstall"/>.
/// </summary>
public sealed class PluginPackageValidationResult
{
    /// <summary>Пакет валиден (нет проблем).</summary>
    public bool IsValid { get; }

    /// <summary>Проблемы валидации; пусто, если <see cref="IsValid"/>.</summary>
    public IReadOnlyList<PluginPackageProblem> Problems { get; }

    /// <summary>
    /// SHA-256 пакета (hex): детерминированный хеш содержимого всех файлов. Неизменяемость —
    /// обновление пакета = новый hash. Вычисляется всегда (даже при проблемах), чтобы запись
    /// <see cref="PluginInstall"/> имела хеш; использовать его стоит, когда <see cref="IsValid"/>.
    /// </summary>
    public string? Sha256 { get; }

    public PluginPackageValidationResult(
        bool isValid,
        IReadOnlyList<PluginPackageProblem> problems,
        string? sha256)
    {
        IsValid = isValid;
        Problems = problems;
        Sha256 = sha256;
    }
}
