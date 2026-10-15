using System.Text.Json;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Учёт очереди ввода CLI по queue-operation главного транскрипта. Фактура — инцидент 15.10.2026:
// субагент сдал отчёт (SubagentHandback) посреди хода, CLI поставил <agent-message> и
// <task-notification> в очередь и доставил их ходом-продолжением сразу после result. Сервер
// закрыл stdin (pending фоновых задач был пуст), продолжение позвало инструмент — второй
// контур погасил процесс. Недоставленное в очереди должно держать stdin.
public class MainTranscriptTailerQueueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tailer-queue-{Guid.NewGuid():N}.jsonl");
    private readonly List<string> _notifications = [];

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* временный файл — не критично */ }
    }

    private static string Op(string operation, string? content = null) =>
        JsonSerializer.Serialize(content is null
            ? new { type = "queue-operation", operation, timestamp = "2026-10-15T09:09:20Z" }
            : (object)new { type = "queue-operation", operation, timestamp = "2026-10-15T09:09:20Z", content });

    private const string TaskNote1 = "<task-notification>\n<task-id>a08065a0410476228</task-id>\n<status>failed</status>\n</task-notification>";
    private const string TaskNote2 = "<task-notification>\n<task-id>a01ae62d15644b922</task-id>\n<status>completed</status>\n</task-notification>";
    private const string Handback = "<agent-message from=\"a01ae62d15644b922\">\n[Subagent hand-back] отчёт\n</agent-message>";

    private void Append(params string[] lines) => File.AppendAllText(_path, string.Join("\n", lines) + "\n");

    private MainTranscriptTailer NewTailer() => new(_path, _notifications.Add);

    // Ровно последовательность инцидента: первое уведомление впитано ходом (remove), отчёт
    // субагента и его уведомление остались в очереди до конца хода
    [Fact]
    public void НевпитанныйОтчётСубагента_ДержитОчередь()
    {
        Append(
            Op("enqueue", "какие изменения в 2.1.300 версии claude?"),
            Op("dequeue"),
            Op("enqueue", TaskNote1),
            Op("remove", TaskNote1),
            Op("enqueue", Handback),
            Op("enqueue", TaskNote2));

        NewTailer().HasQueuedDeliveries().Should().BeTrue();
    }

    // Всё поставленное впитано ходом — продолжения не будет, stdin можно закрывать
    [Fact]
    public void ВсёВпитано_ОчередьПуста()
    {
        Append(
            Op("enqueue", TaskNote1),
            Op("remove", TaskNote1),
            Op("enqueue", Handback),
            Op("remove", Handback));

        NewTailer().HasQueuedDeliveries().Should().BeFalse();
    }

    // Наши собственные сообщения идут через ту же очередь — доставкой основному агенту не считаются
    [Fact]
    public void НашеСообщение_НеСчитается()
    {
        Append(Op("enqueue", "## Картинки в этом чате\nтекст хода"));

        NewTailer().HasQueuedDeliveries().Should().BeFalse();
    }

    // Старт хода-продолжения доставил очередь; хвост, дописанный после, учитывается заново
    [Fact]
    public void Очистка_ИДоборХвоста()
    {
        var tailer = NewTailer();
        Append(Op("enqueue", Handback));
        tailer.HasQueuedDeliveries().Should().BeTrue();

        tailer.ClearQueuedDeliveries();
        tailer.HasQueuedDeliveries().Should().BeFalse();

        Append(Op("enqueue", TaskNote2));
        tailer.HasQueuedDeliveries().Should().BeTrue();
    }

    // Досмотр очереди не ломает прежнее назначение ридера — уведомления из user-строк
    [Fact]
    public void Досмотр_ОтдаётУведомленияВКолбэк()
    {
        Append(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = TaskNote2 } }));

        NewTailer().HasQueuedDeliveries();

        _notifications.Should().ContainSingle().Which.Should().Be(TaskNote2);
    }
}
