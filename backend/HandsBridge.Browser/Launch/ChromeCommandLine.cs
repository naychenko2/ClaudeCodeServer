using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.HandsBridge.Browser.Launch;

/// <summary>
/// Командная строка Chrome браузерной руки (ADR-016 §7.1). CDP идёт только по двум анонимным
/// трубам (<c>--remote-debugging-io-pipes</c>, недокументированный ключ, проверен пробником на
/// Chrome 153): порта отладки нет, и чужой процесс машины к браузеру не подключится.
/// </summary>
public static partial class ChromeCommandLine
{
    /// <summary>Стартовая страница: пустая, модель сама решает, куда идти.</summary>
    public const string StartUrl = "about:blank";

    /// <summary>
    /// Аргументы без имени программы. <paramref name="toChromeHandle"/> Chrome читает,
    /// в <paramref name="fromChromeHandle"/> пишет (AdoptPipes в devtools_agent_host_impl.cc).
    /// </summary>
    /// <remarks>
    /// <paramref name="uiLanguage"/> — язык интерфейса Windows владельца (BCP 47, например
    /// <c>ru-RU</c>), его мост берёт у WinAPI сам, не у модели и не у сервера. Без явного
    /// <c>--lang</c> свежий профиль выбирал язык сам и слал сайтам чужой <c>Accept-Language</c>
    /// (живой прогон Ш8: example.org пришёл на арабском). Непохожее на тег значение
    /// отбрасывается — тогда Chrome решает по-своему, как раньше.
    /// </remarks>
    public static IReadOnlyList<string> Arguments(string profileDirectory, string toChromeHandle, string fromChromeHandle,
        string? uiLanguage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(toChromeHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromChromeHandle);

        return
        [
            "--remote-debugging-pipe",
            $"--remote-debugging-io-pipes={toChromeHandle},{fromChromeHandle}",
            $"--user-data-dir={profileDirectory}",
            "--no-first-run",
            "--no-default-browser-check",
            // После KillTree иначе каждый запуск всплывает «Chrome завершил работу некорректно»
            "--hide-crash-restore-bubble",
            .. LanguageArguments(uiLanguage),
            StartUrl,
        ];
    }

    /// <summary>
    /// <c>--lang</c> (язык интерфейса) и <c>--accept-lang</c> (заголовок сайтам): тег с регионом
    /// дополняется голым языком — <c>ru-RU,ru</c>.
    /// </summary>
    public static IReadOnlyList<string> LanguageArguments(string? uiLanguage)
    {
        if (uiLanguage is null || !LanguageTag().IsMatch(uiLanguage))
            return [];

        var primary = uiLanguage.Split('-')[0];
        var accept = primary.Length == uiLanguage.Length ? uiLanguage : $"{uiLanguage},{primary}";
        return [$"--lang={uiLanguage}", $"--accept-lang={accept}"];
    }

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageTag();

    /// <summary>
    /// Строка для <c>CreateProcess</c>: имя программы всегда в кавычках (у нулевого аргумента
    /// свои правила разбора — слеши буквальные, кавычки внутри невозможны), остальное —
    /// по правилам <c>CommandLineToArgvW</c>.
    /// </summary>
    public static string Build(string chromePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chromePath);
        if (chromePath.Contains('"'))
            throw new ArgumentException("Chrome path must not contain quotes", nameof(chromePath));

        var sb = new StringBuilder().Append('"').Append(chromePath).Append('"');
        foreach (var argument in arguments)
            sb.Append(' ').Append(Quote(argument));
        return sb.ToString();
    }

    /// <summary>Один аргумент так, чтобы <c>CommandLineToArgvW</c> вернул его без изменений.</summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return argument;

        var sb = new StringBuilder("\"");
        for (var i = 0; i < argument.Length; i++)
        {
            var slashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                slashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // Слеши перед закрывающей кавычкой удваиваются
                sb.Append('\\', slashes * 2);
                break;
            }

            if (argument[i] == '"')
                sb.Append('\\', slashes * 2 + 1).Append('"');
            else
                sb.Append('\\', slashes).Append(argument[i]);
        }

        return sb.Append('"').ToString();
    }
}
