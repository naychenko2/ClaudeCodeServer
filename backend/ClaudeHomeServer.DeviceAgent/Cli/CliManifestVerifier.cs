using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace ClaudeHomeServer.DeviceAgent.Cli;

/// <summary>
/// Проверка отсоединённой OpenPGP-подписи <c>manifest.json.sig</c> против закреплённого ключа.
/// Подпись манифеста транзитивно заверяет SHA256 каждого бинаря в нём — так же, как
/// <c>gpg --verify manifest.json.sig manifest.json</c> из официальной документации.
///
/// Внешний <c>gpg</c> не зовём: на Windows его обычно нет, а доверие к бинарю в PATH — та
/// же дыра, что и без проверки. Разбор и криптография — BouncyCastle (управляемый код).
///
/// Правила строже, чем у gpg по умолчанию: ровно одна подпись, тип «двоичный документ»,
/// хеш не слабее SHA-256, подписант — именно закреплённый ключ.
/// </summary>
public sealed class CliManifestVerifier
{
    // Подпись RSA-4096 в armor — около килобайта; потолок не даёт скормить разбору мегабайты.
    internal const int MaxSignatureBytes = 64 * 1024;

    private static readonly HashSet<HashAlgorithmTag> AcceptedHashes =
        [HashAlgorithmTag.Sha256, HashAlgorithmTag.Sha384, HashAlgorithmTag.Sha512];

    private readonly PgpPublicKey _key;

    /// <summary>Боевой верификатор: ключ выпусков Claude Code, закреплённый в агенте.</summary>
    public static CliManifestVerifier Anthropic { get; } = new(AnthropicReleaseKey.Armored, AnthropicReleaseKey.Fingerprint);

    /// <param name="armoredPublicKey">Публичный ключ в ASCII-armor.</param>
    /// <param name="fingerprint">Ожидаемый отпечаток v4 (40 hex, пробелы допустимы): текст ключа
    /// без совпавшего отпечатка — ошибка сборки агента, а не повод доверять.</param>
    public CliManifestVerifier(string armoredPublicKey, string fingerprint)
    {
        var expected = fingerprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(armoredPublicKey)));
        var bundle = new PgpPublicKeyRingBundle(input);
        _key = bundle.GetKeyRings()
                   .SelectMany(ring => ring.GetPublicKeys())
                   .FirstOrDefault(k => Convert.ToHexString(k.GetFingerprint()) == expected)
               ?? throw new InvalidOperationException($"в закреплённом ключе нет ключа с отпечатком {expected}");
        Fingerprint = expected;
    }

    public string Fingerprint { get; }

    /// <summary>
    /// Бросает <see cref="CliIntegrityException"/>, если подписи нет или она не заверяет
    /// ровно эти байты манифеста закреплённым ключом.
    /// </summary>
    public void Verify(byte[] manifest, byte[]? signature)
    {
        if (signature is null || signature.Length == 0)
            throw new CliIntegrityException("у манифеста выпуска нет подписи manifest.json.sig");
        if (signature.Length > MaxSignatureBytes)
            throw new CliIntegrityException("подпись манифеста подозрительно большая");

        PgpSignature sig;
        try
        {
            sig = ReadSingleSignature(signature);
        }
        catch (Exception e) when (e is not CliIntegrityException)
        {
            throw new CliIntegrityException($"подпись манифеста не читается: {e.Message}");
        }

        if (sig.KeyId != _key.KeyId)
            throw new CliIntegrityException(
                $"манифест подписан чужим ключом {sig.KeyId:X16}, ожидался ключ Anthropic {Fingerprint}");
        if (sig.SignatureType != PgpSignature.BinaryDocument)
            throw new CliIntegrityException($"неожиданный тип подписи манифеста: {sig.SignatureType}");
        if (!AcceptedHashes.Contains(sig.HashAlgorithm))
            throw new CliIntegrityException($"подпись манифеста на слабом хеше {sig.HashAlgorithm}");

        bool valid;
        try
        {
            sig.InitVerify(_key);
            sig.Update(manifest);
            valid = sig.Verify();
        }
        catch (Exception e)
        {
            throw new CliIntegrityException($"подпись манифеста не проверяется: {e.Message}");
        }
        if (!valid)
            throw new CliIntegrityException("подпись манифеста не сходится: манифест изменён после подписи");
    }

    private static PgpSignature ReadSingleSignature(byte[] signature)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(signature));
        var factory = new PgpObjectFactory(input);
        if (factory.NextPgpObject() is not PgpSignatureList list || list.Count != 1)
            throw new CliIntegrityException("в manifest.json.sig ожидается ровно одна подпись");
        if (factory.NextPgpObject() is not null)
            throw new CliIntegrityException("в manifest.json.sig лишние данные после подписи");
        return list[0];
    }
}
