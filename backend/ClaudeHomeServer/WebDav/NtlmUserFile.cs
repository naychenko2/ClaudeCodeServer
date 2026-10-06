using System.Text;

namespace ClaudeHomeServer.WebDav;

/// <summary>
/// Файл NTLM_USER_FILE для gss-ntlmssp: единственная точка записи NT-хэшей основного
/// пароля (вариант A разведки f375c276, решение пользователя 30.09.2026). Хэш пишется только
/// в момент, когда открытый пароль виден (вход, смена/сброс, создание, успешный Basic WebDAV),
/// — вызывает его <see cref="Services.UserStore"/>, и больше никто.
///
/// Решение 027fd933 (NT-хэш не хранить) откатано частично и осознанно. Компенсации:
/// файл ВНЕ data/ (в облачный бэкап не едет), права 0600, атомарная перезапись, а Negotiate
/// предлагается только по HTTPS (<see cref="WebDavHandler.OfferNegotiate"/>). В users.json
/// поля NtHash нет по-прежнему.
///
/// Формат — smbpasswd: <c>[DOM\]USER:UID:LM:NT:FLAGS:LCT-…:</c>. gss-ntlmssp берёт ПЕРВУЮ
/// строку, где имя совпало, а домен совпал без учёта регистра или отсутствует в строке; в
/// NTLMv2-хэш домен входит с учётом регистра, причём строка БЕЗ домена считает хэш с ПУСТЫМ
/// доменом, а не с присланным клиентом: Type3 с непустым доменом такая строка не сойдётся.
/// Поэтому строки с доменами идут раньше строки без домена, а на домен — ровно один вариант
/// написания. LM-поле обязано быть валидным hex,
/// но при уровне LM_COMPAT_LEVEL по умолчанию (3) gss-ntlmssp его не читает.
/// </summary>
public sealed class NtlmUserFile
{
    internal const string EnvVar = "NTLM_USER_FILE";
    private const string LmPlaceholder = "AAD3B435B51404EEAAD3B435B51404EE";

    private readonly object _lock = new();
    private readonly ILogger<NtlmUserFile> _logger;
    private readonly bool _isWindows;
    private readonly bool _mechanismPresent;
    private readonly string? _envPath;
    // Имя пользователя (без учёта регистра) → NT-хэш в hex
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Путь файла; null — запись выключена (на Windows всегда: там SSPI).</summary>
    public string? Path { get; }
    public IReadOnlyList<string> Domains { get; }

    public NtlmUserFile(IConfiguration config, ILogger<NtlmUserFile> logger)
        : this(config, logger, OperatingSystem.IsWindows(), DetectMechanism("/etc/gss"),
            Environment.GetEnvironmentVariable(EnvVar))
    {
    }

    internal NtlmUserFile(IConfiguration config, ILogger<NtlmUserFile> logger,
        bool isWindows, bool mechanismPresent, string? envPath)
    {
        _logger = logger;
        _isWindows = isWindows;
        _mechanismPresent = mechanismPresent;
        _envPath = string.IsNullOrWhiteSpace(envPath) ? null : envPath;
        Domains = ResolveDomains(config.GetSection("WebDav:NtlmDomains").Get<string[]>());

        // Писать — только по явному пути из машинного конфига прода, НЕ по переменной среды:
        // NTLM_USER_FILE из юнита наследуют все дочерние процессы, включая дев-стенды из ходов,
        // и такой стенд писал бы свои хэши в боевой файл, а Retain стёр бы строки боевых пользователей
        var configured = config["WebDav:NtlmUserFile"];
        if (isWindows || string.IsNullOrWhiteSpace(configured)) return;
        Path = System.IO.Path.GetFullPath(configured);

        if (_envPath is not null && !PathsEqual(System.IO.Path.GetFullPath(_envPath), Path))
            _logger.LogWarning("WebDav:NtlmUserFile ({Path}) не совпадает с {Env} процесса ({EnvPath}): "
                + "gss-ntlmssp читает переменную среды, NTLM выключен", Path, EnvVar, _envPath);
        else if (_envPath is null)
            _logger.LogWarning("{Env} не задан в среде процесса: хэши пишутся в {Path}, но gss-ntlmssp их не увидит, NTLM выключен",
                EnvVar, Path);
        if (!mechanismPresent)
            _logger.LogInformation("Механизм gss-ntlmssp не найден в /etc/gss: NTLM для WebDAV выключен, только Basic");
        else
            WarnIfUnpatched(ReadPackageVersion("/var/lib/dpkg/status"));

        Load();
    }

