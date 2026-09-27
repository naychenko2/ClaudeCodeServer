using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Hands;

/// <summary>
/// Что сервер предложил поставить: архив моста той же версии, что у агента (из <see cref="DeviceHelloAck"/>).
/// Хеш приехал по аутентифицированному каналу устройства, сам архив качается анонимной ручкой.
/// </summary>
/// <param name="Path">Путь архива относительно <c>/agent/</c>: <c>{version}/{rid}/{file}</c>.</param>
internal sealed record HandsOffer(string AgentVersion, string Path, string Sha256, long Size);

/// <summary>Запись об установленном компоненте: по ней мост сверяется перед каждым ходом с руками.</summary>
internal sealed record HandsComponentRecord(string AgentVersion, string ArchiveSha256, string BridgeSha256, DateTimeOffset InstalledAt);

/// <summary>Состояние компонента: <see cref="Problem"/> — почему рук нет, текстом для человека.</summary>
internal sealed record HandsComponentCheck(bool Ready, string? Problem, HandsComponentRecord? Record);

/// <summary>Установка компонента не удалась: текст — человеку в консоль.</summary>
internal sealed class HandsInstallException(string message) : Exception(message);

/// <summary>
/// Компонент рук на машине (ADR-016 §7): <c>HandsBridge.exe</c> в <c>{данные агента}/hands/</c>.
/// Ставит и убирает его только человек командой <c>ai-home-agent hands enable|disable</c> — это
/// машинный выключатель рук (решение владельца 1в). Сверка — SHA-256 моста против записи,
/// сделанной при установке из архива, чей хеш прислал сервер; хеш пересчитывается, только когда
/// у файла сменились размер или время записи.
/// </summary>
internal sealed partial class HandsComponent
{
    public const string RecordFileName = "hands.json";
    public const string OfferFileName = "hands-offer.json";

    public const string NotInstalledText = "На устройстве не установлены руки: выполни на нём «ai-home-agent hands enable».";

    public const string NotVerifiedText =
        "Компонент рук на устройстве не прошёл сверку SHA-256 — переустанови его командой «ai-home-agent hands enable».";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _dataDirectory;
    private readonly Lock _lock = new();
    private (long Length, DateTime WriteTime, string Sha)? _hashCache;

    public HandsComponent(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        ComponentDirectory = System.IO.Path.Combine(dataDirectory, HandsFiles.ComponentDirectory);
    }

    public string ComponentDirectory { get; }

    public string BridgePath => System.IO.Path.Combine(ComponentDirectory, HandsFiles.BridgeExe);

    private string RecordFile => System.IO.Path.Combine(_dataDirectory, RecordFileName);

    private string OfferFile => System.IO.Path.Combine(_dataDirectory, OfferFileName);

    /// <summary>Установлен ли компонент и совпадает ли мост с записью установки.</summary>
    public HandsComponentCheck Check()
    {
        var record = ReadJson<HandsComponentRecord>(RecordFile);
        if (record is null || !Sha256Hex().IsMatch(record.BridgeSha256 ?? ""))
            return new(false, NotInstalledText, null);

        FileInfo bridge;
        try
        {
            bridge = new FileInfo(BridgePath);
            if (!bridge.Exists) return new(false, NotInstalledText, record);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, NotInstalledText, record);
        }

