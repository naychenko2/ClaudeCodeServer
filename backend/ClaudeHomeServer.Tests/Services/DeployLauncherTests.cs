using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Deploy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services;

// Предусловия выкатки и чтение чужого файла статуса. Процессы и события Windows здесь не
// участвуют: тесты гоняются на linux-раннере CI, а связь с треем спрятана за ITrayGate ровно
// ради этой границы.
//
// Проверяем то, что иначе врёт молча: «выкатка уже идёт» по повисшему статусу (кнопка залипла
// бы навсегда), запуск при мёртвом трее (сигнал ушёл бы в пустоту) и падение на битом файле,
// который пишет другой процесс и который штатно можно застать на середине записи.
public class DeployLauncherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deploy-launcher-" + Guid.NewGuid().ToString("N"));

    public DeployLauncherTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка не критична */ }
        GC.SuppressFinalize(this);
    }

    private string StatusPath => Path.Combine(_dir, "deploy-status.json");

    private DeployLauncher Make(bool enabled = true, bool trayAlive = true, int staleMin = 60)
        => new(
            Options.Create(new TrayDeployOptions
            {
                Enabled = enabled,
                StatusPath = StatusPath,
                StaleRunningMin = staleMin,
                EventName = "TestEvent",
            }),
            new FakeTrayGate(trayAlive),
            NullLogger<DeployLauncher>.Instance);

    private void WriteStatus(string result, DateTime startedAt)
        => File.WriteAllText(StatusPath, $$"""
        {
          "StartedAt": "{{startedAt:yyyy-MM-dd HH:mm:ss}}",
          "FinishedAt": null,
          "Mode": "as-is",
          "Branch": "master",
          "DirtyFiles": 0,
          "Head": "abc1234",
          "DeployExitCode": null,
          "Result": "{{result}}",
          "ProductUp": null,
          "Note": null
        }
        """);

    [Fact]
    public void ReadStatus_ФайлаНет_ВозвращаетNull()
        => Make().ReadStatus().Should().BeNull();

    [Fact]
    public void ReadStatus_БитыйJson_ВозвращаетNullИНеБросает()
    {
        File.WriteAllText(StatusPath, "{ \"StartedAt\": \"2026-08-19 13:22:5");
        Make().ReadStatus().Should().BeNull();
    }

    [Fact]
    public void ReadStatus_РазбираетПоляРаннера()
    {
        WriteStatus("ok", new DateTime(2026, 8, 19, 13, 22, 50));

        var status = Make().ReadStatus();

        status.Should().NotBeNull();
        status!.Result.Should().Be("ok");
        status.Mode.Should().Be("as-is");
        status.Head.Should().Be("abc1234");
        status.StartedAt.Should().Be("2026-08-19 13:22:50");
    }

    [Fact]
    public void CanLaunch_ВыключеноКонфигом_Отказ()
        => Make(enabled: false).CanLaunch().CanLaunch.Should().BeFalse();

    [Fact]
    public void CanLaunch_ТреяНет_Отказ()
    {
        var result = Make(trayAlive: false).CanLaunch();

        result.CanLaunch.Should().BeFalse();
        result.Reason.Should().Contain("раннер");
    }

    [Fact]
    public void CanLaunch_СвежийRunning_Отказ()
    {
        WriteStatus("running", DateTime.Now.AddMinutes(-2));

        Make().CanLaunch().CanLaunch.Should().BeFalse();
    }

    // Повисший running означает, что трей умер посреди работы. Блокировать им запуск нельзя:
    // иначе кнопка залипнет до тех пор, пока файл не поправят руками.
    [Fact]
    public void CanLaunch_ПротухшийRunning_Разрешает()
    {
        WriteStatus("running", DateTime.Now.AddMinutes(-90));

        Make(staleMin: 60).CanLaunch().CanLaunch.Should().BeTrue();
    }

    [Fact]
    public void CanLaunch_ЗавершённаяВыкатка_Разрешает()
    {
        WriteStatus("ok", DateTime.Now.AddMinutes(-1));

        Make().CanLaunch().CanLaunch.Should().BeTrue();
    }

    // Время пишет чужой процесс — если формат вдруг разъедется, «уже идёт» соврало бы и
    // заблокировало выкатку. Пропустить лишний сигнал безопаснее: двойной запуск отобьёт
    // сам трей.
    [Fact]
    public void CanLaunch_НеразбираемоеВремяУRunning_Разрешает()
    {
        File.WriteAllText(StatusPath, """
        { "StartedAt": "вчера вечером", "Result": "running", "DirtyFiles": 0 }
        """);

        Make().CanLaunch().CanLaunch.Should().BeTrue();
    }

    [Theory]
    [InlineData("2026-08-19 13:22:50", true)]
    [InlineData("2026-08-19T13:22:50Z", false)]
    [InlineData(null, false)]
    public void ParseStamp_ЖдётФорматРаннера(string? stamp, bool parsed)
        => (DeployLauncher.ParseStamp(stamp) is not null).Should().Be(parsed);

    private sealed class FakeTrayGate(bool alive) : ITrayGate
    {
        public bool IsAlive(string eventName) => alive;
        public bool Signal(string eventName) => alive;
    }
}
