using ClaudeHomeServer.Services.ChatContext;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

public class ContextKindRegistryTests
{
    [Fact]
    public void DuplicateKind_ThrowsOnBuild()
    {
        var act = () => new ContextKindRegistry([new FakeKindProvider("image", "audio"), new FakeKindProvider("audio")]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*audio*");
    }

    [Fact]
    public void DifferentKinds_AreRegistered_AndFoundByOwner()
    {
        var images = new FakeKindProvider("image");
        var registry = new ContextKindRegistry([images, new FakeKindProvider("audio")]);

        registry.IsRegistered("image").Should().BeTrue();
        registry.IsRegistered("video").Should().BeFalse();
        registry.Find("image").Should().BeSameAs(images);
        registry.Kinds.Should().BeEquivalentTo("image", "audio");
    }

    [Fact]
    public void Validate_UnknownKind_GivesRefusalText()
    {
        var registry = new ContextKindRegistry([new FakeKindProvider("image")]);
        registry.Validate(null!, "audio", new()).Should().NotBeNull();
        registry.Validate(null!, "image", new()).Should().BeNull();
    }
}
