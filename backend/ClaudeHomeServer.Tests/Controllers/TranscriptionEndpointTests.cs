using System.Net;
using System.Net.Http.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/local-model/transcription (вкладка «Встречи», Б1): только admin (роль на действии,
// как у hardware/farm → не-админ 403, аноним 401); админ получает снимок как есть — уровни и блоки
// считает бэкенд, контроллер их не трогает. Устойчивость читателя (пустые блоки вместо 500) —
// в TranscriptionStateReaderTests; здесь — права и то, что ручка отдаёт снимок без изменений.
[Trait("Category", "Host")]
public class TranscriptionEndpointTests : IClassFixture<TranscriptionEndpointTests.Host>, IDisposable
{
    // Фиксированный снимок: форма ответа проверяется на нём, а не на состоянии живой фермы.
    private static readonly TranscriptionSnapshot Fixed = new(
        1_768_000_000_000, "ok",
        new("idle", "ожидание", 1_768_000_000_000, "worker", 4242, true, "ok", "worker", "/srv/ccs/home/andrey/Встречи", "ok"),
        null, new(0, [], 0), [], [], [],
        [new("asr", "unknown", 1_768_000_000_000), new("summary", "unknown", 1_768_000_000_000),
         new("dify", "unknown", 1_768_000_000_000), new("share", "ok", 1_768_000_000_000)]);

    private sealed class FakeReader : ITranscriptionStateReader
    {
        public TranscriptionSnapshot Read() => Fixed;
    }

    /// <summary>
    /// Хост на класс: подставной читатель зарегистрирован один раз на весь класс — он перекрывает
    /// реальный (тот читал бы живой конвейер worker-машины).
    /// </summary>
    public sealed class Host : TestWebApplicationFactory
    {
        public Host()
        {
            ExtraServices = s => s.AddSingleton<ITranscriptionStateReader>(new FakeReader());
        }
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public TranscriptionEndpointTests(Host host)
    {
        _factory = host;
        _admin = host.CreateAuthenticatedClient();
        _user = host.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    // Хост гасит фикстура после класса; здесь — только клиенты теста
    public void Dispose() { _admin.Dispose(); _user.Dispose(); }

    // Мутация: снять [Authorize(Roles="admin")] с действия — не-админ получит 200, тест краснеет.
    [Fact]
    public async Task НеАдмин_403() =>
        (await _user.GetAsync("/api/local-model/transcription")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Аноним_401() =>
        (await _factory.CreateClient().GetAsync("/api/local-model/transcription")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Админ_200_СнимокКакЕсть()
    {
        var r = await _admin.GetAsync("/api/local-model/transcription");
        r.StatusCode.Should().Be(HttpStatusCode.OK);

        var s = await r.Content.ReadFromJsonAsync<TranscriptionSnapshot>() ?? throw new InvalidOperationException("пустой ответ");
        s.Level.Should().Be("ok");
        s.Pipeline.Phase.Should().Be("idle");
        s.Pipeline.PhaseText.Should().Be("ожидание");
        s.Pipeline.Host.Should().Be("worker");
        s.Pipeline.OutputDir.Should().Be("/srv/ccs/home/andrey/Встречи");
        s.Queue.AsrCount.Should().Be(0);
        s.Deps.Should().HaveCount(4);
        s.Deps.Should().Contain(d => d.Key == "share" && d.Status == "ok");
    }
}
