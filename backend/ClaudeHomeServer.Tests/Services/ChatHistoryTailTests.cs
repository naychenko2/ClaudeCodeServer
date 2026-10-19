using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Докачка истории по курсору (Перф-2 B5): чистая функция ChatHistoryTail.Compute —
// хвост при совпадении отпечатка префикса, reset при расхождении/выходе за пределы,
// полный список без курсора. Якорь — последняя завершающая запись хода.
public class ChatHistoryTailTests
{
    private static List<StoredMessage> DoneTurn() =>
    [
        new StoredUserMessage("привет"),
        new StoredTextMessage("ответ"),
        new StoredResultMessage("success", 10, 1),
    ];

    // Два завершённых хода: якорь первого — индекс 2, финальный — индекс 5
    private static List<StoredMessage> TwoTurns() =>
    [
        new StoredUserMessage("первый"),
        new StoredTextMessage("ответ 1"),
        new StoredResultMessage("success", 10, 1),
        new StoredUserMessage("второй"),
        new StoredTextMessage("ответ 2"),
        new StoredResultMessage("success", 20, 2),
    ];

    [Fact]
    public void PrefixFingerprint_ОдинаковыйПрефикс_ОдинаковыйОтпечаток()
    {
        ChatHistoryTail.PrefixFingerprint(TwoTurns(), 2).Should().Be(ChatHistoryTail.PrefixFingerprint(TwoTurns(), 2));
    }

    [Fact]
    public void PrefixFingerprint_ПрефиксыРазнойДлины_РазныеОтпечатки()
    {
        var h = TwoTurns();
        ChatHistoryTail.PrefixFingerprint(h, 2).Should().NotBe(ChatHistoryTail.PrefixFingerprint(h, 5));
    }

    [Fact]
    public void PrefixFingerprint_ПозднийResultВToolUseВСередине_МеняетОтпечаток()
    {
        // Фоновый агент дописывает result в tool_use прошлого хода уже после его конца:
        // отпечаток одной записи-якоря это не увидел бы, префикса — видит
        var tu = new StoredToolUseMessage { Id = "t1", Name = "Task", Input = "{}" };
        List<StoredMessage> h =
        [
            new StoredUserMessage("в"), tu, new StoredResultMessage("success", 10, 1),
        ];
        var before = ChatHistoryTail.PrefixFingerprint(h, 2);
        tu.Result = "агент закончил";
        ChatHistoryTail.PrefixFingerprint(h, 2).Should().NotBe(before);
    }

    [Fact]
    public void AnchorIndex_ЗавершённыйХод_ИндексResult()
    {
        ChatHistoryTail.AnchorIndex(TwoTurns()).Should().Be(5);
    }

    [Fact]
    public void AnchorIndex_ТолькоИдущийХод_МинусОдин()
    {
        List<StoredMessage> h = [new StoredUserMessage("в"), new StoredTextMessage("печатаю")];
        ChatHistoryTail.AnchorIndex(h).Should().Be(-1);
    }

    [Fact]
    public void Compute_БезКурсора_ПолнаяИсторияБезReset()
    {
        var history = TwoTurns();
        var r = ChatHistoryTail.Compute(history, after: null, fp: null);
        r.Reset.Should().BeFalse();
        r.Messages.Should().BeSameAs(history);
        r.AnchorIndex.Should().Be(5);
        r.AnchorFp.Should().Be(ChatHistoryTail.PrefixFingerprint(history, 5));
    }

    [Fact]
    public void Compute_КурсорВКонце_ПустойХвост()
    {
        var history = DoneTurn(); // якорь = 2
        var r = ChatHistoryTail.Compute(history, after: 2, ChatHistoryTail.PrefixFingerprint(history, 2));
        r.Reset.Should().BeFalse();
        r.Messages.Should().BeEmpty();
        r.AnchorIndex.Should().Be(2);
    }

    [Fact]
    public void Compute_КурсорВСередине_ХвостПослеКурсора()
    {
        var history = TwoTurns();
        var r = ChatHistoryTail.Compute(history, after: 2, ChatHistoryTail.PrefixFingerprint(history, 2));
        r.Reset.Should().BeFalse();
        r.Messages.Should().HaveCount(3);
        r.Messages[0].Should().BeSameAs(history[3]);
        r.AnchorIndex.Should().Be(5);
        r.AnchorFp.Should().Be(ChatHistoryTail.PrefixFingerprint(history, 5));
    }

    [Fact]
    public void Compute_НесходящийсяОтпечаток_ResetИПолнаяИстория()
    {
        var history = DoneTurn();
        var r = ChatHistoryTail.Compute(history, after: 2, fp: "не-тот-отпечаток");
        r.Reset.Should().BeTrue();
        r.Messages.Should().BeSameAs(history);
    }

    [Fact]
    public void Compute_ПравкаЗаписиДоКурсора_Reset()
    {
        // Клиент взял отпечаток, потом запись ДО курсора переписали — reset, а не тихий хвост
        var tu = new StoredToolUseMessage { Id = "t1", Name = "Task", Input = "{}" };
        List<StoredMessage> h = [new StoredUserMessage("в"), tu, new StoredResultMessage("success", 1, 1)];
        var fp = ChatHistoryTail.PrefixFingerprint(h, 2);
        tu.Result = "поздний результат";
        ChatHistoryTail.Compute(h, after: 2, fp).Reset.Should().BeTrue();
    }

    [Fact]
    public void Compute_КурсорЗаПределами_Reset()
    {
        var history = DoneTurn();
        var r = ChatHistoryTail.Compute(history, after: 99, fp: "x");
        r.Reset.Should().BeTrue();
        r.Messages.Should().BeSameAs(history);
    }

    [Fact]
    public void Compute_МинусОдин_ResetИЯкорьДляПервойЗагрузки()
    {
        // after=-1 — «ленты нет, дай всё и якорь»: так клиент получает первый курсор
        var history = TwoTurns();
        var r = ChatHistoryTail.Compute(history, after: -1, fp: null);
        r.Reset.Should().BeTrue();
        r.Messages.Should().BeSameAs(history);
        r.AnchorIndex.Should().Be(5);
        r.AnchorFp.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Compute_БезЗавершённогоХода_ЯкорьМинусОдин()
    {
        List<StoredMessage> history = [new StoredUserMessage("в"), new StoredTextMessage("печатаю")];
        var r = ChatHistoryTail.Compute(history, after: null, fp: null);
        r.AnchorIndex.Should().Be(-1);
        r.AnchorFp.Should().BeNull();
    }
}
