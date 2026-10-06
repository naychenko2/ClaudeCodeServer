using System.Text;
using Microsoft.AspNetCore.Connections.Features;

namespace ClaudeHomeServer.WebDav;

/// <summary>
/// Запоминает NTLM Type1 и Type2 рукопожатия на соединении. Negotiate-хендлер ASP.NET Core не
/// показывает их при отказе, а <see cref="NtlmMicProbe"/> без них не может пересчитать MIC.
/// Живёт в элементах соединения Kestrel: умирает вместе с ним, ни на диск, ни в лог не попадает.
/// Хранится по несколько штук — на HTTP/2 параллельные запросы начинают рукопожатие каждый свой.
/// </summary>
public sealed class NtlmHandshakeRecorder(RequestDelegate next)
{
    private const string ItemKey = "ccs.ntlm.handshake";
    private const int Keep = 4;

    public sealed class Handshake
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _type1s = [];
        private readonly List<byte[]> _type2s = [];
        private readonly List<string> _events = [];
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        internal void Add(byte[] msg, bool type2)
        {
            lock (_lock)
            {
                var list = type2 ? _type2s : _type1s;
                if (list.Any(m => m.AsSpan().SequenceEqual(msg))) return;
                if (list.Count == Keep) list.RemoveAt(0);
                list.Add(msg);
            }
        }

        /// <summary>
        /// Хроника запросов соединения: сколько раз пришёл Type1/Type3 и сколько Type2 ушло. Дедуп
        /// в <see cref="Add"/> прячет повторы (Type1 у Windows всегда одинаков), поэтому «записано 1»
        /// не говорит, что рукопожатие было одно.
        /// </summary>
        internal void Note(string what)
        {
            lock (_lock)
            {
                if (_events.Count == 16) _events.RemoveAt(0);
                _events.Add($"{_clock.ElapsedMilliseconds}мс {what}");
            }
        }

        internal string Timeline()
        {
            lock (_lock) return string.Join("; ", _events);
        }

        internal (IReadOnlyList<byte[]> Type1s, IReadOnlyList<byte[]> Type2s) Snapshot()
        {
            lock (_lock) return (_type1s.ToArray(), _type2s.ToArray());
        }
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        var items = ctx.Features.Get<IConnectionItemsFeature>()?.Items;
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (items is null || !auth.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
            return next(ctx);

        var handshake = (Handshake)(items[ItemKey] ??= new Handshake());
        var incoming = ExtractNtlm(auth["Negotiate ".Length..]);
        if (incoming is not null && MessageType(incoming) == 1)
            handshake.Add(incoming, type2: false);
        handshake.Note($"{ctx.Request.Method} {ctx.Request.Protocol} получен Type{(incoming is null ? "?" : MessageType(incoming))}");

        ctx.Response.OnStarting(() =>
        {
            var challenge = ctx.Response.Headers.WWWAuthenticate.ToString();
            var at = challenge.IndexOf("Negotiate ", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var token = challenge[(at + "Negotiate ".Length)..].Split(',')[0];
                if (ExtractNtlm(token) is { } t2 && MessageType(t2) == 2)
                {
                    handshake.Add(t2, type2: true);
                    handshake.Note($"отправлен Type2 (статус {ctx.Response.StatusCode})");
                    return Task.CompletedTask;
                }
            }
            handshake.Note($"ответ {ctx.Response.StatusCode} без Type2");
            return Task.CompletedTask;
        });
        return next(ctx);
    }

    /// <summary>Запись рукопожатия соединения запроса; null — записи нет (не Kestrel или не Negotiate).</summary>
    internal static Handshake? Get(HttpContext ctx) =>
        ctx.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ItemKey, out var h) == true ? h as Handshake : null;

    /// <summary>NTLM-сообщение из токена Negotiate: сырого или внутри SPNEGO (ищем сигнатуру).</summary>
    internal static byte[]? ExtractNtlm(string base64)
    {
        byte[] blob;
        try { blob = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException) { return null; }
        var at = blob.AsSpan().IndexOf("NTLMSSP\0"u8);
        return at < 0 ? null : blob[at..];
    }

    private static uint MessageType(byte[] msg) => msg.Length >= 12 ? BitConverter.ToUInt32(msg, 8) : 0;
}
