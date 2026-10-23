using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

public class SessionMessagePipelineTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // Очередь с потребителем, зависшим на первом кадре: всё последующее копится
    private sealed class Rig
    {
        public readonly List<ServerMessage> Seen = [];
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstTaken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SessionMessagePipeline Pipeline { get; }

        public Rig(int capacity, long bytesLimit = SessionMessagePipeline.DefaultQueueBytesLimit)
        {
            Pipeline = new SessionMessagePipeline(async item =>
            {
                lock (Seen) Seen.Add(item.Msg!);
                _firstTaken.TrySetResult();
                await Release.Task;
            }, capacity, bytesLimit);
        }

        public async Task HoldConsumerAsync()
        {
            await Enqueue(new TextDeltaMessage("hold"));
            await _firstTaken.Task.WaitAsync(Wait);
        }

        public ValueTask Enqueue(ServerMessage msg) => Pipeline.EnqueueAsync(new PipelineItem(
            () => Task.CompletedTask, msg, null, SessionMessagePipeline.IsDroppable(msg), 0,
            WorkFor: SessionMessagePipeline.IsDroppable(msg) ? _ => () => Task.CompletedTask : null));

        public async Task<List<ServerMessage>> FinishAsync()
        {
            Release.TrySetResult();
            await Pipeline.DrainAsync().WaitAsync(Wait);
            lock (Seen) return Seen.Skip(1).ToList();
        }
    }

    private static ToolUseMessage Tool(string id) =>
        new(id, "Read", System.Text.Json.JsonDocument.Parse("{}").RootElement);

    [Fact]
    public async Task Слияние_НеПересекаетНесливаемыйКадр_ПорядокСохранён()
    {
        var rig = new Rig(capacity: 1);
        await rig.HoldConsumerAsync();

        await rig.Enqueue(new TextDeltaMessage("a"));
        await rig.Enqueue(new TextDeltaMessage("b")); // очередь полна — сливается с «a»
        await rig.Enqueue(Tool("t1"));
        await rig.Enqueue(new TextDeltaMessage("c")); // хвост — ToolUse, слить нельзя
        await rig.Enqueue(new TextDeltaMessage("d")); // сливается с «c»

        var frames = await rig.FinishAsync();

        frames.Should().HaveCount(3);
        frames[0].Should().BeOfType<TextDeltaMessage>().Which.Text.Should().Be("ab");
        frames[1].Should().BeOfType<ToolUseMessage>();
        frames[2].Should().BeOfType<TextDeltaMessage>().Which.Text.Should().Be("cd");
        rig.Pipeline.Coalesced.Should().Be(2);
    }

    [Fact]
    public async Task Слияние_ToolInputDelta_ПобеждаетПоследнийТогоЖеИнструмента_РазныеНеСливаются()
    {
        var rig = new Rig(capacity: 1);
        await rig.HoldConsumerAsync();

        await rig.Enqueue(new ToolInputDeltaMessage("t1", "{\"a\""));
        await rig.Enqueue(new ToolInputDeltaMessage("t1", "{\"a\":1")); // накопительный JSON — последний побеждает
        await rig.Enqueue(new ToolInputDeltaMessage("t2", "{\"b\":2")); // другой инструмент — отдельный кадр

        var frames = await rig.FinishAsync();

        frames.Should().HaveCount(2);
        frames[0].Should().BeEquivalentTo(new ToolInputDeltaMessage("t1", "{\"a\":1"));
        frames[1].Should().BeEquivalentTo(new ToolInputDeltaMessage("t2", "{\"b\":2"));
    }

    [Fact]
    public async Task Слияние_ДельтаДругогоВида_НеСливается()
    {
        var rig = new Rig(capacity: 1);
        await rig.HoldConsumerAsync();

        await rig.Enqueue(new TextDeltaMessage("a"));
        await rig.Enqueue(new ThinkingDeltaMessage("m"));

        var frames = await rig.FinishAsync();

        frames.Select(f => f.GetType()).Should().Equal(typeof(TextDeltaMessage), typeof(ThinkingDeltaMessage));
    }

    [Fact]
    public async Task БайтовыйПорог_ТекстНесливаемыхКадровВышеПорога_ПредупреждаетИДоставляетВсе()
    {
        var rig = new Rig(capacity: 10, bytesLimit: 100);
        await rig.HoldConsumerAsync();

        for (var i = 0; i < 3; i++)
            await rig.Enqueue(new ToolResultMessage("t" + i, new string('x', 60), false)); // 180 > 100

        rig.Pipeline.BytesWarnings.Should().Be(1, "порог пересечён один раз — предупреждение не повторяется на каждом кадре");

        var frames = await rig.FinishAsync();
        frames.OfType<ToolResultMessage>().Should().HaveCount(3, "порог — наблюдаемость, кадры не выбрасываются");
    }

    [Fact]
    public async Task БайтовыйПорог_НижеПорога_НеПредупреждает()
    {
        var rig = new Rig(capacity: 10, bytesLimit: 100);
        await rig.HoldConsumerAsync();

        await rig.Enqueue(new ToolResultMessage("t0", new string('x', 60), false));

        rig.Pipeline.BytesWarnings.Should().Be(0);
        await rig.FinishAsync();
    }
}
