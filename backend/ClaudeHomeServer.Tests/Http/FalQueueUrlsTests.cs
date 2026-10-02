using ClaudeHomeServer.Services.Http;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Http;

// Адреса из ответа очереди fal получают ключ: доверяем только https на хостах fal и базе очереди
public class FalQueueUrlsTests
{
    private const string Base = "https://queue.fal.run";

    [Theory]
    [InlineData("https://queue.fal.run/fal-ai/x/requests/r1/status", Base)]
    [InlineData("https://fal.run/fal-ai/x", Base)]
    [InlineData("https://QUEUE.FAL.RUN/fal-ai/x/requests/r1", Base)]
    [InlineData("https://queue.test/r1/status", "https://queue.test")]
    public void Доверяет_https_на_хостах_fal_и_базе_очереди(string url, string queueBase) =>
        FalQueueUrls.IsTrusted(url, queueBase).Should().BeTrue();

    [Theory]
    [InlineData("http://queue.fal.run/fal-ai/x/requests/r1/status", Base)]
    [InlineData("https://evil.test/r1/status", Base)]
    [InlineData("https://fal.run.evil.test/r1", Base)]
    [InlineData("https://evilfal.run/r1", Base)]
    [InlineData("https://user:pass@queue.fal.run/r1", Base)]
    [InlineData("https://queue.test:8443/r1", "https://queue.test")]
    [InlineData("http://queue.test/r1", "http://queue.test")]
    [InlineData("/requests/r1/status", Base)]
    [InlineData("", Base)]
    [InlineData(null, Base)]
    public void Не_доверяет_чужому_хосту_http_и_относительному_адресу(string? url, string queueBase) =>
        FalQueueUrls.IsTrusted(url, queueBase).Should().BeFalse();
}
