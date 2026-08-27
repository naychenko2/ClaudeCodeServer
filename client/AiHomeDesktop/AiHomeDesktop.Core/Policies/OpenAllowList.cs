namespace AiHomeDesktop.Core.Policies;

/// <summary>Чем оказалась цель desktop_open.</summary>
public enum OpenTargetKind
{
    Unknown,

    /// <summary>Приложение из списка, отмеченного человеком.</summary>
    App,

    /// <summary>Файл внутри разрешённой папки.</summary>
    File,

    /// <summary>Ссылка http/https.</summary>
    Url
}

/// <summary>Решение по цели: что открывать и почему нет, если нет.</summary>
public sealed record OpenDecision(bool Allowed, OpenTargetKind Kind, string? Target, string? Reason)
{
    public static OpenDecision Refuse(string reason) => new(false, OpenTargetKind.Unknown, null, reason);

    public static OpenDecision Allow(OpenTargetKind kind, string target) => new(true, kind, target, null);
}

/// <summary>
/// Allow-list целей desktop_open: приложения и папки отмечает человек, ссылки http/https
/// разрешены как класс.
///
/// Оболочки вычеркнуты — cmd, powershell, pwsh, wt, bash и скриптовые хосты, — но это
/// ГИГИЕНА И СЛЕДЫ, а не граница (ADR-008, «Что реально удерживает агента»): мимо списка
/// едут .lnk и протокольные обработчики, а на третьей волне — ввод в уже открытое окно
/// оболочки. Строить на этом списке гарантий нельзя, и тексты интерфейса не должны обещать
/// безопасность, которой нет.
/// </summary>
public sealed class OpenAllowList(IEnumerable<string>? entries = null)
{
    /// <summary>Имена оболочек и скриптовых хостов — без расширения, в нижнем регистре.</summary>
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "powershell_ise", "wt", "windowsterminal", "conhost",
        "bash", "sh", "zsh", "wsl", "ubuntu", "cscript", "wscript", "mshta", "rundll32", "regsvr32"
    };

    /// <summary>Расширения, которые исполняются оболочкой или скриптовым хостом.</summary>
    private static readonly HashSet<string> ShellExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cmd", ".bat", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta"
    };

    private readonly List<string> _entries = (entries ?? []).
        Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Оболочка ли это по имени или расширению цели.</summary>
    public static bool IsShell(string target)
    {
        var trimmed = target.Trim().Trim('"');
        if (trimmed.Length == 0) return false;

        var name = FileNameOf(trimmed);
        var ext = ExtensionOf(name);
        if (ext.Length > 0 && ShellExtensions.Contains(ext)) return true;

        var bare = ext.Length > 0 ? name[..^ext.Length] : name;
        return ShellNames.Contains(bare);
    }

    /// <summary>Разрешена ли цель. Пустая строка, оболочка и всё, чего нет в списке, — отказ.</summary>
    public OpenDecision Evaluate(string? target)
    {
        var value = (target ?? "").Trim().Trim('"');
        if (value.Length == 0) return OpenDecision.Refuse("Не указано, что открывать");

        // Различаем ссылку и путь по СХЕМЕ, а не по слэшам: у «https://…» слэши есть
        // всегда, и проверка на путь съедала бы все ссылки разом. Однобуквенная схема —
        // это буква диска («C:\work»), её Uri тоже разбирает как схему.
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1
            && !LooksLikeLocalPath(value))
        {
            // Ссылки: только http и https. file:, ms-settings:, javascript: и прочие
            // протокольные обработчики — не «ссылка», а способ запустить что угодно.
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return OpenDecision.Refuse($"Ссылки со схемой {uri.Scheme}: не открываются — разрешены только http и https");
            return OpenDecision.Allow(OpenTargetKind.Url, uri.ToString());
        }

        if (IsShell(value))
            return OpenDecision.Refuse("Командные оболочки и скриптовые хосты из списка вычеркнуты");

        foreach (var entry in _entries)
        {
            // Запись-приложение: совпадение по имени файла (с расширением или без).
            if (!LooksLikeWindowsPath(entry) && MatchesAppName(entry, value))
                return OpenDecision.Allow(OpenTargetKind.App, entry);

            // Запись-путь: сама цель или файл внутри разрешённой папки.
            if (LooksLikeWindowsPath(entry) && IsInside(entry, value))
                return OpenDecision.Allow(OpenTargetKind.File, value);
        }

        return OpenDecision.Refuse(
            "Этой цели нет в списке разрешённых на устройстве. Список отмечает человек в окне клиента AI Home");
    }

    private static bool MatchesAppName(string entry, string target)
    {
        var entryName = FileNameOf(entry);
        var targetName = FileNameOf(target);
        return string.Equals(entryName, targetName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(WithoutExtension(entryName), WithoutExtension(targetName), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Сравнение по сегментам, а не по префиксу строки: «C:\work2» не находится внутри
    /// «C:\work». Разыменование реальных путей — дело Windows-половины, здесь чистая строка.
    /// </summary>
    private static bool IsInside(string root, string candidate)
    {
        var rootParts = Segments(root);
        var parts = Segments(candidate);
        if (rootParts.Length == 0 || parts.Length < rootParts.Length) return false;

        for (var i = 0; i < rootParts.Length; i++)
            if (!string.Equals(rootParts[i], parts[i], StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    private static string[] Segments(string path) =>
        path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Путь машины, а не ссылка: буква диска либо начало с разделителя (UNC).</summary>
    private static bool LooksLikeLocalPath(string value) =>
        value.StartsWith("\\", StringComparison.Ordinal)
        || (value.Length >= 2 && value[1] == ':' && char.IsLetter(value[0]));

    private static bool LooksLikeWindowsPath(string value) =>
        value.Contains('\\') || value.Contains('/')
        || (value.Length >= 2 && value[1] == ':' && char.IsLetter(value[0]));

    private static string FileNameOf(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        var slash = normalized.LastIndexOf('\\');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..] : "";
    }

    private static string WithoutExtension(string name)
    {
        var ext = ExtensionOf(name);
        return ext.Length > 0 ? name[..^ext.Length] : name;
    }
}
