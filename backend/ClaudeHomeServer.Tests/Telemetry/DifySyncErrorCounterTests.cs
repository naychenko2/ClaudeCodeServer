using System.Net;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Тесты классификатора ошибок Dify-синхронизации и счётчика ServerMetrics.DifySyncErrors.
///
/// Контекст: MemoryDify.DiffSyncAsync и ProjectKnowledgeSyncService намеренно глотают
/// ошибки Dify (best-effort — Dify down не должен ронять пользовательский флоу). Раньше
/// это был LogDebug, из-за чего сбои были невидимы. Теперь catch-блоки логируют LogWarning
/// и инкрементят ServerMetrics.RecordDifySyncError(reason) с классификацией по reason.
///
/// Categorizer — чистая функция (exception → reason), тестируется напрямую.
/// KnowledgeService не мокается (методы не virtual), поэтому интеграцию catch-блока
/// верифицируем сборкой, а логику классификации — этими тестами.
/// </summary>
public class DifySyncErrorCounterTests
{
    // ── Categorizer: HTTP status codes ──────────────────────────────────────

    [Fact]
    public void Categorize_HttpRequestException401_Returns401()
    {
        // Given: HttpRequestException со статусом 401 Unauthorized
        var ex = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        // When: классифицируем
        var reason = DifyErrorCategorizer.Categorize(ex);

        // Then: reason = "401"
        reason.Should().Be("401");
    }

    [Fact]
    public void Categorize_HttpRequestException404_Returns404()
    {
        var ex = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);

        var reason = DifyErrorCategorizer.Categorize(ex);

        reason.Should().Be("404");
    }

    [Fact]
    public void Categorize_HttpRequestException429_Returns429()
    {
        var ex = new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);

        var reason = DifyErrorCategorizer.Categorize(ex);

        reason.Should().Be("429");
    }

    [Fact]
    public void Categorize_HttpRequestException500_ReturnsOther()
    {
        var ex = new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError);

        var reason = DifyErrorCategorizer.Categorize(ex);

        reason.Should().Be("other");
    }

    // ── Categorizer: timeout ────────────────────────────────────────────────

    [Fact]
    public void Categorize_TaskCanceledException_ReturnsTimeout()
    {
        // TaskCanceledException — то, что HttpClient кидает при таймауте
        var ex = new TaskCanceledException("The operation was canceled");

        var reason = DifyErrorCategorizer.Categorize(ex);

        reason.Should().Be("timeout");
    }

    [Fact]
    public void Categorize_OperationCanceledException_ReturnsTimeout()
    {
        // Базовый класс TaskCanceledException — тоже таймаут
        var ex = new OperationCanceledException("Canceled");

        var reason = DifyErrorCategorizer.Categorize(ex);

        reason.Should().Be("timeout");
    }

    [Fact]
    public void Categorize_HttpRequestExceptionWrappingTimeout_ReturnsTimeout()
    {
        // HttpRequestException может оборачивать TaskCanceledException при таймауте
        var inner = new TaskCanceledException("timed out");
        var ex = new HttpRequestException("Request failed", inner, statusCode: null);

        var reason = DifyErrorCategorizer.Categorize(ex);

        reason.Should().Be("timeout");
    }

    // ── Categorizer: other ──────────────────────────────────────────────────

    [Fact]
    public void Categorize_GenericException_ReturnsOther()
    {
        var ex = new InvalidOperationException("Something unexpected");

        var reason = DifyErrorCategorizer.Categorize(ex);

        reason.Should().Be("other");
    }

    [Fact]
    public void Categorize_HttpRequestExceptionNullStatus_ReturnsOther()
    {
        // HttpRequestException без StatusCode (сетевая ошибка до получения ответа)
        var ex = new HttpRequestException("Connection refused");

        var reason = DifyErrorCategorizer.Categorize(ex);

        reason.Should().Be("other");
    }

    // ── ServerMetrics.RecordDifySyncError: smoke (не бросает) ───────────────

    [Theory]
    [InlineData("401")]
    [InlineData("404")]
    [InlineData("429")]
    [InlineData("timeout")]
    [InlineData("other")]
    public void RecordDifySyncError_DoesNotThrow_ForEachReason(string reason)
    {
        // Smoke: метод должен принимать все валидные reason без исключений
        var act = () => ServerMetrics.RecordDifySyncError(reason);

        act.Should().NotThrow();
    }
}
