using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace ClaudeHomeServer.Controllers;

// JSON-ответ с условным GET: ETag считается по ТЕЛУ, которое реально уходит на wire
// (те же боевые MVC-опции, конвертеры включены), поэтому любое изменение любого поля,
// в том числе updatedAt, меняет тег — устаревшего ответа клиент не получит. Совпал
// If-None-Match → 304 без тела. Браузер ревалидирует сам (Cache-Control: no-cache), а
// fetch в JS видит обычный 200 с телом из своего кэша: фронтовый код не меняется.
//
// Нужен для списков, которые клиент перечитывает опросом (список чатов проекта: ~0,5 МБ
// на 600 чатов каждые несколько секунд, а меняется он редко). Сериализация на сервере
// остаётся, экономится сеть и разбор на клиенте.
public sealed class ETagJsonResult(object value) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptions<MvcJsonOptions>>().Value.JsonSerializerOptions;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), options);
        var tag = new EntityTagHeaderValue("\"" + Convert.ToHexString(SHA256.HashData(bytes))[..32] + "\"");

        var response = http.Response;
        var headers = response.GetTypedHeaders();
        headers.ETag = tag;
        // no-cache = «можно хранить, но перед использованием спроси сервер»; private — ответ
        // персональный (за токеном), общему кэшу его не отдавать
        headers.CacheControl = new CacheControlHeaderValue { NoCache = true, Private = true };

        // Слабое сравнение: сжатие ответа превращает сильный тег в слабый (W/"…"), и браузер
        // присылает его именно так
        var ifNoneMatch = http.Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Any(t => t.Compare(tag, useStrongComparison: false)))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes, http.RequestAborted);
    }
}
