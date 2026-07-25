using ClaudeHomeServer.Services.Backup;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Гейт 3 восстановления: сторы читаются строго ДО того, как каталог data сдвинут.
//
// Зачем это вообще нужно: штатный JsonFileStore.Load намеренно прощает ошибки — битый
// файл он переименовывает в .corrupt-*.bak и отдаёт пустой стор, чтобы не ронять сервер.
// При восстановлении такое поведение означает «раскатал архив, всё зелёное, а персон нет».
public class BackupValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ccs-backup-validation-" + Guid.NewGuid().ToString("N")[..8]);

    public BackupValidationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    private const string ValidUsers = """
        { "Version": 1, "users": [ { "Id": "u1", "Username": "admin", "Role": "admin" } ] }
        """;

    [Fact]
    public void ЦелыйАрхив_ПроходитПроверку()
    {
        Write("users.json", ValidUsers);
        Write("projects.json", "[]");
        Write("personas.json", "[]");

        BackupValidation.Validate(_dir).Should().BeEmpty();
    }

    [Fact]
    public void ОтсутствиеUsers_ЭтоОтказ()
    {
        BackupValidation.Validate(_dir).Should().ContainSingle()
            .Which.Should().Contain("users.json");
    }

    [Fact]
    public void ПустойСписокПользователей_ЭтоОтказ()
    {
        // Инстанс без пользователей не пускает никого — почти всегда это порча файла,
        // а не осмысленное состояние
        Write("users.json", """{ "Version": 1, "users": [] }""");

        BackupValidation.Validate(_dir).Should().NotBeEmpty();
    }

    [Fact]
    public void БитыйJson_ЭтоОтказ()
    {
        Write("users.json", ValidUsers);
        Write("personas.json", "[ { \"Name\": \"обрыв");

        BackupValidation.Validate(_dir).Should().ContainSingle()
            .Which.Should().Contain("personas.json");
    }

    [Fact]
    public void НезнакомоеЗначениеEnum_ЭтоОтказ()
    {
        // Ровно тот случай, ради которого стоит гейт версии схемы: архив новее кода
        Write("users.json", ValidUsers);
        Write("personas.json", """[ { "Id": "p1", "Name": "Тест", "Scope": "ГалактическийМасштаб" } ]""");

        BackupValidation.Validate(_dir).Should().NotBeEmpty();
    }

    [Fact]
    public void ОтсутствующиеНеобязательныеСторы_НеОшибка()
    {
        // Нет задач, персон и групп — нормальное состояние свежего инстанса
        Write("users.json", ValidUsers);

        BackupValidation.Validate(_dir).Should().BeEmpty();
    }
}
