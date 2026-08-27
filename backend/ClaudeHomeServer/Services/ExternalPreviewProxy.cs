using System.Net;
using Yarp.ReverseProxy.Forwarder;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Правки запроса и ответа при форварде на дев-сервер поддомена.
/// </summary>
/// <param name="port">Порт назначения — по нему узнаём «свои» редиректы.</param>
/// <param name="publicHost">Публичное имя с портом, каким его видит браузер.</param>
/// <param name="https">Внешняя схема (для X-Forwarded-Proto).</param>
public sealed class ExternalPreviewTransformer(int port, HostString publicHost, bool https) : HttpTransformer
{
    private const string ForwardedHost = "X-Forwarded-Host";
    private const string ForwardedProto = "X-Forwarded-Proto";
    private const string ForwardedPort = "X-Forwarded-Port";

    public override async ValueTask TransformRequestAsync(HttpContext ctx, HttpRequestMessage request,
        string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(ctx, request, destinationPrefix, cancellationToken);

        // Host ОБЯЗАН стать хостом назначения, и добиться этого нужно явно: база YARP
        // копирует заголовок исходного запроса, то есть дев-сервер увидел бы публичное имя
        // поддомена. Vite (server.allowedHosts) и webpack-dev-server такой Host не пускают —
        // «Blocked request. This host is not allowed», — и выглядит это как поломка прокси,
        // хотя дело в проверке хоста у самого дев-сервера.
        //
        // null означает «подставь хост из адреса назначения» — то есть 127.0.0.1 или [::1],
        // а такие имена дев-серверы разрешают по умолчанию.
        //
        // Публичное имя уходит в X-Forwarded-*: фреймворки, которым надо построить внешний
        // адрес, берут его оттуда.
        request.Headers.Host = null;
        request.Headers.Remove(ForwardedHost);
        request.Headers.TryAddWithoutValidation(ForwardedHost, publicHost.Value);
        request.Headers.Remove(ForwardedProto);
        request.Headers.TryAddWithoutValidation(ForwardedProto, https ? "https" : "http");
        if (publicHost.Port is { } p)
        {
            request.Headers.Remove(ForwardedPort);
            request.Headers.TryAddWithoutValidation(ForwardedPort, p.ToString());
        }
    }

    public override async ValueTask<bool> TransformResponseAsync(HttpContext ctx,
        HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
    {
        var copied = await base.TransformResponseAsync(ctx, proxyResponse, cancellationToken);
        if (!copied || proxyResponse is null) return copied;

        // Дев-серверы охотно редиректят на собственный адрес (http://localhost:5586/foo).
        // Браузер по такому уедет в никуда: на машине клиента этого порта нет. Абсолютные
        // loopback-адреса СВОЕГО порта превращаем в относительные — их браузер разрешит от
        // текущего origin и останется на поддомене.
        //
        // Чужие Location не трогаем: уход на внешний сайт может быть осмысленным.
        var location = ctx.Response.Headers.Location.ToString();
        if (!string.IsNullOrEmpty(location)
            && Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && uri.Port == port
            && IsLoopbackHost(uri.Host))
        {
            ctx.Response.Headers.Location = uri.PathAndQuery + uri.Fragment;
        }

        return copied;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host is "127.0.0.1" or "::1" or "[::1]";
}

/// <summary>
/// Ответы поддомена, когда запрос не обслужен. Человеку нужна подсказка, постороннему —
/// ничего.
/// </summary>
public static class ExternalPreviewResponses
{
    public static Task WriteDenialAsync(HttpContext ctx, ExternalPreviewDenial denial)
    {
        // Выключенный рубильник, ненастроенная фича и чужой проект отвечают ОДИНАКОВО и молча:
        // любой другой ответ рассказывал бы постороннему, что по этому адресу что-то бывает.
        // Здесь же это и есть kill switch — 404 получают и уже открытые сессии.
        if (denial is ExternalPreviewDenial.Disabled
                   or ExternalPreviewDenial.NotConfigured
                   or ExternalPreviewDenial.Forbidden)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return ctx.Response.WriteAsync("Not Found");
        }

        var (code, title, hint) = denial switch
        {
            ExternalPreviewDenial.Revoked => (StatusCodes.Status401Unauthorized,
                "Ссылка больше не действует",
                "Её отозвали или истёк срок. Откройте доступ заново из панели «Сервисы»."),
            ExternalPreviewDenial.ServiceGone => (StatusCodes.Status503ServiceUnavailable,
                "Сервис не найден",
                "Он пропал из настроек проекта или лишился порта. Проверьте конфигурацию и выдайте ссылку заново."),
            ExternalPreviewDenial.NotListening => (StatusCodes.Status503ServiceUnavailable,
                "Сервер не отвечает",
                "Порт есть, но на нём никто не слушает — скорее всего, дев-сервер погашен."),
            _ => (StatusCodes.Status401Unauthorized,
                "Нужна ссылка доступа",
                "Откройте адрес по ссылке из панели «Сервисы» — прямой заход сюда не работает."),
        };

        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.WriteAsync($"""
            <!doctype html><html lang="ru"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{WebUtility.HtmlEncode(title)}</title></head>
            <body style="margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
            font:16px/1.5 system-ui,sans-serif;background:#111;color:#eee">
            <div style="max-width:32rem;padding:2rem;text-align:center">
            <h1 style="font-size:1.25rem;margin:0 0 .75rem">{WebUtility.HtmlEncode(title)}</h1>
            <p style="margin:0;color:#aaa">{WebUtility.HtmlEncode(hint)}</p>
            </div></body></html>
            """);
    }
}