        string sha;
        try
        {
            sha = BridgeSha256(bridge);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, NotVerifiedText, record);
        }

        return string.Equals(sha, record.BridgeSha256, StringComparison.OrdinalIgnoreCase)
            ? new(true, null, record)
            : new(false, NotVerifiedText, record);
    }

    public bool IsReady => Check().Ready;

    /// <summary>
    /// Поставить компонент из скачанного архива: размер и SHA-256 архива сверяются с
    /// предложением сервера, мост обязан лежать в корне архива. Старый компонент заменяется
    /// целиком; запись установки пишется последней.
    /// </summary>
    public HandsComponentRecord Install(string archivePath, HandsOffer offer)
    {
        var archive = new FileInfo(archivePath);
        if (!archive.Exists || archive.Length != offer.Size)
            throw new HandsInstallException($"Размер архива рук не совпал с манифестом сервера ({archive.Length} вместо {offer.Size}).");
        var archiveSha = FileSha256(archivePath);
        if (!string.Equals(archiveSha, offer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new HandsInstallException("SHA-256 архива рук не совпал с манифестом сервера — архив не поставлен.");

        Directory.CreateDirectory(_dataDirectory);
        var staging = ComponentDirectory + ".new-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // ExtractToDirectory сам отвергает записи с выходом за каталог назначения
            ZipFile.ExtractToDirectory(archivePath, staging);
            var stagedBridge = System.IO.Path.Combine(staging, HandsFiles.BridgeExe);
            if (!File.Exists(stagedBridge))
                throw new HandsInstallException($"В архиве рук нет {HandsFiles.BridgeExe}.");
            var bridgeSha = FileSha256(stagedBridge);

            // Запись снимается до подмены каталога: на полпути руки считаются неустановленными
            DeleteRecord();
            ReplaceDirectory(staging);
            staging = null;

            var record = new HandsComponentRecord(offer.AgentVersion, archiveSha, bridgeSha, DateTimeOffset.UtcNow);
            WriteJson(RecordFile, record);
            return record;
        }
        catch (InvalidDataException e)
        {
            throw new HandsInstallException($"Архив рук повреждён: {e.Message}");
        }
        finally
        {
            if (staging is not null) TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Убрать компонент. Запись снимается первой — возможность пропадает сразу, даже если файл
    /// моста ещё занят идущим ходом. Возвращает false, если каталог удалить не вышло.
    /// </summary>
    public bool Remove()
    {
        DeleteRecord();
        return TryDeleteDirectory(ComponentDirectory);
    }

    public HandsOffer? ReadOffer() => ReadJson<HandsOffer>(OfferFile);

    /// <summary>Запомнить предложение сервера из ответа на hello; null — сервер компонента не раздаёт.</summary>
    public void SaveOffer(HandsOffer? offer)
    {
        try
        {
            if (offer is null)
            {
                if (File.Exists(OfferFile)) File.Delete(OfferFile);
                return;
            }
            Directory.CreateDirectory(_dataDirectory);
            WriteJson(OfferFile, offer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не записалось — enable скажет «сервер ещё не прислал сведения», агент перепишет на следующем hello
        }
    }

    /// <summary>Предложение из ответа сервера: только полный набор полей и путь без выхода наверх.</summary>
    public static HandsOffer? OfferFrom(DeviceHelloAck ack, string agentVersion) =>
        ack.HandsArchivePath is { } path && IsSafeArchivePath(path)
        && ack.HandsArchiveSha256 is { } sha && Sha256Hex().IsMatch(sha)
        && ack.HandsArchiveSize is > 0
            ? new HandsOffer(agentVersion, path, sha.ToLowerInvariant(), ack.HandsArchiveSize.Value)
            : null;

    /// <summary>Путь архива под <c>/agent/</c>: ровно три сегмента без точек-переходов и разделителей Windows.</summary>
    public static bool IsSafeArchivePath(string path) => ArchivePath().IsMatch(path);

    private string BridgeSha256(FileInfo bridge)
    {
        lock (_lock)
        {
            if (_hashCache is { } c && c.Length == bridge.Length && c.WriteTime == bridge.LastWriteTimeUtc) return c.Sha;
        }
        var sha = FileSha256(bridge.FullName);
        lock (_lock) _hashCache = (bridge.Length, bridge.LastWriteTimeUtc, sha);
        return sha;
    }

    internal static string FileSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private void ReplaceDirectory(string staging)
    {
        if (Directory.Exists(ComponentDirectory))
        {
            var old = ComponentDirectory + ".old-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                Directory.Move(ComponentDirectory, old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new HandsInstallException(
                    "Старый компонент рук занят — вероятно, идёт ход с руками. Дождись его конца и повтори.");
            }
            TryDeleteDirectory(old);
        }
        Directory.Move(staging, ComponentDirectory);
        lock (_lock) _hashCache = null;
    }

    private void DeleteRecord()
    {
        if (File.Exists(RecordFile)) File.Delete(RecordFile);
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}\z")]
    private static partial Regex Sha256Hex();

    // {version}/{rid}/{file}: сегменты — буквы, цифры, точка, дефис, подчёркивание, плюс; первый символ не точка
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}/[A-Za-z0-9][A-Za-z0-9._-]{0,31}/[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")]
    private static partial Regex ArchivePath();
}
