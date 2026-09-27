using ClaudeHomeServer.DeviceAgent.Cli;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace ClaudeHomeServer.DeviceAgent.Tests.Cli;

public sealed class CliManifestVerifierTests
{
    [Fact]
    public void Закреплённый_ключ_совпадает_с_отпечатком_из_документации()
    {
        // https://code.claude.com/docs/en/setup#binary-integrity-and-code-signing
        CliManifestVerifier.Anthropic.Fingerprint.Should().Be("31DDDE24DDFAB679F42D7BD2BAA929FF1A7ECACE");
        AnthropicReleaseKey.Fingerprint.Should().Be(CliManifestVerifier.Anthropic.Fingerprint);
    }

    [Fact]
    public void Ключ_без_ожидаемого_отпечатка_не_принимается()
    {
        FluentActions.Invoking(() => new CliManifestVerifier(AnthropicReleaseKey.Armored, TestPgpKey.Stranger.Fingerprint))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Настоящий_выпуск_проходит_проверку_закреплённым_ключом()
    {
        FluentActions.Invoking(() => CliManifestVerifier.Anthropic.Verify(RealRelease.Manifest, RealRelease.Signature))
            .Should().NotThrow();
        CliManifest.Parse(RealRelease.Manifest).Version.Should().Be(RealRelease.Version);
    }

    [Fact]
    public void Подменённый_манифест_при_настоящей_подписи_отвергается()
    {
        var manifest = RealRelease.Manifest;
        var text = System.Text.Encoding.UTF8.GetString(manifest);
        var checksum = CliManifest.Parse(manifest).Platforms["linux-x64"].Sha256;
        var tampered = System.Text.Encoding.UTF8.GetBytes(text.Replace(checksum, new string('0', 64)));

        FluentActions.Invoking(() => CliManifestVerifier.Anthropic.Verify(tampered, RealRelease.Signature))
            .Should().Throw<CliIntegrityException>().WithMessage("*не сходится*");
    }

    [Fact]
    public void Лишний_байт_в_конце_манифеста_ломает_подпись()
    {
        byte[] tampered = [.. RealRelease.Manifest, (byte)'\n'];

        FluentActions.Invoking(() => CliManifestVerifier.Anthropic.Verify(tampered, RealRelease.Signature))
            .Should().Throw<CliIntegrityException>();
    }

    [Fact]
    public void Подпись_чужим_ключом_отвергается()
    {
        var signature = TestPgpKey.Stranger.Sign(RealRelease.Manifest);

        FluentActions.Invoking(() => CliManifestVerifier.Anthropic.Verify(RealRelease.Manifest, signature))
            .Should().Throw<CliIntegrityException>().WithMessage("*чужим ключом*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Отсутствующая_подпись_отвергается(bool empty)
    {
        FluentActions.Invoking(() => CliManifestVerifier.Anthropic.Verify(RealRelease.Manifest, empty ? [] : null))
            .Should().Throw<CliIntegrityException>().WithMessage("*нет подписи*");
    }

    [Theory]
    [InlineData("<html>blocked</html>")]
    [InlineData("-----BEGIN PGP SIGNATURE-----\n\nAAAA\n-----END PGP SIGNATURE-----\n")]
    public void Мусор_вместо_подписи_это_отказ_целостности(string garbage)
    {
        FluentActions.Invoking(() => CliManifestVerifier.Anthropic.Verify(RealRelease.Manifest, System.Text.Encoding.ASCII.GetBytes(garbage)))
            .Should().Throw<CliIntegrityException>();
    }

    [Fact]
    public void Подпись_на_SHA1_отвергается_даже_доверенным_ключом()
    {
        var key = TestPgpKey.Trusted;
        var signature = key.Sign(RealRelease.Manifest, HashAlgorithmTag.Sha1);

        FluentActions.Invoking(() => key.Verifier().Verify(RealRelease.Manifest, signature))
            .Should().Throw<CliIntegrityException>().WithMessage("*слабом хеше*");
    }

    [Fact]
    public void Подпись_текстового_типа_отвергается()
    {
        var key = TestPgpKey.Trusted;
        var signature = key.Sign(RealRelease.Manifest, signatureType: PgpSignature.CanonicalTextDocument);

        FluentActions.Invoking(() => key.Verifier().Verify(RealRelease.Manifest, signature))
            .Should().Throw<CliIntegrityException>().WithMessage("*тип подписи*");
    }

    [Fact]
    public void Подпись_доверенного_тестового_ключа_проходит()
    {
        var key = TestPgpKey.Trusted;

        FluentActions.Invoking(() => key.Verifier().Verify(RealRelease.Manifest, key.Sign(RealRelease.Manifest)))
            .Should().NotThrow();
    }
}
