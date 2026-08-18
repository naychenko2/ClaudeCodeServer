using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Deploy;

// Валидация всего, что приходит снаружи и способно повлиять на запуск кода на хосте.
// Эндпоинт деплоя — граница привилегий (ADR-010), поэтому проверки белым списком:
// разрешено только перечисленное, всё прочее — отказ.
public static partial class DeployValidation
{
    /// <summary>Белый список ADR-010 для git-ref: буквы, цифры, точка, подчёркивание, слэш, дефис.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._/-]{1,100}$")]
    private static partial Regex RefPattern();

    /// <summary>Имя задачи планировщика — единственное значение конфига, уходящее в argv.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._\\ -]{1,120}$")]
    private static partial Regex TaskNamePattern();

    /// <summary>Идентификатор сборки из файла релиза — уезжает в HTTP-заголовок X-Build.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex BuildIdPattern();

    /// <summary>
    /// Допустим ли git-ref. Кроме белого списка ADR-010 отсекаем то, что список пропускает,
    /// но чем можно навредить: ведущий дефис (значение прикинулось бы ключом командной
    /// строки git у агента) и «..» (и обход пути, и запрещённая самим git последовательность
    /// в имени ссылки). Пустая строка допустима как «не задан» — это дефолтная ветка.
    /// </summary>
    public static bool IsValidRef(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!RefPattern().IsMatch(value)) return false;
        if (value.StartsWith('-')) return false;
        if (value.Contains("..")) return false;
        if (value.StartsWith('/') || value.EndsWith('/')) return false;
        if (value.EndsWith(".lock")) return false;
        return true;
    }

    public static bool IsValidTaskName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && TaskNamePattern().IsMatch(value) && !value.StartsWith('-');

    /// <summary>
    /// Годен ли идентификатор сборки для заголовка. Строгий набор символов тут не
    /// вкусовщина: содержимое файла с диска попадает в HTTP-заголовок, и CR/LF в нём
    /// означал бы инъекцию заголовков.
    /// </summary>
    public static bool IsValidBuildId(string? value) =>
        !string.IsNullOrEmpty(value) && BuildIdPattern().IsMatch(value);

    /// <summary>Идентификатор выкатки: UTC-штамп, он же имя папки снимка релиза.</summary>
    public static string NewDeployId(DateTime utcNow) => utcNow.ToString("yyyyMMdd-HHmmss");
}
