using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Реестр устройств десктопного агента — data/devices.json (ADR-008, «Аутентификация и
/// транспорт»). Хранит метаданные, хеш device-токена, монотонную версию токена и отпечаток
/// машины; секрета токена не хранит никогда.
///
/// Реестр только заводит, проверяет и отзывает устройства. Сопряжение кодом — в
/// <see cref="DevicePairingService"/>, приём токена на границе HTTP — в
/// <see cref="DesktopDeviceAuthHandler"/>.
/// </summary>
public sealed class DeviceRegistry
{
    public const string FileName = "devices.json";

    /// <summary>Длина секрета токена — 256 бит (ADR-008).</summary>
    public const int TokenSecretBytes = 32;

    /// <summary>Как часто LastSeenAt доходит до диска: устройство стучится постоянно.</summary>
    private static readonly TimeSpan SeenPersistInterval = TimeSpan.FromMinutes(1);

    // Опции обязаны совпадать с теми, что читает BackupValidation: разные опции = ложный
    // вердикт при проверке архива
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly ILogger<DeviceRegistry>? _logger;
    private readonly List<DesktopDevice> _devices;
    private readonly Lock _lock = new();
    private DateTime _seenPersistedAt = DateTime.MinValue;

    public DeviceRegistry(IConfiguration config, ILogger<DeviceRegistry>? logger = null)
        : this(DataDirOf(config), logger)
    {
    }

    // Отдельный конструктор по каталогу: юнит-тесты не поднимают конфигурацию
    public DeviceRegistry(string dataDir, ILogger<DeviceRegistry>? logger = null)
    {
        _logger = logger;
        _filePath = Path.Combine(dataDir, FileName);
        _devices = JsonFileStore.Load<List<DesktopDevice>>(_filePath, JsonOptions, logger) ?? [];
    }

    private static string DataDirOf(IConfiguration config) =>
        Path.GetDirectoryName(config["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;

    /// <summary>Все записи владельца, включая отозванные надгробия (снимок).</summary>
    public IReadOnlyList<DesktopDevice> GetByOwner(string ownerId)
    {
        lock (_lock)
            return _devices.Where(d => d.OwnerId == ownerId)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public DesktopDevice? Get(string ownerId, string id)
    {
        lock (_lock)
            return _devices.FirstOrDefault(d => d.OwnerId == ownerId && d.Id == id);
    }

    /// <summary>Живое устройство владельца по человеческому имени — это и есть параметр device у MCP.</summary>
    public DesktopDevice? FindByName(string ownerId, string name)
    {
        var normalized = NormalizeName(name);
        lock (_lock)
            return _devices.FirstOrDefault(d => d.OwnerId == ownerId && !d.Revoked
                && string.Equals(d.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Заводит устройство и выдаёт device-токен (единственный момент, когда секрет существует
    /// в открытом виде). Повторное сопряжение той же машины под тем же именем не плодит
    /// запись, а вращает токен с инкрементом версии — прежний токен умирает.
    /// Нарушение имени/отпечатка — InvalidOperationException с текстом для 400.
    /// </summary>
    public (DesktopDevice Device, string Token) Register(
        string ownerId, string name, string fingerprint, string? clientVersion = null)
    {
        var normalized = NormalizeName(name);
        var nameError = ValidateName(normalized);
        if (nameError is not null) throw new InvalidOperationException(nameError);
        if (!MachineFingerprint.IsValid(fingerprint))
            throw new InvalidOperationException("Отпечаток машины не распознан");

        var (secret, hash) = NewSecret();

        lock (_lock)
        {
            var existing = _devices.FirstOrDefault(d => d.OwnerId == ownerId && !d.Revoked
                && string.Equals(d.Name, normalized, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                // Имя занято другой машиной — молча перевесить его нельзя: человек в чате
                // адресует руки именно по имени
                if (!string.Equals(existing.MachineFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Имя «{normalized}» уже занято другим устройством. Отзови его или выбери другое имя");

                existing.TokenHash = hash;
                existing.TokenVersion++;
                existing.ClientVersion = clientVersion ?? existing.ClientVersion;
                existing.LastSeenAt = DateTime.UtcNow;
                Save();
                return (existing, ComposeToken(existing.Id, existing.TokenVersion, secret));
            }

            var device = new DesktopDevice
            {
                OwnerId = ownerId,
                Name = normalized,
                TokenHash = hash,
                // Версия не начинается с 1 вслепую: если эта машина уже была сопряжена и
                // отозвана, продолжаем её счётчик — иначе старый токен совпал бы по версии
                TokenVersion = NextVersionFor(ownerId, fingerprint),
                MachineFingerprint = fingerprint,
                ClientVersion = clientVersion,
            };
            _devices.Add(device);
            Save();
            return (device, ComposeToken(device.Id, device.TokenVersion, secret));
        }
    }

    /// <summary>Переименование. null — устройства нет; исключение — имя не годится или занято.</summary>
    public DesktopDevice? Rename(string ownerId, string id, string name)
    {
        var normalized = NormalizeName(name);
        var nameError = ValidateName(normalized);
        if (nameError is not null) throw new InvalidOperationException(nameError);

        lock (_lock)
        {
            var device = _devices.FirstOrDefault(d => d.OwnerId == ownerId && d.Id == id);
            if (device is null) return null;

            var taken = _devices.Any(d => d.OwnerId == ownerId && d.Id != id && !d.Revoked
                && string.Equals(d.Name, normalized, StringComparison.OrdinalIgnoreCase));
            if (taken) throw new InvalidOperationException($"Имя «{normalized}» уже занято другим устройством");

            device.Name = normalized;
            Save();
            return device;
        }
    }

    /// <summary>
    /// Отзыв: запись остаётся надгробием, хеш стирается, версия токена растёт. Повторный
    /// отзыв — идемпотентен. false — устройства нет у владельца.
    /// </summary>
    public bool Revoke(string ownerId, string id)
    {
        lock (_lock)
        {
            var device = _devices.FirstOrDefault(d => d.OwnerId == ownerId && d.Id == id);
            if (device is null) return false;
            if (device.Revoked) return true;

            device.Revoked = true;
            device.RevokedAt = DateTime.UtcNow;
            device.TokenHash = "";
            device.TokenVersion++;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Проверяет device-токен и отпечаток машины. null — любой отказ (формат, неизвестное
    /// устройство, отзыв, версия из прошлой выдачи, несовпавший секрет или отпечаток):
    /// различать причины наружу незачем, а внутрь — не по чему.
    /// </summary>
    public DesktopDevice? Authenticate(string? rawToken, string? fingerprint)
    {
        if (!TryParseToken(rawToken, out var deviceId, out var version, out var secret)) return null;
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;

        var hash = HashSecret(secret);
        lock (_lock)
        {
            var device = _devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null || device.Revoked) return null;
            if (device.TokenVersion != version) return null;
            if (!FixedTimeEquals(device.TokenHash, hash)) return null;
            // Отпечаток именно сверяется: украденный токен, приехавший с другой машины,
            // канал не открывает
            if (!string.Equals(device.MachineFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                return null;

            MarkSeenLocked(device);
            return device;
        }
    }

    /// <summary>Нормализация человеческого имени: обрезка и схлопывание пробелов.</summary>
    public static string NormalizeName(string? name) =>
        string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Текст ошибки либо null. Имя видит человек и произносит его в чате — держим коротким.</summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Имя устройства не может быть пустым";
        if (name.Length > 32) return "Имя устройства длиннее 32 символов";
        foreach (var ch in name)
            if (!char.IsLetterOrDigit(ch) && ch is not ('-' or '_' or ' '))
                return "В имени устройства допустимы буквы, цифры, дефис, подчёркивание и пробел";
        return null;
    }

    // Токен: {deviceId}.{версия}.{секрет}. Версия внутри токена — та самая монотонность:
    // по ней токен прошлой выдачи отличим от текущего без хранения истории хешей.
    private static string ComposeToken(string deviceId, int version, string secret) =>
        $"{deviceId}.{version}.{secret}";

    private static bool TryParseToken(string? raw, out string deviceId, out int version, out string secret)
    {
        deviceId = "";
        version = 0;
        secret = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Trim().Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[1], out version) || version <= 0) return false;
        if (parts[0].Length == 0 || parts[2].Length == 0) return false;

        deviceId = parts[0];
        secret = parts[2];
        return true;
    }

    private static (string Secret, string Hash) NewSecret()
    {
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenSecretBytes));
        return (secret, HashSecret(secret));
    }

    internal static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length == 0 || a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }

    // Следующая версия для машины: максимум по всем её записям у владельца (включая
    // отозванные) + 1. Так повторное сопряжение отозванной машины никогда не выдаёт
    // версию, которую уже видел прежний токен.
    private int NextVersionFor(string ownerId, string fingerprint)
    {
        var seen = _devices
            .Where(d => d.OwnerId == ownerId
                && string.Equals(d.MachineFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.TokenVersion)
            .DefaultIfEmpty(0)
            .Max();
        return seen + 1;
    }

    private void MarkSeenLocked(DesktopDevice device)
    {
        device.LastSeenAt = DateTime.UtcNow;
        // На диск — не чаще раза в минуту: устройство стучится ежесекундно, а отметка
        // присутствия не стоит перезаписи стора на каждый запрос
        if (DateTime.UtcNow - _seenPersistedAt < SeenPersistInterval) return;
        _seenPersistedAt = DateTime.UtcNow;
        Save();
    }

    private void Save()
    {
        try
        {
            JsonFileStore.Save(_filePath, _devices, JsonOptions);
        }
        catch (Exception ex)
        {
            // Потеря записи хуже тишины: канал живой, но после рестарта устройство
            // придётся сопрягать заново — это должно быть видно в логе
            _logger?.LogError(ex, "Не удалось сохранить реестр устройств {Path}", _filePath);
            throw;
        }
    }
}

/// <summary>
/// Отпечаток машины: SHA-256 (hex) от имени машины в нижнем регистре. Ровно та же строка
/// считается на клиенте — это контракт сопряжения, менять его в одиночку нельзя.
/// Сложнее (MAC, серийники) намеренно не берём: отпечаток тут не защищает от подделки, а
/// отвечает на один вопрос — «это не та же машина, где крутится бэкенд?».
/// </summary>
public static class MachineFingerprint
{
    public static string OfHost() => Of(Environment.MachineName);

    public static string Of(string machineName) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes((machineName ?? "").Trim().ToLowerInvariant()))).ToLowerInvariant();

    public static bool IsValid(string? fingerprint) =>
        !string.IsNullOrWhiteSpace(fingerprint)
        && fingerprint.Length == 64
        && fingerprint.All(Uri.IsHexDigit);
}
