using System.IO.Compression;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Backup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// NT-хэш пароля (легаси-поле NtHash) в users.json больше не хранится: файл уезжает
/// в облачный бэкап, а по MD4-хэшу пароль ломается перебором. Проверяем, что старое
/// поле вычищается при загрузке и что новые записи его не заводят.
/// </summary>
public class UserStoreNtHashMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _usersPath;

    public UserStoreNtHashMigrationTests()
    {
        // В имени папки НЕ должно быть искомой подстроки: путь к data попадает в manifest.json,
        // и приёмочный поиск по архиву ловил бы сам себя
        _tempDir = Path.Combine(Path.GetTempPath(), "userstore_pwdmig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _usersPath = Path.Combine(_tempDir, "users.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private UserStore CreateStore() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json")
            }).Build(),
        new Helpers.FakeHostEnvironment(),
        NullLogger<UserStore>.Instance);

    // users.json в формате «до отмены»: у пользователя лежит NT-хэш
    private void SeedLegacyUsersFile()
    {
        var legacy = """
        {
          "Version": 1,
          "users": [
            {
              "Id": "u-1",
              "Username": "alice",
              "PasswordHash": "AQAAAA==",
              "TokenVersion": 1,
              "Role": "admin",
              "NtHash": "MTIzNDU2Nzg5MDEyMzQ1Ng=="
            }
          ]
        }
        """;
        File.WriteAllText(_usersPath, legacy);
    }

    [Fact]
    public void Load_LegacyNtHash_IsStrippedFromFile()
    {
        SeedLegacyUsersFile();

        var store = CreateStore();

        store.FindByUsername("alice").Should().NotBeNull("миграция не должна терять пользователей");
        File.ReadAllText(_usersPath).Should().NotContain("NtHash");
    }

    // Приёмка задачи: снимаем настоящий архив с каталога data и убеждаемся, что NT-хэша
    // в нём нет ни в одном файле — именно он раньше уезжал в облако вместе с users.json.
    [Fact]
    [Trait("Category", "Slow")]
    public void BackupArchive_ContainsNoNtHash()
    {
        SeedLegacyUsersFile();
        File.WriteAllText(Path.Combine(_tempDir, "projects.json"), "[]");
        CreateStore(); // старт сервера: миграция чистит легаси-поле

        var ctx = BackupContext.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["Backup:Path"] = Path.Combine(_tempDir, "..", "archives-" + Path.GetFileName(_tempDir)),
                ["Backup:SecretsPath"] = Path.Combine(_tempDir, "..", "secrets-" + Path.GetFileName(_tempDir)),
                // Контейнера песочницы в тестах нет — docker дёргать незачем
                ["Sandbox:ContainerName"] = "",
            }).Build());

        var result = BackupCore.Snapshot(ctx);
        result.Ok.Should().BeTrue(result.Error);

        var unpacked = Path.Combine(_tempDir, "unpacked");
        ZipFile.ExtractToDirectory(result.ArchivePath!, unpacked);

        // Сначала утверждаем, что users.json вообще попал в архив: иначе «NtHash не найден»
        // означало бы не «секрет вычищен», а «в архиве нет и самих юзеров» — ложная зелень.
        var archivedUsers = Directory.EnumerateFiles(unpacked, "users.json", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("alice"))
            .ToList();
        archivedUsers.Should().NotBeEmpty("приёмка утечки имеет смысл, только если users.json с юзерами реально в архиве");

        var hits = Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("NtHash", StringComparison.OrdinalIgnoreCase))
            .ToList();
        hits.Should().BeEmpty("NT-хэш пароля не должен попадать в облачный архив; найден в: "
            + string.Join(", ", hits.Select(h => Path.GetRelativePath(unpacked, h))));

        try { Directory.Delete(Path.Combine(_tempDir, "..", "archives-" + Path.GetFileName(_tempDir)), true); } catch { /* временная папка */ }
        try { Directory.Delete(Path.Combine(_tempDir, "..", "secrets-" + Path.GetFileName(_tempDir)), true); } catch { /* временная папка */ }
    }

    [Fact]
    public void NewUserAndPasswordChange_DoNotWriteNtHash()
    {
        var store = CreateStore();
        var user = store.Add("bob", "password-1", "user");
        store.ChangePassword(user.Id, "password-1", "password-2").Should().BeTrue();
        store.ResetPassword(user.Id, "password-3").Should().BeTrue();

        var json = File.ReadAllText(_usersPath);
        json.Should().NotContain("NtHash");
        // Файл остался валидным JSON и содержит пользователя (первый — дефолтный admin,
        // созданный стором на пустом каталоге)
        JsonDocument.Parse(json).RootElement.GetProperty("users")
            .EnumerateArray().Select(u => u.GetProperty("Username").GetString())
            .Should().Contain("bob");
    }
}
