using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Память последних портов дев-серверов.
///
/// Смысл: реестр процессов живёт в памяти и умирает вместе с продуктом, а сами дев-серверы
/// переживают его перезапуск. Без этой записи панель показывала живой сервис остановленным,
/// и запуск падал с «порт занят» — собственным вчерашним процессом.
/// </summary>
public class DevServerPortMemoryTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;

    public DevServerPortMemoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "portmem_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private DevServerPortMemory New() => new(_config, NullLogger<DevServerPortMemory>.Instance);

    [Fact]
    public void Помнит_порт_сервиса()
    {
        var sut = New();
        sut.Remember("p1", "svc", 5590, 4242);

        sut.Get("p1", "svc").Should().Be(5590);
    }

    [Fact]
    public void Порт_привязан_к_паре_проект_плюс_сервис()
    {
        var sut = New();
        sut.Remember("p1", "svc", 5590, 4242);

        sut.Get("p2", "svc").Should().BeNull("это другой проект");
        sut.Get("p1", "другой").Should().BeNull("это другой сервис");
    }

    /// <summary>
    /// Ради этого всё и затевалось: запись обязана пережить перезапуск продукта — иначе
    /// после выкатки живой дев-сервер снова выглядел бы остановленным.
    /// </summary>
    [Fact]
    public void Переживает_перезапуск()
    {
        New().Remember("p1", "svc", 5590, 4242);

        New().Get("p1", "svc").Should().Be(5590);
    }

    /// <summary>
    /// Погасили сами — помнить порт опасно: место мог занять посторонний процесс, и сервис
    /// показался бы «поднятым снаружи» по чужому серверу.
    /// </summary>
    [Fact]
    public void Забывает_порт_по_требованию()
    {
        var sut = New();
        sut.Remember("p1", "svc", 5590, 4242);

        sut.Forget("p1", "svc");

        sut.Get("p1", "svc").Should().BeNull();
        New().Get("p1", "svc").Should().BeNull("забвение тоже обязано пережить перезапуск");
    }

    [Fact]
    public void Нулевой_порт_не_запоминается()
    {
        var sut = New();
        sut.Remember("p1", "svc", 0, 4242);

        sut.Get("p1", "svc").Should().BeNull("порт 0 означает «неизвестен», а не адрес");
    }

    /// <summary>
    /// PID помним вместе с портом: после перезапуска продукта он единственный способ
    /// отличить свой осиротевший процесс от постороннего, занявшего тот же порт.
    /// </summary>
    [Fact]
    public void Помнит_процесс_вместе_с_портом()
    {
        var sut = New();
        sut.Remember("p1", "svc", 5590, 4242);

        var run = sut.GetRun("p1", "svc");
        run.Should().NotBeNull();
        run!.Port.Should().Be(5590);
        run.Pid.Should().Be(4242);

        New().GetRun("p1", "svc")!.Pid.Should().Be(4242, "процесс тоже переживает перезапуск");
    }
}
