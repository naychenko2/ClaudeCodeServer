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
        logger?.LogWarning("NTLM отклонён: домен '{Domain}', пользователь '{User}', {Path}: {Error}",
            domain ?? "?", user ?? "?", http.Request.Path.Value, ctx.Exception?.GetBaseException().Message);

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
}
