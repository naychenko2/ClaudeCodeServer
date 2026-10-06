using Microsoft.AspNetCore.Authentication.Negotiate;
using System.Security.Cryptography.X509Certificates;
using ClaudeHomeServer.WebDav;
var b = WebApplication.CreateBuilder(args);
var cert = X509CertificateLoader.LoadPkcs12FromFile("c.pfx", "x");
// STAND_LISTEN=any — слушать все интерфейсы, чтобы стенд достал живой Windows (curl.exe, Word); по умолчанию только localhost
var listenAny = Environment.GetEnvironmentVariable("STAND_LISTEN") == "any";
b.WebHost.ConfigureKestrel(k =>
{
    if (listenAny) k.ListenAnyIP(5443, o => o.UseHttps(cert));
    else k.ListenLocalhost(5443, o => o.UseHttps(cert));
});
b.Configuration["WebDav:NtlmUserFile"] = Path.GetFullPath("users.txt");
b.Services.AddSingleton<NtlmUserFile>();
b.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate(o => o.Events = new NegotiateEvents { OnAuthenticationFailed = NegotiateFailure.HandleAsync });
b.Services.AddAuthorization(o => o.FallbackPolicy = o.DefaultPolicy);
var app = b.Build();
// STAND_DUMP=1 — печатать Type1 и Type3 каждого запроса целиком. Только для тестовой учётки стенда (пароль известен):
// по полному Type3 пробу можно прогнать офлайн, не гоняя клиента заново. На боевом сервере этого нет.
if (Environment.GetEnvironmentVariable("STAND_DUMP") == "1")
    app.Use(async (ctx, next) =>
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase) && ExtractNtlm(auth[10..]) is { } m)
            Console.Error.WriteLine($"DUMP {ctx.Connection.Id} {ctx.Request.Method} клиент→сервер Type{BitConverter.ToUInt32(m, 8)}: {Convert.ToHexString(m)}");
        ctx.Response.OnStarting(() =>
        {
            var ch = ctx.Response.Headers.WWWAuthenticate.ToString();
            var at = ch.IndexOf("Negotiate ", StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && ExtractNtlm(ch[(at + 10)..].Split(',')[0]) is { } t2)
                Console.Error.WriteLine($"DUMP {ctx.Connection.Id} сервер→клиент Type{BitConverter.ToUInt32(t2, 8)}: {Convert.ToHexString(t2)}");
            return Task.CompletedTask;
        });
        await next();
    });
app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/", (HttpContext c) => "hello " + c.User.Identity?.Name);
app.Run();

// NTLM-сообщение из токена Negotiate: сырого или внутри SPNEGO (ищем сигнатуру)
static byte[]? ExtractNtlm(string base64)
{
    byte[] blob;
    try { blob = Convert.FromBase64String(base64.Trim()); }
    catch (FormatException) { return null; }
    var at = blob.AsSpan().IndexOf("NTLMSSP\0"u8);
    return at < 0 ? null : blob[at..];
}
