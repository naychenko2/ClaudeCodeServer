using System.Diagnostics;
using System.Security.Cryptography;
using ClaudeHomeServer.Services.Backup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Инкрементальные снимки перед выкаткой (--snapshot): жёсткие ссылки, .part, ротация,
// восстановление из каталога снимка. Реальная файловая система во временной папке.
public class BackupSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ccs-snapshot-" + Guid.NewGuid().ToString("N")[..8]);

    private string DataDir => Path.Combine(_root, "data");
    private string BackupDir => Path.Combine(_root, "archives");
    private string SecretsDir => Path.Combine(_root, "secrets");
    private string SnapshotsDir => Path.Combine(DataDir, "backups", "snapshots");

    public BackupSnapshotTests()
    {
        Directory.CreateDirectory(DataDir);
        Write("users.json", """{ "Version": 1, "users": [ { "Id": "u1", "Username": "admin", "Role": "admin" } ] }""");
        Write("projects.json", "[]");
        Write("sessions.json", "[]");
        Write("notes/a.md", "заметка А");
        Write("notes/b.md", "заметка Б");
        Write("claude-profiles/glm/projects/C--repo/abc.jsonl", "{}");
        // Фиксируем отпечаток заранее: иначе первый снимок создаёт instance-id.txt уже после
        // обхода, и составы двух снимков подряд различались бы на этот файл
        InstanceIdentity.GetOrCreate(DataDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(DataDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private BackupContext Context() => BackupContext.FromConfiguration(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(DataDir, "projects.json"),
            ["Backup:Path"] = BackupDir,
            ["Backup:SecretsPath"] = SecretsDir,
            ["Sandbox:ContainerName"] = "",
        }).Build());

    private BackupResult Snapshot()
    {
        var result = BackupCore.SnapshotIncremental(Context());
        result.Ok.Should().BeTrue(result.Error);
        return result;
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // Число жёстких ссылок на файл. В публичном API .NET его нет — только Linux, через stat;
    // на других платформах -1 (вызывающий пропускает проверку: счётчики снимка проверяются везде)
    private static int LinkCount(string path)
    {
        if (!OperatingSystem.IsLinux()) return -1;
        var psi = new ProcessStartInfo("stat") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("%h");
        psi.ArgumentList.Add(path);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return int.Parse(output);
    }

    private static string InSnapshot(BackupResult snapshot, string relative) =>
        Path.Combine(snapshot.ArchivePath!, relative.Replace('/', Path.DirectorySeparatorChar));

    // --- Состав и хеши ---

    [Fact]
    public void ПервыйСнимок_ПолнаяКопияСМанифестомИХешами()
    {
        var snapshot = Snapshot();

        snapshot.Metrics!.Linked.Should().Be(0, "ссылаться не на что");
        snapshot.Metrics.Copied.Should().Be(snapshot.Manifest!.Files.Count);
        Directory.GetParent(snapshot.ArchivePath!)!.FullName.Should().Be(SnapshotsDir);
        File.Exists(Path.Combine(snapshot.ArchivePath!, "manifest.json")).Should().BeTrue();

        snapshot.Manifest.Files.Should().Contain(f => f.Path == "notes/a.md");
        foreach (var file in snapshot.Manifest.Files)
        {
            var copy = InSnapshot(snapshot, file.Path);
            File.Exists(copy).Should().BeTrue(file.Path);
            file.Sha256.Should().Be(Sha(copy), file.Path);
            File.ReadAllBytes(copy).Should().Equal(
                File.ReadAllBytes(Path.Combine(DataDir, file.Path.Replace('/', Path.DirectorySeparatorChar))));
        }

        Directory.GetDirectories(SnapshotsDir, "*.part").Should().BeEmpty("rename завершён");
    }

    [Fact]
    public void ВторойСнимок_НеизменённыйФайл_Ссылка_ИзменённыйКопия_УдалённыйОтсутствует()
    {
        var first = Snapshot();

        Write("notes/a.md", "заметка А, правка");          // изменён
        File.Delete(Path.Combine(DataDir, "notes", "b.md")); // удалён
        Write("notes/c.md", "новая");                       // добавлен

        var second = Snapshot();
        var m = second.Metrics!;

        // Всё, кроме изменённого и удалённого, подхвачено ссылками; изменённый и новый — копии
        m.Linked.Should().Be(first.Manifest!.Files.Count - 2);
        m.Copied.Should().Be(2);
        m.Linked.Should().Be(second.Manifest!.Files.Count - m.Copied);

        var files = second.Manifest.Files.ToDictionary(f => f.Path);
        files.Should().NotContainKey("notes/b.md");
        File.Exists(InSnapshot(second, "notes/b.md")).Should().BeFalse("удалённый файл в снимок не попадает");
        files["notes/a.md"].Sha256.Should().Be(Sha(Path.Combine(DataDir, "notes", "a.md")));
        files["notes/a.md"].Sha256.Should().NotBe(first.Manifest.Files.Single(f => f.Path == "notes/a.md").Sha256);
        files["users.json"].Sha256.Should().Be(first.Manifest.Files.Single(f => f.Path == "users.json").Sha256,
            "хеш ссылки берётся из прошлого манифеста");
        File.ReadAllText(InSnapshot(first, "notes/a.md")).Should().Be("заметка А", "старый снимок не тронут");

        // mtime копии выставлен явно равным источнику — по нему следующий снимок решает про ссылку
        File.GetLastWriteTimeUtc(InSnapshot(second, "notes/a.md"))
            .Should().Be(File.GetLastWriteTimeUtc(Path.Combine(DataDir, "notes", "a.md")));

        // Замер по фазам и счётчики попадают в сводку для лога выкатки
        m.Phases.Select(p => p.Phase).Should().Contain(new[] { "обход", "сверка", "ссылки", "копирование", "манифест" });
        m.Format().Should().Contain("ссылок").And.Contain("копий");
    }

    [Fact]
    public void НеизменённыйФайл_ДелитInodeСПрошлымСнимком_ИзменённыйНет()
    {
        var first = Snapshot();
        Write("notes/a.md", "заметка А, правка");
        var second = Snapshot();

        // Только Linux: счётчик ссылок недоступен в публичном API .NET; остальные платформы
        // покрыты счётчиками Linked/Copied в соседнем тесте
        if (!OperatingSystem.IsLinux()) return;

        LinkCount(InSnapshot(second, "users.json")).Should().Be(2, "один inode в двух снимках");
        LinkCount(InSnapshot(first, "users.json")).Should().Be(2);
        LinkCount(InSnapshot(second, "notes/a.md")).Should().Be(1, "изменённый файл — своя копия");
    }

    // --- Обрыв и .part ---

    [Fact]
    public void Обрыв_ОставляетPart_СледующийПрогонУбираетЕгоИБерётЗавершённыйСнимок()
    {
        var first = Snapshot();

        // Имитация убитого по таймауту прогона: .part с манифестом (обрыв между записью
        // манифеста и rename) и заведомо ложным хешем — брать источником его нельзя
        var part = Path.Combine(SnapshotsDir, "20991231-235959.part");
        Directory.CreateDirectory(part);
        File.WriteAllText(Path.Combine(part, "users.json"), "мусор");
        File.WriteAllText(Path.Combine(part, "manifest.json"),
            """{ "Files": [ { "Path": "users.json", "Sha256": "00", "Size": 5 } ] }""");

        var second = Snapshot();

        Directory.Exists(part).Should().BeFalse("незавершённый .part удаляется на старте");
        second.Metrics!.Linked.Should().Be(first.Manifest!.Files.Count, "источник — последний ЗАВЕРШЁННЫЙ снимок");
        second.Manifest!.Files.Single(f => f.Path == "users.json").Sha256
            .Should().Be(Sha(Path.Combine(DataDir, "users.json")));
    }

    [Fact]
    public void КаталогНеПоИмениСнимка_НеСчитаетсяИсточникомСсылок()
    {
        var first = Snapshot();
        var before = Directory.GetDirectories(SnapshotsDir);

        // Каталог не с меткой времени в имени снимком не считается и источником не служит
        Directory.CreateDirectory(Path.Combine(SnapshotsDir, "чужое"));
        File.WriteAllText(Path.Combine(SnapshotsDir, "чужое", "manifest.json"), "{}");

        var second = Snapshot();

        second.Metrics!.Linked.Should().Be(first.Manifest!.Files.Count);
        Directory.GetDirectories(SnapshotsDir).Should().Contain(before);
    }

    // --- Ротация ---

    [Fact]
    public void Ротация_ДержитТриПоследнихСнимка_СсылкиСтаршихПереживаютУдаление()
    {
        var all = new List<BackupResult>();
        for (var i = 0; i < 5; i++)
        {
            Write("notes/a.md", $"версия {i}");
            all.Add(Snapshot());
        }

        var kept = Directory.GetDirectories(SnapshotsDir).Select(Path.GetFileName).Order().ToList();
        kept.Should().HaveCount(BackupCore.SnapshotKeepLast);
        kept.Should().BeEquivalentTo(all.TakeLast(3).Select(s => Path.GetFileName(s.ArchivePath)));

        // Файлы, жившие по ссылкам в удалённых снимках, остались целыми в оставшихся
        var last = all[^1];
        foreach (var file in last.Manifest!.Files)
            Sha(InSnapshot(last, file.Path)).Should().Be(file.Sha256, file.Path);
    }

    // --- Состав = как в zip ---

    [Fact]
    public void Состав_Снимка_Совпадает_СZip_ПоBackupPaths()
    {
        // Всё, что BackupPaths исключает из zip, обязано отсутствовать и в снимке
        Write("claude-profiles/glm/.credentials.json", "токен");
        Write("claude-profiles/glm/plugins/p.md", "плагин");
        Write("jwt-secret.txt", "секрет");
        Write("notes/временный.tmp", "tmp");
        Write("logs/app.log", "лог");
        Write("sandbox-tmp/x", "x");
        Write("forgejo/ssh/ssh_host_key", "ключ");
        Write("forgejo/gitea/actions_artifacts/a", "артефакт");
        Write("forgejo/repositories/r.git/HEAD", "ref");
        Write("backups/ccs-старый.zip", "архив");
        Write("backups/snapshots/20200101-000000/users.json", "чужой снимок");

        var zip = BackupCore.Snapshot(Context());
        zip.Ok.Should().BeTrue(zip.Error);
        var snapshot = Snapshot();

        snapshot.Manifest!.Files.Select(f => f.Path).Should()
            .BeEquivalentTo(zip.Manifest!.Files.Select(f => f.Path));
        snapshot.Manifest.Files.Select(f => f.Path).Should().Contain("forgejo/repositories/r.git/HEAD");
        snapshot.Manifest.Files.Select(f => f.Path).Should().NotContain(p =>
            p.Contains("credentials") || p.EndsWith(".tmp") || p.StartsWith("logs/")
            || p.StartsWith("backups/") || p.Contains("jwt-secret") || p.Contains("/ssh/"));

        // Секреты — отдельным архивом, как у zip, а не в каталоге снимка
        File.Exists(InSnapshot(snapshot, "jwt-secret.txt")).Should().BeFalse();
        Directory.GetFiles(SecretsDir, "*.zip").Should().NotBeEmpty();
    }

    [Fact]
    public void ЛогСобытий_Sqlite_КопируетсяСвежимФайлом_НеСсылкой()
    {
        var db = Path.Combine(DataDir, "project-events.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1);";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var first = Snapshot();
        var second = Snapshot();

        first.Manifest!.Files.Should().Contain(f => f.Path == "project-events.db");
        // Оба снимка снимают базу заново: она одна из копий, а не ссылка на прошлый файл
        second.Metrics!.Copied.Should().BeGreaterThanOrEqualTo(1);
        File.Exists(InSnapshot(second, "project-events.db")).Should().BeTrue();
        if (OperatingSystem.IsLinux())
            LinkCount(InSnapshot(second, "project-events.db")).Should().Be(1);
    }

    // --- Восстановление ---

    [Fact]
    public void Восстановление_ИзКаталогаСнимка_ВозвращаетДанные_СнимокОстаётсяИСвоимиСсылками()
    {
        Snapshot();
        Write("notes/a.md", "заметка А, правка");
        var snapshot = Snapshot();
        File.WriteAllText(Path.Combine(DataDir, "sessions.json"), """[{"Id":"после-снимка"}]""");

        var result = BackupRestore.Restore(Context(), snapshot.ArchivePath!, null, force: false);

        result.Ok.Should().BeTrue(result.Error);
        File.ReadAllText(Path.Combine(DataDir, "sessions.json")).Should().Be("[]");
        File.ReadAllText(Path.Combine(DataDir, "notes", "a.md")).Should().Be("заметка А, правка");
        File.ReadAllText(Path.Combine(result.MovedDataTo!, "sessions.json")).Should().Contain("после-снимка");

        // История снимков вернулась на место (переносом каталога), снимок целый и не делит
        // inode с восстановленными данными: восстановление копирует, а не ссылается
        File.Exists(Path.Combine(snapshot.ArchivePath!, "manifest.json")).Should().BeTrue();
        Directory.GetDirectories(SnapshotsDir).Should().HaveCount(2);
        foreach (var file in snapshot.Manifest!.Files)
            Sha(InSnapshot(snapshot, file.Path)).Should().Be(file.Sha256, file.Path);
        if (OperatingSystem.IsLinux())
        {
            LinkCount(Path.Combine(DataDir, "users.json")).Should().Be(1, "данные — копия, не ссылка на снимок");
            LinkCount(InSnapshot(snapshot, "users.json")).Should().Be(2, "ссылки между снимками сохранены");
        }
    }

    [Fact]
    public void Восстановление_ИзКаталогаСнимка_ПорчаФайлаЛовитсяСверкойSha256_ДанныеНеТронуты()
    {
        var snapshot = Snapshot();
        File.WriteAllText(InSnapshot(snapshot, "users.json"), """{ "Version": 1, "users": [] }""");

        var result = BackupRestore.Restore(Context(), snapshot.ArchivePath!, null, force: false);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("контрольная сумма").And.Contain("users.json");
        // Все отказы происходят до подмены каталога
        File.ReadAllText(Path.Combine(DataDir, "users.json")).Should().Contain("admin");
        Directory.GetDirectories(_root, "data.old-*").Should().BeEmpty();
    }

    [Fact]
    public void Восстановление_ИзКаталогаСнимка_ОтсутствующийФайл_Отклоняется()
    {
        var snapshot = Snapshot();
        File.Delete(InSnapshot(snapshot, "notes/a.md"));

        var result = BackupRestore.Restore(Context(), snapshot.ArchivePath!, null, force: false);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("notes/a.md");
    }

    [Fact]
    public void Восстановление_ИзКаталогаСнимка_ПутьВМанифестеНаружу_Отклоняется()
    {
        var snapshot = Snapshot();
        var manifestPath = Path.Combine(snapshot.ArchivePath!, "manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath)
            .Replace("\"notes/a.md\"", "\"../../evil.md\""));

        var result = BackupRestore.Restore(Context(), snapshot.ArchivePath!, null, force: false);

        result.Ok.Should().BeFalse();
        File.Exists(Path.Combine(_root, "evil.md")).Should().BeFalse();
        Directory.GetDirectories(_root, "data.old-*").Should().BeEmpty();
    }
}