    /// <summary>
    /// Есть ли чем проверить NTLM Type3. Windows — SSPI, как и раньше. Прочие платформы — только
    /// при механизме gss-ntlmssp, переменной NTLM_USER_FILE на наш файл и самом файле: иначе
    /// рукопожатие обречено, а Mini-Redirector держится за Negotiate и крутит его по кругу.
    /// </summary>
    public bool NegotiateAvailable => ComputeAvailable(_isWindows, _mechanismPresent, Path, _envPath,
        Path is not null && File.Exists(Path));

    internal static bool ComputeAvailable(bool isWindows, bool mechanismPresent, string? path,
        string? envPath, bool fileExists)
    {
        if (isWindows) return true;
        return mechanismPresent && path is not null && envPath is not null
            && PathsEqual(System.IO.Path.GetFullPath(envPath), path) && fileExists;
    }

    /// <summary>Есть ли у пользователя строка в файле (имя из Type3 — наше имя).</summary>
    public bool Contains(string username)
    {
        lock (_lock) return _hashes.ContainsKey(username);
    }

    /// <summary>Записать хэш открытого пароля. Файл переписывается, только если хэш изменился.</summary>
    public void Record(string username, string password)
    {
        if (Path is null || !IsRepresentable(username)) return;
        var hash = Convert.ToHexString(NtlmHelper.ComputeNtHash(password));
        lock (_lock)
        {
            if (_hashes.TryGetValue(username, out var old) && old == hash) return;
            _hashes[username] = hash;
            WriteLocked();
        }
    }

    /// <summary>Убрать строки пользователя (удаление, переименование).</summary>
    public void Remove(string username)
    {
        if (Path is null) return;
        lock (_lock)
        {
            if (_hashes.Remove(username)) WriteLocked();
        }
    }

    /// <summary>Выбросить строки пользователей, которых больше нет (удалены при остановленном сервере).</summary>
    public void Retain(IEnumerable<string> usernames)
    {
        if (Path is null) return;
        var keep = new HashSet<string>(usernames, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            var stale = _hashes.Keys.Where(k => !keep.Contains(k)).ToList();
            if (stale.Count == 0) return;
            foreach (var k in stale) _hashes.Remove(k);
            WriteLocked();
        }
    }

    internal static string BuildContent(IEnumerable<KeyValuePair<string, string>> hashes, IReadOnlyList<string> domains)
    {
        var sb = new StringBuilder();
        sb.Append("# Сгенерировано ClaudeHomeServer (NtlmUserFile). Руками не править: файл перезаписывается.\n");
        foreach (var (user, hash) in hashes.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var domain in domains)
                AppendLine(sb, $"{domain}\\{user}", hash);
            // Строка без домена — последней: она совпадает с любым доменом и перехватила бы их
            AppendLine(sb, user, hash);
        }
        return sb.ToString();

