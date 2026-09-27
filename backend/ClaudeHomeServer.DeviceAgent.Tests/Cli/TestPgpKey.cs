using ClaudeHomeServer.DeviceAgent.Cli;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace ClaudeHomeServer.DeviceAgent.Tests.Cli;

/// <summary>Свой OpenPGP-ключ теста: подписывает фейковые выпуски и играет «чужой ключ».</summary>
internal sealed class TestPgpKey
{
    /// <summary>Ключ, которому доверяет верификатор тестов <see cref="ManagedCliTests"/>.</summary>
    public static TestPgpKey Trusted { get; } = new();

    /// <summary>Посторонний ключ: подпись им не должна проходить.</summary>
    public static TestPgpKey Stranger { get; } = new();

    private readonly PgpKeyPair _pair;

    private TestPgpKey()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), new SecureRandom(), 2048, 25));
        _pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, generator.GenerateKeyPair(), DateTime.UtcNow);
    }

    public string Fingerprint => Convert.ToHexString(_pair.PublicKey.GetFingerprint());

    public string ArmoredPublicKey
    {
        get
        {
            using var buffer = new MemoryStream();
            using (var armor = new ArmoredOutputStream(buffer))
                _pair.PublicKey.Encode(armor);
            return System.Text.Encoding.ASCII.GetString(buffer.ToArray());
        }
    }

    public CliManifestVerifier Verifier() => new(ArmoredPublicKey, Fingerprint);

    /// <summary>Отсоединённая armored-подпись, как у настоящего manifest.json.sig.</summary>
    public byte[] Sign(byte[] data, HashAlgorithmTag hash = HashAlgorithmTag.Sha512,
        int signatureType = PgpSignature.BinaryDocument)
    {
        var generator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, hash);
        generator.InitSign(signatureType, _pair.PrivateKey);
        generator.Update(data);

        using var buffer = new MemoryStream();
        using (var armor = new ArmoredOutputStream(buffer))
            generator.Generate().Encode(armor);
        return buffer.ToArray();
    }
}
