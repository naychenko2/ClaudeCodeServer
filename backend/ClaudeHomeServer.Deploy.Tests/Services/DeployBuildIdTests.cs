using ClaudeHomeServer.Services.Deploy;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// X-Build (ADR-010): идентификатор текущей сборки читается из файла рядом с exe и уезжает
// в HTTP-заголовок. Отсюда два требования: нет файла — нет заголовка (контракт /api/health
// не ломается), и содержимое файла проверяется строго — CR/LF в нём означал бы инъекцию
// заголовков ответа.
public class DeployBuildIdTests : IDisposable
{
    private readonly string _tempDir;

    public DeployBuildIdTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs_buildid_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFs.DeleteDirectoryResilient(_tempDir);

    private void Write(string content) =>
        File.WriteAllText(Path.Combine(_tempDir, BuildIdProvider.FileName), content);

    [Fact]
    public void Файла_нет_идентификатора_нет() =>
        new BuildIdProvider(_tempDir).BuildId.Should().BeNull();

    [Fact]
    public void Идентификатор_читается_первой_строкой_без_пробелов()
    {
        Write("20260818-141230\nвыкатка из чата\n");

        new BuildIdProvider(_tempDir).BuildId.Should().Be("20260818-141230");
    }

    [Theory]
    [InlineData("20260818-141230\r\nX-Evil: 1")]   // инъекция заголовка
    [InlineData("id с пробелом")]
    [InlineData("сборка")]
    [InlineData("")]
    [InlineData("   ")]
    public void Негодное_содержимое_заголовка_не_даёт(string content)
    {
        Write(content);

        // Первая строка «20260818-141230» из первого случая валидна сама по себе —
        // проверяем, что читается именно она, а хвост с CRLF в заголовок не попадает
        var id = new BuildIdProvider(_tempDir).BuildId;
        (id is null || id == "20260818-141230").Should().BeTrue();
        id.Should().NotContain("X-Evil");
    }

    [Fact]
    public void Слишком_длинный_идентификатор_отвергается()
    {
        Write(new string('a', 65));

        new BuildIdProvider(_tempDir).BuildId.Should().BeNull();
    }

    // ---------- Текст доклада ----------

    [Fact]
    public void Доклад_об_откате_называет_вещи_своими_именами()
    {
        var record = new DeployRecord
        {
            Id = "20260818-141230",
            Ref = "master",
            Sha = "4dc7ddab",
            Phase = DeployPhases.RolledBack,
            Steps = [new DeployStep { Name = "health", Status = "failed" }],
            Result = new DeployResult
            {
                Ok = false,
                Status = DeployPhases.RolledBack,
                Message = "Health-гейт не сошёлся, вернули прошлый релиз.",
            },
        };

        var (title, body) = DeployReportService.Compose(record, "20260818-135500");

        title.Should().Contain("вернули прошлый релиз");
        body.Should().Contain("20260818-141230").And.Contain("master").And.Contain("4dc7ddab");
        body.Should().Contain("Не прошли шаги: health");
        body.Should().Contain("20260818-135500");
    }

    [Fact]
    public void Доклад_об_успехе_не_пугает_ошибками()
    {
        var record = new DeployRecord
        {
            Id = "20260818-141230",
            Phase = DeployPhases.Succeeded,
            Steps = [new DeployStep { Name = "frontend", Status = "ok" }],
            Result = new DeployResult { Ok = true, Status = DeployPhases.Succeeded },
        };

        var (title, body) = DeployReportService.Compose(record, null);

        title.Should().Be("Выкатка прошла");
        body.Should().NotContain("Не прошли шаги");
    }
}
