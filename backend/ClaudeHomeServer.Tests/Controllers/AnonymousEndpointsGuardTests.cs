using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Сторож анонимных ручек: все эндпоинты хоста — контроллеры и минимальные API —
// собираются из EndpointDataSource и сверяются с белым списком. Анонимная — та, что несёт
// [AllowAnonymous], или та, у которой нет ни одного [Authorize] (политики по умолчанию в хосте нет,
// так что такая ручка открыта наружу так же). Новая анонимная ручка вне списка краснит тест, как и
// ручка, пропавшая из хоста при оставшейся строке: список — ровно то, что открыто.
//
// Слепая зона: сторож видит только эндпоинты маршрутизации ТЕСТОВОГО хоста. Мимо него проходит:
//  - ветки конвейера middleware — app.Use, UseWhen, app.Map и прочие обработчики, отвечающие
//    сами, без эндпоинта (WebDAV, прокси «Сервисов» /preview/**);
//  - маршруты YARP внешних модулей — они живут в конфиге прокси и в отдельном процессе;
//  - эндпоинты, которые хост заводит только в Production (или по конфигу, которого в тестовом
//    хосте нет).
// Анонимность такой ветки проверяется её собственными тестами, а не этим списком.
public class AnonymousEndpointsGuardTests
{
    // Запись — «владелец: МЕТОДЫ маршрут»; владелец — имя контроллера или «minimal» у минимальных API
    // Список снят рефлексией при заведении сторожа (F3-10, 2026-10-15) — ровно то, что было открыто
    private static readonly string[] Allowed =
    [
        // Раздача агента устройства (agent-distribution Р11): у установщика учётных данных нет
        "AgentDownloads: GET agent/install.ps1",
        "AgentDownloads: GET agent/install.sh",
        "AgentDownloads: GET agent/manifest.json",
        "AgentDownloads: GET agent/{version}/{rid}/{file}",
        "AgentDownloads: HEAD agent/install.ps1",
        "AgentDownloads: HEAD agent/install.sh",
        "AgentDownloads: HEAD agent/manifest.json",
        "AgentDownloads: HEAD agent/{version}/{rid}/{file}",
        "Auth: POST api/auth/login",
        "Devices: POST api/devices/pair",
        // ONLYOFFICE ходит со своим JWT
        "Files: GET api/projects/{projectId}/files/office-download",
        "Files: POST api/projects/{projectId}/files/office-callback",
        "Health: GET,HEAD api/health",
        // OAuth-возвраты внешних сервисов
        "HiggsfieldInstance: GET api/higgsfield/callback",
        "McpOAuth: GET api/mcp/oauth/callback",
        "Video: GET api/video/youtube/callback",
        // Канал модулей: свой host-токен вместо JWT пользователя
        "HostLlm: POST api/host/llm/complete",
        "HostLlm: POST api/host/token",
        "LlmProxyEvents: POST api/internal/llm-proxy/events",
        // Тестовый динамический модуль хоста тестов
        "Stub: GET api/stub-module/ping",
        "minimal: ANY /api/{**rest}",
        // Реверс-прокси ONLYOFFICE, draw.io, Forgejo — авторизация на своей стороне прокси
        "minimal: ANY /cache/{**catch-all}",
        "minimal: ANY /coauthoring/{**catch-all}",
        "minimal: ANY /dictionaries/{**catch-all}",
        "minimal: ANY /doc/{**catch-all}",
        "minimal: ANY /doceditor/{**catch-all}",
        "minimal: ANY /drawio/{**catch-all}",
        "minimal: ANY /fonts/{**catch-all}",
        "minimal: ANY /forgejo",
        "minimal: ANY /forgejo/{**catch-all}",
        "minimal: ANY /sdkjs-plugins/{**catch-all}",
        "minimal: ANY /sdkjs/{**catch-all}",
        "minimal: ANY /web-apps/{**catch-all}",
        // Шлюз хода локальных проектов (ADR-016): токен хода вместо JWT
        "minimal: ANY /gw/t/{turnId}/egress",
        "minimal: ANY /gw/t/{turnId}/llm/{**path}",
        "minimal: ANY /gw/t/{turnId}/mcp/{name}/{**tail}",
        // OCR рук локального проекта — тот же TurnTokenEndpointFilter, что у остальных ручек шлюза
        "minimal: POST /gw/t/{turnId}/ocr",
        "minimal: GET /.well-known/aihome-modules/jwks.json",
        // Раздача фронта (SPA)
        "minimal: GET,HEAD {*path:nonfile}",
    ];

    internal static IReadOnlyList<string> AnonymousEndpoints(IServiceProvider services)
    {
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        var anonymous = new List<string>();
        foreach (var endpoint in endpoints)
        {
            var metadata = endpoint.Metadata;
            var open = metadata.GetMetadata<IAllowAnonymous>() is not null || metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0;
            if (!open) continue;
            var methods = metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } m
                ? string.Join(",", m.Order(StringComparer.Ordinal))
                : "ANY";
            var owner = metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerName ?? "minimal";
            anonymous.Add($"{owner}: {methods} {endpoint.RoutePattern.RawText}");
        }
        return anonymous.Distinct().Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void АнонимныеРучки_РовноБелыйСписок()
    {
        using var factory = new TestWebApplicationFactory();
        var actual = AnonymousEndpoints(factory.Services);
        actual.Should().BeEquivalentTo(Allowed,
            "анонимная ручка добавляется в белый список отдельной строкой осознанно; фактический список:\n"
            + string.Join("\n", actual.Select(a => $"        \"{a}\",")));
    }
}
