using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Desktop;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Каталог релизов агента во временной папке (agent-distribution Р3): версии с архивами и
/// манифестами плюс указатель текущей выкатки — ровно та раскладка, что пишет выкатка.
/// </summary>
public sealed class AgentReleaseFixture : IDisposable
{
    public const string Version = "1.200.0";
    public const string WinFile = "ai-home-agent-1.200.0-win-x64.zip";
    public const string LinuxFile = "ai-home-agent-1.200.0-linux-x64.tar.gz";

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ccs_agent_rel_" + Guid.NewGuid().ToString("N"));
    public string Root => Path.Combine(Dir, "agent-releases");
    public string PointerPath => Path.Combine(Dir, "app", AgentReleaseCatalog.PointerFileName);

    public byte[] WinBytes { get; } = Encoding.UTF8.GetBytes("архив агента под windows");
    public byte[] LinuxBytes { get; } = Encoding.UTF8.GetBytes("архив агента под linux, другой");

    public AgentReleaseFixture(bool publish = true)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(PointerPath)!);
        if (!publish) return;
        WriteRelease(Version, (DeviceAgentRids.WinX64, WinFile, WinBytes), (DeviceAgentRids.LinuxX64, LinuxFile, LinuxBytes));
        WritePointer(Version);
    }

    /// <summary>Каталог версии: архивы и manifest.json с их размером и SHA-256.</summary>
    public string WriteRelease(string version, params (string Rid, string File, byte[] Bytes)[] archives)
    {
        var dir = Path.Combine(Root, version);
        Directory.CreateDirectory(dir);
        foreach (var a in archives) File.WriteAllBytes(Path.Combine(dir, a.File), a.Bytes);
        var json = ManifestJson(version, archives.Select(a => (a.Rid, a.File, (long)a.Bytes.Length, Sha(a.Bytes))).ToArray());
        File.WriteAllText(Path.Combine(dir, AgentReleaseCatalog.ManifestFileName), json);
        return json;
    }

    /// <summary>Манифест руками — для битых вариантов.</summary>
    public void WriteManifest(string version, string json)
    {
        Directory.CreateDirectory(Path.Combine(Root, version));
        File.WriteAllText(Path.Combine(Root, version, AgentReleaseCatalog.ManifestFileName), json);
    }

    /// <summary>Указатель = копия манифеста версии; mtime сдвигается, чтобы кэш каталога увидел смену.</summary>
    public void WritePointer(string version) =>
        WritePointerJson(File.ReadAllText(Path.Combine(Root, version, AgentReleaseCatalog.ManifestFileName)));

    public void WritePointerJson(string json)
    {
        var before = File.Exists(PointerPath) ? File.GetLastWriteTimeUtc(PointerPath) : DateTime.MinValue;
        File.WriteAllText(PointerPath, json);
        // Грубое разрешение mtime на части ФС: две записи подряд могли бы получить одну метку
        var after = File.GetLastWriteTimeUtc(PointerPath);
        if (after <= before) File.SetLastWriteTimeUtc(PointerPath, before.AddSeconds(1));
    }

    public static string ManifestJson(string version, params (string Rid, string File, long Size, string Sha256)[] archives) =>
        JsonSerializer.Serialize(new
        {
            version,
            archives = archives.ToDictionary(a => a.Rid, a => new { file = a.File, size = a.Size, sha256 = a.Sha256 }),
        });

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public Dictionary<string, string?> Settings() => new()
    {
        [AgentReleaseCatalog.ReleasesPathKey] = Root,
        [AgentReleaseCatalog.PointerPathKey] = PointerPath,
    };

    public IConfiguration Config(bool withRoot = true)
    {
        var settings = Settings();
        if (!withRoot) settings.Remove(AgentReleaseCatalog.ReleasesPathKey);
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch { /* временная папка */ }
    }
}

/// <summary>
/// Файловая система каталога релизов с журналом: какие пути трогались и сколько раз читались
/// файлы. Работает по-настоящему с диском — журнал нужен для доказательства «до диска не дошло».
/// </summary>
public sealed class RecordingReleaseFileSystem : IAgentReleaseFileSystem
{
    private readonly DiskAgentReleaseFileSystem _disk = new();

    public ConcurrentQueue<(string Op, string Path)> Calls { get; } = new();

    public void Reset() => Calls.Clear();

    public IReadOnlyList<string> Paths => Calls.Select(c => c.Path).ToList();

    public int Count(string op) => Calls.Count(c => c.Op == op);

    public DateTime? LastWriteTimeUtc(string path) { Calls.Enqueue(("stat", path)); return _disk.LastWriteTimeUtc(path); }

    public bool DirectoryExists(string path) { Calls.Enqueue(("dir?", path)); return _disk.DirectoryExists(path); }

    public IEnumerable<string> DirectoryNames(string path) { Calls.Enqueue(("ls", path)); return _disk.DirectoryNames(path).ToList(); }

    public string? ReadAllText(string path) { Calls.Enqueue(("read", path)); return _disk.ReadAllText(path); }

    public Stream OpenRead(string path) { Calls.Enqueue(("open", path)); return _disk.OpenRead(path); }
}
