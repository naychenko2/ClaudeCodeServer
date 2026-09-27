using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ClaudeHomeServer.DeviceAgent.Credentials;

/// <summary>
/// Где лежит токен устройства. Токен — единственный секрет агента; в лог он не пишется
/// никогда, в исключения — тоже.
/// </summary>
internal interface IDeviceTokenStore
{
    /// <summary>Человекочитаемо: где хранится (для сообщений агента).</summary>
    string Describe { get; }

    /// <summary>null — токена нет.</summary>
    string? Read(string deviceId);

    void Save(string deviceId, string token);

    void Delete(string deviceId);
}

/// <summary>Хранилище не годится: права шире допустимого, macOS и т. п. Текст — что делать.</summary>
internal sealed class TokenStoreException(string message) : Exception(message);

internal static class DeviceTokenStores
{
    public const string DpapiKind = "dpapi";
    public const string SecretServiceKind = "secret-service";
    public const string FileKind = "file";

    /// <summary>
    /// Выбор при сопряжении (Р6). <paramref name="alwaysOn"/> на Linux — всегда файл 0600: при
    /// загрузке без входа (linger) связка ключей сеанса закрыта, и агент остался бы без токена.
    /// Выбор запоминается в регистрации: иначе агент в сеансе с Secret Service искал бы там
    /// токен, лежащий в файле.
    /// </summary>
    public static (IDeviceTokenStore Store, string? Kind) Choose(string configDirectory, bool alwaysOn)
    {
        if (!alwaysOn || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var store = ForCurrentOs(configDirectory);
            return (store, KindOf(store));
        }
        return (new FileTokenStore(configDirectory), FileKind);
    }

    /// <summary>Хранилище, выбранное при сопряжении; сопряжение до записи выбора — как раньше, по ОС.</summary>
    public static IDeviceTokenStore Open(string? kind, string configDirectory)
    {
        if (kind == FileKind && !OperatingSystem.IsWindows()) return new FileTokenStore(configDirectory);
        if (kind == SecretServiceKind && !OperatingSystem.IsWindows()) return new SecretServiceTokenStore(new SecretToolRunner());
        return ForCurrentOs(configDirectory);
    }

    private static string? KindOf(IDeviceTokenStore store) => store switch
    {
        DpapiTokenStore => DpapiKind,
        SecretServiceTokenStore => SecretServiceKind,
        FileTokenStore => FileKind,
        _ => null,
    };

    /// <summary>
    /// Windows — DPAPI CurrentUser (как клиент ADR-008); Linux — Secret Service (libsecret
    /// через <c>secret-tool</c>), если он доступен, иначе файл 0600 в каталоге 0700;
    /// macOS — Keychain не реализован, агент говорит об этом прямо.
    /// </summary>
    public static IDeviceTokenStore ForCurrentOs(string configDirectory)
    {
        if (OperatingSystem.IsWindows()) return new DpapiTokenStore(Path.Combine(configDirectory, "device-token.bin"));
        if (OperatingSystem.IsMacOS()) return new UnsupportedTokenStore(
            "хранение токена устройства на macOS (Keychain) не реализовано — агент на macOS пока не поддерживается");

        var secretTool = new SecretToolRunner();
        if (SecretServiceTokenStore.IsAvailable(secretTool)) return new SecretServiceTokenStore(secretTool);
        return new FileTokenStore(configDirectory);
    }
}

/// <summary>Windows: блоб DPAPI CurrentUser — расшифровывается только этой учёткой на этой машине.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiTokenStore(string filePath) : IDeviceTokenStore
{
    // Не секрет, а разделитель пространства блобов: чужой DPAPI-блоб этой учётки не разберётся как наш
    private static readonly byte[] Entropy = "AiHomeAgent/device-token/v1"u8.ToArray();

    public string Describe => $"DPAPI (текущий пользователь), {filePath}";

    public string? Read(string deviceId)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var plain = Dpapi.Unprotect(File.ReadAllBytes(filePath), Entropy);
            var text = Encoding.UTF8.GetString(plain);
            var sep = text.IndexOf('\n');
            // Токен привязан к устройству: чужой id — значит, сопряжение было другим
            return sep > 0 && text[..sep] == deviceId ? text[(sep + 1)..] : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public void Save(string deviceId, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var blob = Dpapi.Protect(Encoding.UTF8.GetBytes(deviceId + "\n" + token), Entropy);
        File.WriteAllBytes(filePath, blob);
    }

    public void Delete(string deviceId)
    {
        if (File.Exists(filePath)) File.Delete(filePath);
    }
}

