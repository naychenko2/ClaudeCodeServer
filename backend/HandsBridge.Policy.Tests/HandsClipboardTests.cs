using ClaudeHomeServer.HandsBridge.Policy;
using Xunit;

namespace HandsBridge.Policy.Tests;

/// <summary>
/// Текстовый буфер (D1c): <c>read</c> — только если буфер изменился после последнего собственного
/// ввода моста и не несёт формата «не копировать» (менеджеры паролей); <c>write</c> — до 20 000
/// символов.
/// </summary>
public class HandsClipboardTests
{
    private static HandsPolicy Policy() => new(new FakeWindows());

    private sealed class FakeClipboard : IHandsClipboardState
    {
        public uint SequenceNumber { get; set; }
        public uint SequenceNumberAtLastOwnInput { get; set; }
        public bool HadOwnInput { get; set; }
        public IReadOnlyCollection<string> FormatNames { get; set; } = Array.Empty<string>();
    }

    // ---------- read ----------

    [Fact]
    public void Read_without_a_change_since_own_input_is_denied()
    {
        var clip = new FakeClipboard { SequenceNumber = 5, SequenceNumberAtLastOwnInput = 5, HadOwnInput = true };
        var decision = Policy().CheckClipboard("read", 100, clip);
        Assert.False(decision.Allowed);
        Assert.Contains("has not changed", decision.Reason);
    }

    [Fact]
    public void Read_after_a_change_is_allowed()
    {
        var clip = new FakeClipboard { SequenceNumber = 6, SequenceNumberAtLastOwnInput = 5, HadOwnInput = true };
        Assert.True(Policy().CheckClipboard("read", 100, clip).Allowed);
    }

    // Переполнение uint: максимум → 0. Нуль — ДРУГОЙ номер, а не «не изменилось»: сравнение
    // «отличается от», а не «больше».
    [Fact]
    public void Read_after_uint_overflow_is_allowed()
    {
        var clip = new FakeClipboard
        {
            SequenceNumber = 0,
            SequenceNumberAtLastOwnInput = uint.MaxValue,
            HadOwnInput = true,
        };
        Assert.True(Policy().CheckClipboard("read", 100, clip).Allowed);
    }

    // Собственного ввода не было — читать нечего, даже если номер «свежий».
    [Fact]
    public void Read_without_any_own_input_is_denied()
    {
        var clip = new FakeClipboard { SequenceNumber = 7, SequenceNumberAtLastOwnInput = 0, HadOwnInput = false };
        var decision = Policy().CheckClipboard("read", 100, clip);
        Assert.False(decision.Allowed);
        Assert.Contains("has not changed", decision.Reason);
    }

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("CanIncludeInClipboardHistory=0")]
    public void Read_with_a_do_not_copy_format_is_denied(string format)
    {
        var clip = new FakeClipboard
        {
            SequenceNumber = 6,
            SequenceNumberAtLastOwnInput = 5,
            HadOwnInput = true,
            FormatNames = new[] { format },
        };
        var decision = Policy().CheckClipboard("read", 100, clip);
        Assert.False(decision.Allowed);
        Assert.Contains("do-not-copy", decision.Reason);
    }

    // ---------- write ----------

    [Fact]
    public void Write_is_capped_at_20000_characters()
    {
        var clip = new FakeClipboard();
        Assert.True(Policy().CheckClipboard("write", 20_000, clip).Allowed);
        Assert.False(Policy().CheckClipboard("write", 20_001, clip).Allowed);
    }

    // ---------- форма ----------

    [Fact]
    public void Unknown_action_is_denied()
    {
        var decision = Policy().CheckClipboard("copy", 100, new FakeClipboard());
        Assert.False(decision.Allowed);
        Assert.Contains("Unknown clipboard_text action", decision.Reason);
    }
}
