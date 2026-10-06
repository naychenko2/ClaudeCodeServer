using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.WebDav;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Самодиагностика MIC (<see cref="NtlmMicProbe"/>) и описание транспорта Negotiate. Клиент NTLMv2
/// с MIC собирается прямо в тесте по MS-NLMP: проба обязана назвать ту гипотезу ключа, по которой
/// клиент подписал рукопожатие, и честно сказать «ни одна», когда подпись испорчена.
/// </summary>
public class WebDavNtlmMicProbeTests
{
    private const uint SignSealKeyExch = 0xE2888235; // Type3 реального Windows из отказа 2026-10-06
    private const uint KeyExchOnly = 0xE2088207;
    private static readonly byte[] NtHash = NtlmHelper.ComputeNtHash("secret");

    [Fact]
    public void MicSignedWithRandomSessionKey_NamesRc4Hypothesis()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey);

        var report = NtlmMicProbe.Explain([h.Type1], [h.Type2], h.Type3, NtHash);

        report.Should().Contain("MIC СХОДИТСЯ").And.Contain("RC4(KeyExchangeKey, EncryptedRandomSessionKey)");
        report.Should().Contain("смещение MIC 72").And.Contain("Type3 0xE2888235");
    }

    [Fact]
    public void MicSignedWithKxkeyEssRandomKey_NamesKxkeyEssHypothesis()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.KxkeyEssRandomKey);

        var report = NtlmMicProbe.Explain([h.Type1], [h.Type2], h.Type3, NtHash);

        report.Should().Contain("MIC СХОДИТСЯ").And.Contain("RC4(KXKEY-ESS, EncryptedRandomSessionKey)");
    }

    [Fact]
    public void MicOverType1WithoutVersion_NamesTruncatedType1()
    {
        // Клиент подписал Type1 без Version, а на проводе (и в записи сервера) он полный
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey, micType1Length: 32);

        NtlmMicProbe.Explain([h.Type1], [h.Type2], h.Type3, NtHash)
            .Should().Contain("MIC СХОДИТСЯ").And.Contain("без Version (32 байта)");
    }

    [Fact]
    public void MicSignedWithKeyExchangeKey_NamesNoDecryptHypothesis()
    {
        // Клиент без SIGN/SEAL: ExportedSessionKey = KeyExchangeKey (MS-NLMP 3.1.5.1.2)
        var h = Handshake(KeyExchOnly, KeyMode.KeyExchangeKey);

        var report = NtlmMicProbe.Explain([h.Type1], [h.Type2], h.Type3, NtHash);

        report.Should().Contain("MIC СХОДИТСЯ").And.Contain("KeyExchangeKey (без расшифровки)");
        report.Should().NotContain("RC4(");
    }

    [Fact]
    public void TamperedMic_ReportsNoHypothesisMatches()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey, tamperMic: true);

        var report = NtlmMicProbe.Explain([h.Type1], [h.Type2], h.Type3, NtHash);

        report.Should().Contain("MIC не сходится ни в одной").And.Contain("NTProofStr верен");
    }

    [Fact]
    public void WrongType1_MicMatchesNothing()
    {
        // Сервер записал не тот Type1, по которому клиент считал MIC (подмена или чужое соединение)
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey);
        var other = (byte[])h.Type1.Clone();
        other[12] ^= 0x01;

        NtlmMicProbe.Explain([other], [h.Type2], h.Type3, NtHash)
            .Should().Contain("MIC не сходится ни в одной");
    }

    [Fact]
    public void SeveralType1AndType2_PicksTheOnesTheClientUsed()
    {
        // На HTTP/2 соединение несёт несколько рукопожатий; Type2 находится по NTProofStr
        var mine = Handshake(SignSealKeyExch, KeyMode.RandomKey);
        var foreign = Handshake(SignSealKeyExch, KeyMode.RandomKey);

        var report = NtlmMicProbe.Explain([foreign.Type1, mine.Type1], [foreign.Type2, mine.Type2], mine.Type3, NtHash);

        report.Should().Contain("MIC СХОДИТСЯ").And.Contain("Type1 №2");
    }

    [Fact]
    public void NoMatch_ReportsWireMessagesAndTimelineButNotType3Body()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey, tamperMic: true);

        var report = NtlmMicProbe.Explain([h.Type1], [h.Type2], h.Type3, NtHash, "0мс T1; 3мс T2");

        report.Should().Contain(Convert.ToHexString(h.Type1)).And.Contain(Convert.ToHexString(h.Type2));
        report.Should().Contain("хроника соединения: 0мс T1; 3мс T2");
        report.Should().NotContain(Convert.ToHexString(h.Type3));
        report.Should().NotContain(Convert.ToHexString(h.Type3.AsSpan(88)));
    }

    [Fact]
    public void WrongHash_ReportsProofMismatch()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey);

        NtlmMicProbe.Explain([h.Type1], [h.Type2], h.Type3, NtlmHelper.ComputeNtHash("other"))
            .Should().Contain("NTProofStr не сходится");
    }

    [Fact]
    public void NoType2Recorded_SaysSo()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey);

        NtlmMicProbe.Explain([h.Type1], [], h.Type3, NtHash).Should().Contain("Type2 этого соединения не записан");
    }

    [Fact]
    public void NotType3_SaysSo()
    {
        NtlmMicProbe.Explain([], [], "NTLMSSP\0"u8.ToArray(), NtHash).Should().Be("не Type3");
    }

    [Fact]
    public void Probe_DoesNotLeakKeysOrHash()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey);

        var report = NtlmMicProbe.Explain([h.Type1], [h.Type2], h.Type3, NtHash);

        report.Should().NotContain(Convert.ToHexString(NtHash)).And.NotContain(Convert.ToHexString(h.Mic));
    }

    [Fact]
    public void Rc4_MatchesRfc6229Vector()
    {
        // RFC 6229, ключ 0102030405 (40 бит), первые 16 байт потока
        var keystream = NtlmMicProbe.Rc4([1, 2, 3, 4, 5], new byte[16]);

        Convert.ToHexString(keystream).Should().Be("B2396305F03DC027CCC3524A0A1118A8");
    }

    [Fact]
    public void ExternalVector_ChromiumMicOverSpecMessages_Matches()
    {
        // Независимый эталон: net/ntlm/ntlm_test_data.h Chromium (kExpectedNegotiateMsg, kChallengeMsgFromSpecV2,
        // kExpectedAuthenticateMsgSpecResponseV2 с MIC kExpectedMicV2; пользователь User, домен Domain, пароль
        // Password из MS-NLMP 4.2). Реализация Chromium работает против настоящих Windows-серверов, поэтому
        // проба обязана подтвердить этот MIC — иначе она сама считает иначе, чем Windows.
        var type1 = Convert.FromHexString("4e544c4d53535000010000000782080000000000200000000000000020000000");
        var type2 = Convert.FromHexString(
            "4e544c4d53535000020000000c000c003800000033828ae20123456789abcdef0000000000000000240024004400000006007017000000"
            + "0f53006500720076006500720002000c0044006f006d00610069006e0001000c0053006500720076006500720000000000");
        var type3 = Convert.FromHexString(
            "4e544c4d535350000300000018001800580000008a008a00700000000c000c00fa0000000800080006010000100010000e010000000000"
            + "0058000000038208000000000000000000f7361633f0ad9bdf4a7c421bc6b824a300000000000000000000000000000000000000000000"
            + "00008c0260dbef690662af9c42d50782d2ed0101000000000000800bc8fd00d4d201aaaaaaaaaaaaaaaa0000000002000c0044006f006d"
            + "00610069006e0001000c0053006500720076006500720006000400020000000a0010006586e99d81c2fc984e47172fd4dd031009001600"
            + "48005400540050002f00530065007200760065007200000000000000000044006f006d00610069006e00550073006500720043004f004d"
            + "0050005500540045005200");
        var hash = Convert.FromHexString("a4f49c406510bdcab6824ee7c30fd852");

        var report = NtlmMicProbe.Explain([type1], [type2], type3, hash);

        report.Should().Contain("MIC СХОДИТСЯ").And.Contain("KeyExchangeKey (без расшифровки)");
    }

    [Fact]
    public void DescribeTransport_RawNtlm()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey);

        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String(h.Type3))
            .Should().Be("сырой NTLMSSP");
        Convert.ToBase64String(h.Type3).Should().StartWith("TlRMTVNT");
    }

    [Fact]
    public void DescribeTransport_SpnegoWithAndWithoutMechListMic()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey);

        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String(NegTokenResp(h.Type3, withMic: true)))
            .Should().Be("SPNEGO NegTokenResp с mechListMIC");
        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String(NegTokenResp(h.Type3, withMic: false)))
            .Should().Be("SPNEGO NegTokenResp без mechListMIC");
        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String(NegTokenInit(h.Type1)))
            .Should().Be("SPNEGO NegTokenInit");
    }

    [Fact]
    public void DescribeTransport_Garbage()
    {
        NegotiateFailure.DescribeTransport("Basic YTpi").Should().Be("не Negotiate");
        NegotiateFailure.DescribeTransport("Negotiate ***").Should().Be("не base64");
        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String([0x55, 1])).Should().Contain("неизвестный формат");
        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String([0xA1, 0x84, 0xFF])).Should().Contain("без mechListMIC");
    }

    [Fact]
    public void ExtractNtlm_FindsMessageRawAndInsideSpnego()
    {
        var h = Handshake(SignSealKeyExch, KeyMode.RandomKey);

        NtlmHandshakeRecorder.ExtractNtlm(Convert.ToBase64String(h.Type1)).Should().Equal(h.Type1);
        NtlmHandshakeRecorder.ExtractNtlm(Convert.ToBase64String(NegTokenInit(h.Type1))).Should().Equal(h.Type1);
        NtlmHandshakeRecorder.ExtractNtlm("не base64").Should().BeNull();
    }

    private enum KeyMode { RandomKey, KeyExchangeKey, KxkeyEssRandomKey }

    private sealed record Messages(byte[] Type1, byte[] Type2, byte[] Type3, byte[] Mic);

    /// <summary>Клиент NTLMv2 с MIC (MS-NLMP 3.1.5.1.2): флаги Type3 и ключ MIC задаёт тест.</summary>
    private static Messages Handshake(uint type3Flags, KeyMode keyMode, bool tamperMic = false, int micType1Length = 40)
    {
        var type1 = new byte[40];
        "NTLMSSP\0"u8.CopyTo(type1);
        BitConverter.GetBytes(1u).CopyTo(type1, 8);
        BitConverter.GetBytes(0xE2088237u).CopyTo(type1, 12);

        var challenge = RandomNumberGenerator.GetBytes(8);
        var type2 = new byte[56];
        "NTLMSSP\0"u8.CopyTo(type2);
        BitConverter.GetBytes(2u).CopyTo(type2, 8);
        BitConverter.GetBytes(0xE28A8235u).CopyTo(type2, 20);
        challenge.CopyTo(type2, 24);

        var avPairs = new byte[] { 6, 0, 4, 0, 2, 0, 0, 0, 0, 0, 0, 0 }; // MsvAvFlags=2 (MIC), EOL
        var blob = new byte[28 + avPairs.Length];
        blob[0] = 1; blob[1] = 1;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(blob, 8);
        RandomNumberGenerator.GetBytes(8).CopyTo(blob, 16);
        avPairs.CopyTo(blob, 28);

        var user = Encoding.Unicode.GetBytes("andrey");
        var domain = Encoding.Unicode.GetBytes("WORKGROUP");
        var responseKey = NtlmMicProbe.HmacMd5(NtHash, Encoding.Unicode.GetBytes("ANDREY" + "WORKGROUP"));
        var proof = NtlmMicProbe.HmacMd5(responseKey, [.. challenge, .. blob]);
        byte[] nt = [.. proof, .. blob];
        var keyExchangeKey = NtlmMicProbe.HmacMd5(responseKey, proof);
        var random = RandomNumberGenerator.GetBytes(16);
        // клиент, считающий KeyExchangeKey по KXKEY расширенной защиты (LM-ответ в тесте нулевой)
        var kxkeyEss = NtlmMicProbe.HmacMd5(keyExchangeKey, [.. challenge, .. new byte[8]]);
        var encrypted = NtlmMicProbe.Rc4(keyMode == KeyMode.KxkeyEssRandomKey ? kxkeyEss : keyExchangeKey, random);
        var micKey = keyMode == KeyMode.KeyExchangeKey ? keyExchangeKey : random;

        const int header = 88; // 64 + Version(8) + MIC(16)
        var lm = new byte[24];
        var payload = new List<byte[]> { lm, nt, domain, user, Array.Empty<byte>(), encrypted };
        var type3 = new byte[header + payload.Sum(p => p.Length)];
        "NTLMSSP\0"u8.CopyTo(type3);
        BitConverter.GetBytes(3u).CopyTo(type3, 8);
        var at = header;
        for (var i = 0; i < payload.Count; i++)
        {
            var p = payload[i];
            var f = 12 + (i * 8);
            BitConverter.GetBytes((ushort)p.Length).CopyTo(type3, f);
            BitConverter.GetBytes((ushort)p.Length).CopyTo(type3, f + 2);
            BitConverter.GetBytes((uint)at).CopyTo(type3, f + 4);
            p.CopyTo(type3, at);
            at += p.Length;
        }
        BitConverter.GetBytes(type3Flags).CopyTo(type3, 60);
        new byte[] { 10, 0, 0x63, 0x45, 0, 0, 0, 0x0F }.CopyTo(type3, 64);

        var mic = NtlmMicProbe.HmacMd5(micKey, [.. type1.AsSpan(0, micType1Length), .. type2, .. type3]);
        if (tamperMic) mic[0] ^= 1;
        mic.CopyTo(type3, 72);
        return new Messages(type1, type2, type3, mic);
    }

    private static byte[] Der(byte tag, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        byte[] len = body.Length < 128 ? [(byte)body.Length]
            : body.Length < 256 ? [0x81, (byte)body.Length]
            : [0x82, (byte)(body.Length >> 8), (byte)body.Length];
        return [tag, .. len, .. body];
    }

    private static byte[] NegTokenInit(byte[] type1)
    {
        byte[] ntlmOid = [0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0A];
        byte[] spnegoOid = [0x06, 0x06, 0x2B, 0x06, 0x01, 0x05, 0x05, 0x02];
        var init = Der(0x30, Der(0xA0, Der(0x30, ntlmOid)), Der(0xA2, Der(0x04, type1)));
        return Der(0x60, spnegoOid, Der(0xA0, init));
    }

    private static byte[] NegTokenResp(byte[] type3, bool withMic)
    {
        var parts = new List<byte[]> { Der(0xA2, Der(0x04, type3)) };
        if (withMic) parts.Add(Der(0xA3, Der(0x04, new byte[16])));
        return Der(0xA1, Der(0x30, [.. parts]));
    }
}
