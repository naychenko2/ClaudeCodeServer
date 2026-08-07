using System.Text.Json;
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

    // graph.json — regenerable (перестраивается из кода проекта), поэтому его порча не должна
    // блокировать восстановление всей data. Проверяем, что он не входит в fatal Validate,
    // а попадает в отдельный soft-список ValidateGraphWarnings.

    private void WriteGraphJson(string hash, string content)
    {
        var hashDir = Path.Combine(_dir, "code-graphs", hash);
        Directory.CreateDirectory(hashDir);
        File.WriteAllText(Path.Combine(hashDir, "graph.json"), content);
    }

    [Fact]
    public void БитыйGraphJson_НеБлокируетВосстановление()
    {
        Write("users.json", ValidUsers);
        WriteGraphJson("abc123", "{ НЕ JSON");

        // fatal-сторы валидны → восстановление разрешено
        BackupValidation.Validate(_dir).Should().BeEmpty();
        // но soft-предупреждение есть — граф пересоберётся из кода
        BackupValidation.ValidateGraphWarnings(_dir)
            .Should().ContainSingle().Which.Should().Contain("graph.json");
    }

    [Fact]
    public void GraphJson_БезNodesEdges_ЭтоСoftWarning()
    {
        Write("users.json", ValidUsers);
        WriteGraphJson("xyz", """{ "Foo": 1 }""");

        BackupValidation.Validate(_dir).Should().BeEmpty();
        BackupValidation.ValidateGraphWarnings(_dir).Should().NotBeEmpty();
    }

    [Fact]
    public void ВалидныйGraphJson_БезПредупреждений()
    {
        Write("users.json", ValidUsers);
        WriteGraphJson("good", """{ "Nodes": {}, "Edges": [] }""");

        BackupValidation.Validate(_dir).Should().BeEmpty();
        BackupValidation.ValidateGraphWarnings(_dir).Should().BeEmpty();
    }

    // Шесть новых полей онбординга/дефолт-персоны (User.DefaultPersonaId,
    // User.OnboardingSessionId, Project.DefaultPersonaId, Project.OnboardingSessionId,
    // Session.OnboardingKind, Session.OnboardingCreatedPersonaId) — все string?.
    // Поскольку схема не бампалась (поля добавлены без increment BackupSchema.Version),
    // архив со старой SchemaVersion=5 ОБЯЗАН продолжать валидироваться как пригодный.
    [Fact]
    public void НовыеПоляОнбординга_ПроходятВалидацию()
    {
        Write("users.json", """
            {
              "Version": 1,
              "users": [
                {
                  "Id": "u1",
                  "Username": "admin",
                  "Role": "admin",
                  "DefaultPersonaId": "test-default-persona-1234",
                  "OnboardingSessionId": "test-onboarding-session-5678"
                }
              ]
            }
            """);
        Write("projects.json", """
            [
              {
                "Id": "p1",
                "Name": "Киберпсихология",
                "RootPath": "C:/x",
                "OwnerId": "u1",
                "DefaultPersonaId": "test-project-default-persona-9012",
                "OnboardingSessionId": "test-project-onboarding-session-3456"
              }
            ]
            """);
        Write("sessions.json", """
            [
              {
                "Id": "s1",
                "ProjectId": "p1",
                "OwnerId": "u1",
                "OnboardingKind": "project",
                "OnboardingCreatedPersonaId": "test-onboarding-created-persona-7890"
              }
            ]
            """);

        BackupValidation.Validate(_dir).Should().BeEmpty();
    }

    // Forward compatibility: новый архив (с ещё не существовавшими на момент его создания
    // полями) читается старым кодом без падения. System.Text.Json по умолчанию игнорирует
    // неизвестные свойства, и архив не должен ломать стор, у которого этих полей нет.
    // Проверяем в миниатюре: «старая» модель с одним Id десериализует JSON с лишним полем —
    // Id доходит, исключения нет. На этом же построена совместимость для шести наших полей.
    [Fact]
    public void СтараяМодельИгнорируетНеизвестныеПоля()
    {
        const string json = """{ "Id": "u1", "MysteryField": "неведомое" }""";

        var oldShape = JsonSerializer.Deserialize<OldUserShape>(json);

        oldShape.Should().NotBeNull();
        oldShape!.Id.Should().Be("u1");
    }

    private sealed class OldUserShape
    {
        public string? Id { get; set; }
    }
}