/// <summary>DPAPI без пакета ProtectedData: две функции crypt32.</summary>
[SupportedOSPlatform("windows")]
internal static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] plain, byte[] entropy) => Run(plain, entropy, protect: true);

    public static byte[] Unprotect(byte[] blob, byte[] entropy) => Run(blob, entropy, protect: false);

    private static byte[] Run(byte[] data, byte[] entropy, bool protect)
    {
        var input = Pin(data, out var inputHandle);
        var extra = Pin(entropy, out var entropyHandle);
        var output = default(DATA_BLOB);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastPInvokeError());

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private static DATA_BLOB Pin(byte[] data, out GCHandle handle)
    {
        handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        return new DATA_BLOB { cbData = data.Length, pbData = handle.AddrOfPinnedObject() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

/// <summary>
/// Linux без Secret Service: файл в <c>$XDG_CONFIG_HOME/ai-home-agent/</c>, файл 0600,
/// каталог 0700. Права шире — агент отказывается стартовать и говорит, как починить: токен,
/// который могли прочитать другие, уже нельзя считать секретом.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class FileTokenStore(string directory) : IDeviceTokenStore
{
    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public string FilePath => Path.Combine(directory, "device-token");

    public string Describe => $"файл {FilePath} (0600)";

    public string? Read(string deviceId)
    {
        if (!File.Exists(FilePath)) return null;
        EnsurePrivate();
        var text = File.ReadAllText(FilePath);
        var sep = text.IndexOf('\n');
        return sep > 0 && text[..sep] == deviceId ? text[(sep + 1)..].TrimEnd('\n') : null;
    }

    public void Save(string deviceId, string token)
    {
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory, DirMode);
        EnsureDirectoryPrivate();

        // Сначала пустой файл с правами 0600, потом содержимое: ни мгновения с правами шире
        var temp = FilePath + ".tmp";
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions
               { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = FileMode }))
            stream.Write(Encoding.UTF8.GetBytes(deviceId + "\n" + token + "\n"));
        File.Move(temp, FilePath, overwrite: true);
        EnsurePrivate();
    }

    public void Delete(string deviceId)
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    /// <summary>Отказ, если каталог или файл доступны кому-то кроме владельца.</summary>
    public void EnsurePrivate()
    {
        EnsureDirectoryPrivate();
        if (File.Exists(FilePath) && (File.GetUnixFileMode(FilePath) & GroupOrOther) != 0)
            throw new TokenStoreException(
                $"файл токена устройства {FilePath} доступен не только владельцу — агент не стартует. " +
                $"Почини: chmod 600 '{FilePath}' (и лучше выполни сопряжение заново: токен мог утечь)");
    }

    private void EnsureDirectoryPrivate()
    {
        if (Directory.Exists(directory) && (File.GetUnixFileMode(directory) & GroupOrOther) != 0)
            throw new TokenStoreException(
                $"каталог токена устройства {directory} доступен не только владельцу — агент не стартует. " +
                $"Почини: chmod 700 '{directory}'");
    }
}

/// <summary>Запуск secret-tool (libsecret). Отдельно — чтобы тесты шли без D-Bus.</summary>
internal interface ISecretTool
{
    bool Exists { get; }

    /// <summary>Код выхода, stdout, stderr.</summary>
    (int Code, string Stdout, string Stderr) Run(IReadOnlyList<string> args, string? stdin = null);
}

[UnsupportedOSPlatform("windows")]
internal sealed class SecretToolRunner : ISecretTool
{
    private static readonly string[] Candidates = ["/usr/bin/secret-tool", "/bin/secret-tool", "/usr/local/bin/secret-tool"];

    private readonly string? _path = Candidates.FirstOrDefault(File.Exists);

    public bool Exists => _path is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));

    public (int Code, string Stdout, string Stderr) Run(IReadOnlyList<string> args, string? stdin = null)
    {
        var psi = new ProcessStartInfo(_path!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        // Секрет — только через stdin, в argv его увидел бы любой пользователь машины
        if (stdin is not null) process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (-1, "", "secret-tool не ответил за 10 с");
        }
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

/// <summary>Linux с Secret Service (GNOME Keyring, KWallet): токен лежит в связке ключей сеанса.</summary>
internal sealed class SecretServiceTokenStore(ISecretTool tool) : IDeviceTokenStore
{
    private const string Service = "ai-home-agent";

    public string Describe => "Secret Service (libsecret) сеанса пользователя";

    /// <summary>Доступен ли Secret Service прямо сейчас: пробный поиск отвечает без ошибки.</summary>
    public static bool IsAvailable(ISecretTool tool)
    {
        if (!tool.Exists) return false;
        var (code, _, stderr) = tool.Run(["lookup", "service", Service, "probe", "availability"]);
        // Не найдено — это код 1 без текста ошибки; нет демона — код 1 с текстом D-Bus
        return code is 0 or 1 && string.IsNullOrWhiteSpace(stderr);
    }

    public string? Read(string deviceId)
    {
        var (code, stdout, _) = tool.Run(["lookup", "service", Service, "device", deviceId]);
        return code == 0 && stdout.Length > 0 ? stdout.TrimEnd('\n') : null;
    }

    public void Save(string deviceId, string token)
    {
        var (code, _, stderr) = tool.Run(
            ["store", "--label=AI Home: токен устройства", "service", Service, "device", deviceId], token);
        if (code != 0) throw new TokenStoreException($"Secret Service не сохранил токен устройства: {stderr.Trim()}");
    }

    public void Delete(string deviceId) => tool.Run(["clear", "service", Service, "device", deviceId]);
}

internal sealed class UnsupportedTokenStore(string reason) : IDeviceTokenStore
{
    public string Describe => reason;
    public string? Read(string deviceId) => throw new TokenStoreException(reason);
    public void Save(string deviceId, string token) => throw new TokenStoreException(reason);
    public void Delete(string deviceId) { }
}
