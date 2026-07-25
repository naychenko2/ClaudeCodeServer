using System.IO.Compression;
using ClaudeHomeServer.Services.Backup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Файловая часть бэкапа: снимок → архив → восстановление на реальном каталоге во временной
// папке. Именно здесь живёт весь риск потери данных, и никакая проверка строковой логики
// его не ловит: важно, что именно попало в zip, что осталось после подмены каталога и что
// происходит, когда операция обрывается на середине.
public class BackupRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ccs-roundtrip-" + Guid.NewGuid().ToString("N")[..8]);

    private string DataDir => Path.Combine(_root, "data");
    private string AppDir => Path.Combine(_root, "app");
    private string BackupDir => Path.Combine(_root, "archives");
    private string SecretsDir => Path.Combine(_root, "secrets");

    public BackupRoundTripTests()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(AppDir);
        SeedData();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private const string Users = """
        { "Version": 1, "users": [ { "Id": "u1", "Username": "admin", "Role": "admin" } ] }
        """;

    private void SeedData()
    {
        File.WriteAllText(Path.Combine(DataDir, "users.json"), Users);
        File.WriteAllText(Path.Combine(DataDir, "projects.json"), "[]");
        File.WriteAllText(Path.Combine(DataDir, "sessions.json"), "[]");
        // Секреты — их не должно быть в основном архиве, но они обязаны пережить restore
        File.WriteAllText(Path.Combine(DataDir, "jwt-secret.txt"), "секрет-инстанса");
        File.WriteAllText(Path.Combine(DataDir, "vapid-keys.json"), "{\"pub\":\"x\"}");
        // Транскрипт стороннего провайдера — единственный источник --resume для таких чатов
        var transcripts = Path.Combine(DataDir, "claude-profiles", "glm", "projects", "C--repo");
        Directory.CreateDirectory(transcripts);
        File.WriteAllText(Path.Combine(transcripts, "abc.jsonl"), "{}");
        // Креденшалы и plugins в облачный архив уезжать не должны
        File.WriteAllText(Path.Combine(DataDir, "claude-profiles", "glm", ".credentials.json"), "токен");
        Directory.CreateDirectory(Path.Combine(DataDir, "claude-profiles", "glm", "plugins"));
        File.WriteAllText(Path.Combine(DataDir, "claude-profiles", "glm", "plugins", "p.md"), "плагин");
    }

    private BackupContext Context() => BackupContext.FromConfiguration(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(DataDir, "projects.json"),
            ["Backup:Path"] = BackupDir,
            ["Backup:SecretsPath"] = SecretsDir,
            // Контейнера песочницы в тестах нет — docker дёргать незачем
            ["Sandbox:ContainerName"] = "",
        }).Build());

    private static List<string> EntriesOf(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    // --- Снимок ---

    [Fact]
    public void Снимок_ДваждыПодряд_СReadOnlyФайлом_ОбаУспешны_StagingЗачищен()
    {
        // Прод 25.07: git-объекты Forgejo помечены read-only, копия наследовала атрибут —
        // зачистка staging молча падала, а следующий бэкап валился «Access denied»
        // на собственном мусоре и стопорил деплой
        var objects = Path.Combine(DataDir, "forgejo", "git", "repositories", "u", "r.git", "objects", "00");
        Directory.CreateDirectory(objects);
        var obj = Path.Combine(objects, "abc123");
        File.WriteAllText(obj, "объект");
        File.SetAttributes(obj, FileAttributes.ReadOnly);

        try
        {
            var first = BackupCore.Snapshot(Context());
            first.Ok.Should().BeTrue(first.Error);
            Directory.Exists(Path.Combine(DataDir, BackupPaths.StagingDirName))
                .Should().BeFalse("staging обязан зачищаться и с read-only копиями");

            var second = BackupCore.Snapshot(Context());
            second.Ok.Should().BeTrue(second.Error);
        }
        finally
        {
            File.SetAttributes(obj, FileAttributes.Normal); // иначе Dispose не удалит папку
        }
    }

    [Fact]
    public void Снимок_СобираетАрхивСМанифестомИСекретамиОтдельно()
    {
        var result = BackupCore.Snapshot(Context());

        result.Ok.Should().BeTrue(result.Error);
        var entries = EntriesOf(result.ArchivePath!);

        entries.Should().Contain("users.json");
        entries.Should().Contain("manifest.json");
        // Транскрипты профилей нужны для --resume и восстанавливаются только отсюда
        entries.Should().Contain(e => e.Contains("claude-profiles/glm/projects/"));

        // Основной архив уезжает в облачную папку — ни креденшалов, ни секретов в нём
        entries.Should().NotContain(e => e.Contains(".credentials.json"));
        entries.Should().NotContain(e => e.Contains("plugins/"));
        entries.Should().NotContain("jwt-secret.txt");
        entries.Should().NotContain("vapid-keys.json");

        var secrets = Directory.GetFiles(SecretsDir, "*.zip").Should().ContainSingle().Subject;
        EntriesOf(secrets).Should().Contain("data/jwt-secret.txt");
    }

    [Fact]
    public void Снимок_МатериализуетОтпечатокИнстанса()
    {
        // До первого снимка файла нет — иначе ветка «усыновить id из архива»
        // при восстановлении на чистой машине стала бы недостижимой
        InstanceIdentity.TryRead(DataDir).Should().BeNull();

        var result = BackupCore.Snapshot(Context());

        result.Manifest!.InstanceId.Should().NotBeEmpty();
        InstanceIdentity.TryRead(DataDir).Should().Be(result.Manifest.InstanceId);
    }

    // --- Восстановление ---

    [Fact]
    public void Восстановление_ВозвращаетДанныеИСохраняетПрежниеРядом()
    {
        var archive = BackupCore.Snapshot(Context()).ArchivePath!;
        File.WriteAllText(Path.Combine(DataDir, "sessions.json"), """[{"Id":"после-снимка"}]""");

        var result = BackupRestore.Restore(Context(), archive, null, force: false);

        result.Ok.Should().BeTrue(result.Error);
        File.ReadAllText(Path.Combine(DataDir, "sessions.json")).Should().Be("[]");
        Directory.Exists(result.MovedDataTo).Should().BeTrue();
        // Прежнее состояние осталось рядом — путь назад, если откатились не туда
        File.ReadAllText(Path.Combine(result.MovedDataTo!, "sessions.json"))
            .Should().Contain("после-снимка");
    }

    [Fact]
    public void Восстановление_СохраняетТекущиеСекреты()
    {
        // Секретов в архиве нет, а каталог заменяется целиком: без переноса обычный откат
        // втихую разлогинил бы всех (новый jwt-secret) и убил push-подписки (новые VAPID)
        var archive = BackupCore.Snapshot(Context()).ArchivePath!;
        File.WriteAllText(Path.Combine(DataDir, "jwt-secret.txt"), "секрет-после-снимка");

        BackupRestore.Restore(Context(), archive, null, force: false).Ok.Should().BeTrue();

        File.Exists(Path.Combine(DataDir, "jwt-secret.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(DataDir, "jwt-secret.txt")).Should().Be("секрет-после-снимка");
        File.Exists(Path.Combine(DataDir, "vapid-keys.json")).Should().BeTrue();
    }

    [Fact]
    public void Восстановление_СтавитМеткуДляПересборкиБазЗнаний()
    {
        var archive = BackupCore.Snapshot(Context()).ArchivePath!;

        BackupRestore.Restore(Context(), archive, null, force: false).Ok.Should().BeTrue();

        File.Exists(Path.Combine(DataDir, BackupPaths.PostRestoreMarker)).Should().BeTrue();
    }

    [Fact]
    public void Восстановление_НаЧистойМашине_УсыновляетОтпечатокБезForce()
    {
        // Штатный disaster recovery: диск умер, деплой свежий, data пустая.
        // Требовать здесь --force нельзя — это тот же флаг, что и у опасного
        // восстановления чужого архива, и разница между случаями потерялась бы.
        var archive = BackupCore.Snapshot(Context()).ArchivePath!;
        var originalId = InstanceIdentity.TryRead(DataDir)!;
        Directory.Delete(DataDir, recursive: true);
        Directory.CreateDirectory(DataDir);

        var result = BackupRestore.Restore(Context(), archive, null, force: false);

        result.Ok.Should().BeTrue(result.Error);
        InstanceIdentity.TryRead(DataDir).Should().Be(originalId);
    }

    [Fact]
    public void Восстановление_ЧужогоАрхива_ОтклоняетсяБезForce()
    {
        var archive = BackupCore.Snapshot(Context()).ArchivePath!;
        InstanceIdentity.Adopt(DataDir, "ffffffffffffffffffffffffffffffff");

        var refused = BackupRestore.Restore(Context(), archive, null, force: false);
        refused.Ok.Should().BeFalse();
        refused.Error.Should().Contain("другим инстансом");

        BackupRestore.Restore(Context(), archive, null, force: true).Ok.Should().BeTrue();
    }

    [Fact]
    public void Восстановление_БитогоАрхива_ОтклоняетсяИНеТрогаетДанные()
    {
        var archive = BackupCore.Snapshot(Context()).ArchivePath!;
        // Правим содержимое внутри архива — контрольная сумма в манифесте перестаёт сходиться
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("users.json")!;
            using var stream = entry.Open();
            stream.SetLength(0);
            using var writer = new StreamWriter(stream);
            writer.Write("""{ "Version": 1, "users": [] }""");
        }

        var result = BackupRestore.Restore(Context(), archive, null, force: false);

        result.Ok.Should().BeFalse();
        // Каталог не сдвинут: все отказы происходят до подмены
        File.ReadAllText(Path.Combine(DataDir, "users.json")).Should().Contain("admin");
        Directory.GetDirectories(_root, "data.old-*").Should().BeEmpty();
    }

    [Fact]
    public void Восстановление_НеРотируетАрхивы()
    {
        // Страховочный снимок перед восстановлением не должен запускать чистку: пересчёт
        // корзин мог бы выкинуть из окна ровно тот архив, который сейчас восстанавливают
        var archive = BackupCore.Snapshot(Context()).ArchivePath!;

        BackupRestore.Restore(Context(), archive, null, force: false).Ok.Should().BeTrue();

        File.Exists(archive).Should().BeTrue();
    }

    [Fact]
    public void Восстановление_ПодЖивымСервером_Отклоняется()
    {
        var archive = BackupCore.Snapshot(Context()).ArchivePath!;

        // Мьютекс принадлежит ПОТОКУ и реентерабелен, поэтому «сервер» держим из
        // отдельного потока — так же, как в бою его держит отдельный процесс сервера,
        // а восстановление идёт из своего (exe --restore)
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var serverLock = InstanceLock.TryAcquireInstance(DataDir);
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            try { serverLock?.ReleaseMutex(); } catch { /* отпускаем best-effort */ }
        });
        holder.Start();
        acquired.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        try
        {
            var result = BackupRestore.Restore(Context(), archive, null, force: false);

            result.Ok.Should().BeFalse();
            result.Error.Should().Contain("Сервер запущен");
            // Каталог не тронут — отказ случился до подмены
            Directory.GetDirectories(_root, "data.old-*").Should().BeEmpty();
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void ЗаброшенныйМьютекс_НеЛоматСледующуюПроверку()
    {
        // Трей гасит сервер через Kill — мьютекс инстанса остаётся заброшенным ВСЕГДА,
        // и раньше на этом падало любое восстановление из меню трея
        var thread = new Thread(() => InstanceLock.TryAcquireInstance(DataDir));
        thread.Start();
        thread.Join();

        InstanceLock.IsServerRunning(DataDir).Should().BeFalse();
        InstanceLock.IsServerRunning(DataDir).Should().BeFalse();
    }
}
