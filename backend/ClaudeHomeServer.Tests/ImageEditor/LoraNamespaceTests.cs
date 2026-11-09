using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor;

// Единая функция видимости LoRA владельцу (пространство имён ccs/<ownerId:N>/)
public class LoraNamespaceTests
{
    private const string Owner = "AB12CD34-0000-4000-8000-00000000AAAA";
    private const string Prefix = "ccs/ab12cd34000040008000" + "00000000aaaa/";

    [Fact]
    public void Префикс_без_дефисов_в_нижнем_регистре() =>
        LoraNamespace.PrefixFor(Owner).Should().Be(Prefix);

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a b")]
    public void Префикс_из_мусора_не_строится(string owner) =>
        FluentActions.Invoking(() => LoraNamespace.PrefixFor(owner)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(Prefix + "anya.safetensors")]
    [InlineData(Prefix + "sub/anya_v1-2.safetensors")]
    public void Свой_префикс_виден(string name) => LoraNamespace.VisibleTo(Owner, name).Should().BeTrue();

    [Theory]
    [InlineData("ccs/00000000000000000000000000000000/anya.safetensors")]
    [InlineData("anya.safetensors")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(Prefix)]
    [InlineData(Prefix + "../other/x.safetensors")]
    [InlineData(Prefix + "a/../x.safetensors")]
    [InlineData(Prefix + "a//x.safetensors")]
    [InlineData(Prefix + "x/")]
    [InlineData(Prefix + "a\\x.safetensors")]
    [InlineData(Prefix + "c:x.safetensors")]
    [InlineData("/" + Prefix + "x.safetensors")]
    [InlineData("CCS/ab12cd3400004000800000000000aaaa/x.safetensors")]
    [InlineData("ccs/AB12CD3400004000800000000000AAAA/x.safetensors")]
    public void Чужое_и_подозрительное_скрыто(string? name) =>
        LoraNamespace.VisibleTo(Owner, name).Should().BeFalse();

    [Fact]
    public void Мусорный_владелец_ничего_не_видит() =>
        LoraNamespace.VisibleTo("../x", Prefix + "x.safetensors").Should().BeFalse();
}
