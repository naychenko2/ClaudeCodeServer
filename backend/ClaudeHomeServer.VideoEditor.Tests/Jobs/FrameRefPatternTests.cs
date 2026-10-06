using ClaudeHomeServer.Services.VideoEditor.Jobs;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Jobs;

// Имя кадра рабочей папки — строго <32 hex>.<png|jpg|webp>; перевод строки в конце не проходит ($ его пропускал)
public sealed class FrameRefPatternTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Верное_имя_проходит() => VideoEditWorkspace.IsFrameRef($"frames/{Id}.png").Should().BeTrue();

    [Theory]
    [InlineData("\\n")]
    [InlineData("\\r\\n")]
    public void Перевод_строки_в_конце_отклоняется(string tail) =>
        VideoEditWorkspace.IsFrameRef($"frames/{Id}.png" + tail.Replace("\\r", "\r").Replace("\\n", "\n")).Should().BeFalse();
}
