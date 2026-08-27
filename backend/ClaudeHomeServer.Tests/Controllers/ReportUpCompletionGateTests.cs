using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Controllers;

// B5: гашение дублирующего chats_report_up после того, как доклад о завершении задачи
// постановщику уже доставлен сервером (TaskExecutionService). Гейт вынесен статическим
// предикатом SessionMessagingService.IsCompletionAlreadyReported — проверяем его вместе
// с резолвом задачи по сессии, без поднятия приложения.
public class ReportUpCompletionGateTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;

    public ReportUpCompletionGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "report_gate_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _tasks = new TaskManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ДокладУжеДоставлен_ГейтЗакрыт()
    {
        var task = _tasks.Create(null, "u", new CreateTaskRequest("t"));
        _tasks.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);
        _tasks.TryMarkCompletionDelivered(task.Id).Should().BeTrue();

        SessionMessagingService.IsCompletionAlreadyReported(_tasks.GetBySession("sess-1"), blocker: false)
            .Should().BeTrue("второе сообщение об одном факте в ленту постановщика не идёт");
    }

    // Гейт закрывает только ФИНАЛЬНЫЙ доклад: работа в чате-исполнителе продолжается и после
    // закрытия задачи, и «я встал» обязано долетать — иначе координатор ждёт сигнала, которого
    // молча съели, а модель по ответу Ok считает, что доложила
    [Fact]
    public void БлокерПослеДоставленногоДоклада_ГейтПропускает()
    {
        var task = _tasks.Create(null, "u", new CreateTaskRequest("t"));
        _tasks.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);
        _tasks.TryMarkCompletionDelivered(task.Id).Should().BeTrue();

        SessionMessagingService.IsCompletionAlreadyReported(_tasks.GetBySession("sess-1"), blocker: true)
            .Should().BeFalse("доклад о блокере проходит всегда — гейт про финальный доклад");
    }

    [Fact]
    public void ЗадачаЕщёВРаботе_ГейтОткрыт()
    {
        var task = _tasks.Create(null, "u", new CreateTaskRequest("t"));
        _tasks.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);

        SessionMessagingService.IsCompletionAlreadyReported(_tasks.GetBySession("sess-1"), blocker: false)
            .Should().BeFalse("промежуточный отчёт по ходу работы гасить нечем");
    }

    // Обычный чат (не исполнитель задачи) — GetBySession возвращает null, отчёт идёт как раньше
    [Fact]
    public void СессияНеПривязанаКЗадаче_ГейтОткрыт()
    {
        var task = _tasks.Create(null, "u", new CreateTaskRequest("t"));
        _tasks.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);
        _tasks.TryMarkCompletionDelivered(task.Id);

        SessionMessagingService.IsCompletionAlreadyReported(_tasks.GetBySession("chat-42"), blocker: false)
            .Should().BeFalse();
        SessionMessagingService.IsCompletionAlreadyReported(null, blocker: false).Should().BeFalse();
    }
}