        static void AppendLine(StringBuilder sb, string name, string hash) =>
            sb.Append(name).Append(":0:").Append(LmPlaceholder).Append(':').Append(hash)
              .Append(":[U          ]:LCT-00000000:\n");
    }

    private void WriteLocked()
    {
        var path = Path!;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
                else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // tmp в том же каталоге + rename: gss-ntlmssp перечитывает файл на каждом Type3 и
            // не должен увидеть его наполовину записанным
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using (var fs = new FileStream(tmp, options))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                    w.Write(BuildContent(_hashes, Domains));
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Сбой записи не должен ронять вход: NTLM просто откатится на Basic
            _logger.LogError(ex, "Не удалось записать {Path}: NTLM для WebDAV может не принять новый пароль", path);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(Path)) return;
            foreach (var line in File.ReadLines(Path!))
            {
                if (line.StartsWith('#')) continue;
                var fields = line.Split(':');
                if (fields.Length < 4 || fields[3].Length != 32) continue;
                var name = fields[0];
                var slash = name.IndexOf('\\');
                _hashes[slash >= 0 ? name[(slash + 1)..] : name] = fields[3].ToUpperInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Не удалось прочитать {Path}", Path);
        }
    }

    private static IReadOnlyList<string> ResolveDomains(string[]? configured)
    {
        // По умолчанию — WORKGROUP и имя хоста заглавными (так NetBIOS-имя шлёт Windows)
        var source = configured is { Length: > 0 }
            ? configured
            : ["WORKGROUP", Environment.MachineName.ToUpperInvariant()];
        return source
            .Select(d => d.Trim())
            .Where(d => d.Length > 0 && IsRepresentable(d))
            // Два написания одного домена бессмысленны: сработает только первое
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Символы-разделители формата и переводы строк в имени сломали бы разбор файла
    private static bool IsRepresentable(string s) =>
        s.Length > 0 && s.IndexOfAny([':', '\\', '\r', '\n', '#']) < 0;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Без патча +ccs2 Windows SSPI (KEY_EXCH без SIGN/SEAL) получает InvalidToken при верном хэше:
    /// при apt upgrade патч слетает молча, поэтому громко пишем об этом при старте.
    /// Установка и откат — docs/operations/remote-access.md.
    /// </summary>
    private void WarnIfUnpatched(string? version)
    {
        if (version is null || IsPatchedVersion(version)) return;
        _logger.LogWarning("gss-ntlmssp {Version} без патча '+ccs2' или новее: Windows SSPI получит InvalidToken при верном хэше "
            + "(KEY_EXCH без SIGN/SEAL). Поставьте пропатченный пакет, см. docs/operations/remote-access.md", version);
    }

    /// <summary>Минимальный номер локальной сборки: +ccs1 без правки Type2 (MsvAvFlags) Windows по-прежнему не пускает.</summary>
    private const int MinCcsBuild = 2;

    internal static bool IsPatchedVersion(string version)
    {
        var m = System.Text.RegularExpressions.Regex.Match(version, @"\+ccs(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= MinCcsBuild;
    }

    /// <summary>Версия пакета gss-ntlmssp из файла статуса dpkg; null — файла или пакета нет.</summary>
    internal static string? ReadPackageVersion(string dpkgStatusPath)
    {
        try
        {
            if (!File.Exists(dpkgStatusPath)) return null;
            var inPackage = false;
            foreach (var line in File.ReadLines(dpkgStatusPath))
            {
                if (line.Length == 0) { inPackage = false; continue; }
                if (line.StartsWith("Package: ", StringComparison.Ordinal))
                    inPackage = line.AsSpan(9).Trim().SequenceEqual("gss-ntlmssp");
                else if (inPackage && line.StartsWith("Version: ", StringComparison.Ordinal))
                    return line[9..].Trim();
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Установлен ли механизм gss-ntlmssp: его регистрирует файл в /etc/gss/mech.d (или /etc/gss/mech).</summary>
    internal static bool DetectMechanism(string gssDir)
    {
        try
        {
            var files = new List<string>();
            var mech = System.IO.Path.Combine(gssDir, "mech");
            if (File.Exists(mech)) files.Add(mech);
            var mechD = System.IO.Path.Combine(gssDir, "mech.d");
            if (Directory.Exists(mechD)) files.AddRange(Directory.EnumerateFiles(mechD));
            return files.Any(f => File.ReadLines(f)
                .Any(l => !l.TrimStart().StartsWith('#') && l.Contains("ntlmssp", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
