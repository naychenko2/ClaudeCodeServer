using System.Text;
using Microsoft.AspNetCore.Authentication.Negotiate;

namespace ClaudeHomeServer.WebDav;

/// <summary>
/// Неудачное рукопожатие Negotiate. На Linux gss-ntlmssp на неверном пароле, неизвестном
/// пользователе и чужом домене отдаёт общий отказ: хендлер кидает исключение, и без этого
/// события клиент получал 500. Explorer на 500 (как и на повторный Negotiate) крутит
/// сохранённую учётку по кругу — поэтому ответ строго 401 с ОДНИМ Basic.
/// </summary>
public static class NegotiateFailure
{
    public static Task HandleAsync(AuthenticationFailedContext ctx)
    {
        var http = ctx.HttpContext;
        var logger = http.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(typeof(NegotiateFailure).FullName!);
        // Домен и имя нужны для разбора «какой домен прислал Mini-Redirector»; секретов в них нет
        var (domain, user) = TryReadType3Identity(http.Request.Headers.Authorization.ToString());
        // Статус .NET различает причины: InvalidToken — дефектный токен (MIC, channel binding),
        // GenericFailure — не сошёлся NTLMv2-ответ (пароль, регистр или написание домена)
        logger?.LogWarning("NTLM отклонён: домен '{Domain}', пользователь '{User}', {Path}: {Error}; Type3: {Shape}",
            domain ?? "?", user ?? "?", http.Request.Path.Value, ctx.Exception?.GetBaseException().Message,
            DescribeType3(http.Request.Headers.Authorization.ToString()));

        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.ContentLength = 0;
        http.Response.Headers.WWWAuthenticate = WebDavHandler.BuildAuthChallenge(ntlmAvailable: false);
        ctx.HandleResponse();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Домен и имя из NTLM Type3 (сырого или завёрнутого в SPNEGO) по заголовку Authorization.
    /// Ответы на вызов не трогаем — в лог уходят только эти два поля.
    /// </summary>
    internal static (string? Domain, string? User) TryReadType3Identity(string authorization)
    {
        const string prefix = "Negotiate ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (null, null);
        byte[] blob;
        try { blob = Convert.FromBase64String(authorization[prefix.Length..].Trim()); }
        catch (FormatException) { return (null, null); }

        var start = blob.AsSpan().IndexOf("NTLMSSP\0"u8);
        if (start < 0) return (null, null);
        var msg = blob.AsSpan(start);
        if (msg.Length < 64 || BitConverter.ToUInt32(msg[8..12]) != 3) return (null, null);

        var unicode = (BitConverter.ToUInt32(msg[60..64]) & 0x1) != 0;
        return (ReadField(msg, 28, unicode), ReadField(msg, 36, unicode));

        static string? ReadField(ReadOnlySpan<byte> m, int at, bool unicode)
        {
            int len = BitConverter.ToUInt16(m[at..(at + 2)]);
            var off = (int)BitConverter.ToUInt32(m[(at + 4)..(at + 8)]);
            if (off < 0 || off + len > m.Length) return null;
            var bytes = m.Slice(off, len);
            return unicode ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
        }
    }

    /// <summary>
    /// Форма NTLM Type3 для лога: флаги, версия ответа и AV-пары NTLMv2 (MsvAvFlags/MIC,
    /// MsvAvChannelBindings, MsvAvTargetName). Нужна, чтобы по одной строке боевого лога
    /// отличить «клиент положил MIC/CBT» от «не сошёлся хэш» без Wireshark. Содержимое
    /// ответов и хэшей в лог не попадает — только признаки наличия.
    /// </summary>
    internal static string DescribeType3(string authorization)
    {
        const string prefix = "Negotiate ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "нет";
        byte[] blob;
        try { blob = Convert.FromBase64String(authorization[prefix.Length..].Trim()); }
        catch (FormatException) { return "нет"; }

        var start = blob.AsSpan().IndexOf("NTLMSSP\0"u8);
        if (start < 0) return "без NTLMSSP";
        var msg = blob.AsSpan(start);
        if (msg.Length < 64 || BitConverter.ToUInt32(msg[8..12]) != 3) return "не Type3";

        var flags = BitConverter.ToUInt32(msg[60..64]);
        int ntLen = BitConverter.ToUInt16(msg[20..22]);
        var ntOff = (int)BitConverter.ToUInt32(msg[24..28]);
        var sb = new StringBuilder($"флаги 0x{flags:X8}");
        if (ntOff < 0 || ntOff + ntLen > msg.Length) return sb.Append(", NT-ответ вне границ").ToString();
        if (ntLen == 24) return sb.Append(", NTLMv1").ToString();
        if (ntLen < 44) return sb.Append($", NT-ответ {ntLen} байт").ToString();

        sb.Append(", NTLMv2");
        // NTProofStr(16) + заголовок blob(28), дальше AV-пары: id(2) len(2) value
        var av = msg.Slice(ntOff + 44, ntLen - 44);
        for (var p = 0; p + 4 <= av.Length;)
        {
            int id = BitConverter.ToUInt16(av[p..(p + 2)]);
            int len = BitConverter.ToUInt16(av[(p + 2)..(p + 4)]);
            p += 4;
            if (id == 0 || p + len > av.Length) break;
            var value = av.Slice(p, len);
            p += len;
            switch (id)
            {
                case 6 when len == 4:
                    var avFlags = BitConverter.ToUInt32(value);
                    sb.Append($", MsvAvFlags 0x{avFlags:X}").Append((avFlags & 0x2) != 0 ? " (MIC)" : "");
                    break;
                case 9:
                    sb.Append(", MsvAvTargetName ").Append(Encoding.Unicode.GetString(value));
                    break;
                case 10 when len == 16:
                    sb.Append(value.IndexOfAnyExcept((byte)0) < 0 ? ", CBT нулевой" : ", CBT задан");
                    break;
            }
        }
        return sb.ToString();
    }
}
