using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

public class ChatHistoryPaginatorTests
{
    // Генератор списка сообщений: msg-0 — самое старое, msg-(N-1) — самое новое.
    // Тексты уникальны, чтобы проверять границы пачки по содержиманию, а не только по счётчику.
    private static IReadOnlyList<StoredMessage> Messages(int count) =>
        Enumerable.Range(0, count).Select(i => (StoredMessage)new StoredTextMessage($"msg-{i}")).ToList();

    [Fact]
    public void Slice_Tail_ReturnsLastLimitMessages_WithCursorAtOldest()
    {
        var all = Messages(350);

        var page = ChatHistoryPaginator.Slice(all, limit: 100, before: null);

        page.Messages.Should().HaveCount(100);
        page.Messages[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("msg-250");
        page.Messages[^1].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("msg-349");
        page.HasMore.Should().BeTrue();
        page.Cursor.Should().Be(250, "курсор — индекс самого старого сообщения в пачке");
    }

    [Fact]
    public void Slice_BeforeCursor_LoadsEarlierBatch()
    {
        var all = Messages(350);

        // Фронт жмёт «показать ещё» с курсором 250 из хвоста → пачка [150..249]
        var page = ChatHistoryPaginator.Slice(all, limit: 100, before: 250);

        page.Messages.Should().HaveCount(100);
        page.Messages[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("msg-150");
        page.Messages[^1].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("msg-249");
        page.HasMore.Should().BeTrue();
        page.Cursor.Should().Be(150);
    }

    [Fact]
    public void Slice_FinalBatch_HasMoreFalse_CursorNull()
    {
        var all = Messages(350);

        // Последняя догрузка: курсор 50 → пачка [0..49], дальше начала нет
        var page = ChatHistoryPaginator.Slice(all, limit: 100, before: 50);

        page.Messages.Should().HaveCount(50);
        page.Messages[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("msg-0");
        page.HasMore.Should().BeFalse();
        page.Cursor.Should().BeNull("дошли до начала — дальше грузить нечего");
    }

    [Fact]
    public void Slice_LimitLessThanTotal_WithoutBefore_TailOnly()
    {
        var all = Messages(50);

        var page = ChatHistoryPaginator.Slice(all, limit: 10, before: null);

        page.Messages.Should().HaveCount(10);
        page.Messages[^1].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("msg-49");
        page.HasMore.Should().BeTrue();
        page.Cursor.Should().Be(40);
    }

    [Fact]
    public void Slice_LimitGreaterThanTotal_ReturnsAll_NoMore()
    {
        var all = Messages(5);

        var page = ChatHistoryPaginator.Slice(all, limit: 100, before: null);

        page.Messages.Should().HaveCount(5);
        page.HasMore.Should().BeFalse();
        page.Cursor.Should().BeNull();
    }

    [Fact]
    public void Slice_LimitNull_DefaultsTo100()
    {
        var all = Messages(150);

        var page = ChatHistoryPaginator.Slice(all, limit: null, before: null);

        page.Messages.Should().HaveCount(100, "дефолтный размер страницы — 100");
        page.HasMore.Should().BeTrue();
        page.Cursor.Should().Be(50);
    }

    [Fact]
    public void Slice_LimitClampedToLowerBound_TakesAtLeastOne()
    {
        var all = Messages(10);

        // limit=0 или отрицательный — прижимается к 1, а не ломает запрос
        var pageZero = ChatHistoryPaginator.Slice(all, limit: 0, before: null);
        var pageNeg = ChatHistoryPaginator.Slice(all, limit: -5, before: null);

        pageZero.Messages.Should().ContainSingle();
        pageNeg.Messages.Should().ContainSingle();
        pageZero.HasMore.Should().BeTrue();
        pageZero.Cursor.Should().Be(9);
    }

    [Fact]
    public void Slice_LimitClampedToUpperBound_DoesNotExceedMax()
    {
        // Запрос на 100000 сообщений у 10-элементного списка не должен ни взорвать аллокации,
        // ни отдать больше MaxLimit — clamp режет до потолка, а дальше реальный размер списка
        var all = Messages(10);

        var page = ChatHistoryPaginator.Slice(all, limit: 100_000, before: null);

        page.Messages.Should().HaveCount(10);
        page.HasMore.Should().BeFalse();
    }

    [Fact]
    public void Slice_BeforeZero_EmptyResult_NoMore()
    {
        var all = Messages(50);

        // before=0 — «всё перед индексом 0», то есть ничего; формально валидный край
        var page = ChatHistoryPaginator.Slice(all, limit: 100, before: 0);

        page.Messages.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.Cursor.Should().BeNull();
    }

    [Fact]
    public void Slice_BeforeEqualsTotal_SameAsTail()
    {
        var all = Messages(350);

        // before=total — курсор на конец массива, эквивалент хвоста
        var page = ChatHistoryPaginator.Slice(all, limit: 100, before: 350);

        page.Messages.Should().HaveCount(100);
        page.Messages[0].Should().BeOfType<StoredTextMessage>().Which.Text.Should().Be("msg-250");
        page.HasMore.Should().BeTrue();
        page.Cursor.Should().Be(250);
    }

    [Fact]
    public void Slice_EmptyHistory_ReturnsEmptyPage()
    {
        var page = ChatHistoryPaginator.Slice(Array.Empty<StoredMessage>(), limit: 100, before: null);

        page.Messages.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.Cursor.Should().BeNull();
    }

    [Theory]
    [InlineData(350, 0, true)]      // нижний край — валиден (пустой результат, но не ошибка)
    [InlineData(350, 350, true)]    // верхний край — хвост
    [InlineData(350, 250, true)]    // обычный курсор из хвоста
    [InlineData(350, 351, false)]   // за пределами массива — несуществующий индекс
    [InlineData(350, -1, false)]    // отрицательный — невалиден
    [InlineData(0, 0, true)]        // пустая история, before=0 — формально валидный край
    [InlineData(0, 1, false)]       // пустая история, любой положительный before — невалиден
    public void IsCursorValid_Boundaries(int total, int before, bool expected)
    {
        ChatHistoryPaginator.IsCursorValid(total, before).Should().Be(expected);
    }
}
