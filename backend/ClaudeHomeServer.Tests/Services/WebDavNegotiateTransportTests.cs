using ClaudeHomeServer.WebDav;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>Описание транспорта Negotiate в строке отказа: сырой NTLMSSP или SPNEGO.</summary>
public class WebDavNegotiateTransportTests
{
    private static byte[] Ntlm(uint type)
    {
        var msg = new byte[40];
        "NTLMSSP\0"u8.CopyTo(msg);
        BitConverter.GetBytes(type).CopyTo(msg, 8);
        return msg;
    }

    [Fact]
    public void DescribeTransport_RawNtlm()
    {
        var type3 = Convert.ToBase64String(Ntlm(3));

        NegotiateFailure.DescribeTransport("Negotiate " + type3).Should().Be("сырой NTLMSSP");
        type3.Should().StartWith("TlRMTVNT");
    }

    [Fact]
    public void DescribeTransport_SpnegoWithAndWithoutMechListMic()
    {
        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String(NegTokenResp(Ntlm(3), withMic: true)))
            .Should().Be("SPNEGO NegTokenResp с mechListMIC");
        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String(NegTokenResp(Ntlm(3), withMic: false)))
            .Should().Be("SPNEGO NegTokenResp без mechListMIC");
        NegotiateFailure.DescribeTransport("Negotiate " + Convert.ToBase64String(NegTokenInit(Ntlm(1))))
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
