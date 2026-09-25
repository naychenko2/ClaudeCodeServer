using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace ClaudeHomeServer.Services.ProjectServices;

/// <summary>
/// Проброс превью <c>/preview/{projectId}/{**path}</c> на активный дев-сервер проекта.
/// Одна реализация на две стороны (ADR-016, задача 4.3): сервер и агент устройства
/// различаются только входом — кто и как проверил право смотреть превью; сам проброс,
/// выбор порта и семьи loopback-адресов — здесь.
/// </summary>
public static partial class DevServerPreviewForwarder
{
    [GeneratedRegex(@"^/preview/([^/]+)(/.*)?$")]
    private static partial Regex PreviewPath();

    /// <summary>Проект и хвост пути из адреса превью; false — адрес не превью.</summary>
    public static bool TryParse(string? path, out string projectId, out string restPath)
    {
        var match = PreviewPath().Match(path ?? "");
        projectId = match.Success ? match.Groups[1].Value : "";
        restPath = match.Success ? match.Groups[2].Value : "";
        return match.Success;
    }

    /// <summary>Клиент к дев-серверам: без прокси машины, редиректов, распаковки и кук.</summary>
    public static HttpMessageInvoker CreateInvoker() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    });

    /// <summary>Отдать запрос активному для превью сервису проекта; право уже проверено вызывающим.</summary>
    public static async Task ForwardAsync(HttpContext ctx, DevServerService devServer, IHttpForwarder forwarder,
        HttpMessageInvoker invoker, string projectId, string restPath)
    {
        // Порт активного для превью сервиса проекта; если ни один не запущен — 503.
        var port = devServer.GetActivePreviewPort(projectId);
        if (port is null)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("{\"error\":\"Dev-сервер не запущен\"}");
            return;
        }

        // HttpTransformer.Default сам дописывает к префиксу Path и QueryString запроса,
        // поэтому в префиксе пути быть не должно (иначе /preview/{id} уедет на дев-сервер
        // дважды и тот ответит 404). Срезаем свой префикс прямо в запросе.
        ctx.Request.Path = restPath.Length == 0 ? "/" : restPath;
        // Семью loopback-адресов выбирает LoopbackResolver, а не литерал: dev-сервер
        // на Node 17+ слушает ТОЛЬКО ::1, и прежний 127.0.0.1 до него не доставал —
        // живой сервис отдавал «соединение отвергнуто» при работающем порте.
        var previewBase = await LoopbackResolver.ResolveBaseAsync(port.Value);
        if (previewBase is null)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("{\"error\":\"Dev-сервер не отвечает\"}");
            return;
        }

        var previewError = await forwarder.SendAsync(ctx, previewBase, invoker,
            ForwarderRequestConfig.Empty, HttpTransformer.Default);
        // До назначения не достучались — процесс мог смениться на слушающий по другой
        // семье, поэтому выбор семьи забываем, а не держим до истечения TTL. Отмены
        // клиентом сюда не попадают: они ничего не говорят о живости назначения.
        if (previewError is ForwarderError.Request or ForwarderError.RequestTimedOut)
            LoopbackResolver.Invalidate(port.Value);
    }
}
