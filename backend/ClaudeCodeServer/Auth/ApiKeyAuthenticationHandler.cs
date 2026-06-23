using System.Security.Claims;
using System.Text.Encodings.Web;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ClaudeCodeServer.Auth;

/// <summary>
/// Аутентификация по единственному API-ключу. Ключ принимается из:
///   - заголовка Authorization: Bearer &lt;key&gt;
///   - заголовка X-Api-Key
///   - query ?access_token=&lt;key&gt; (для SignalR/WebSocket, где нельзя задать заголовок)
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly ApiKeyAuthService _auth;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyAuthService auth)
        : base(options, logger, encoder)
    {
        _auth = auth;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ExtractKey();
        if (key is null)
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!_auth.Validate(key))
            return Task.FromResult(AuthenticateResult.Fail("Неверный API-ключ"));

        var identity = new ClaimsIdentity(ApiKeyAuthService.SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.Name, "user"));
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), ApiKeyAuthService.SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? ExtractKey()
    {
        // Authorization: Bearer <key>
        var authHeader = Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authHeader["Bearer ".Length..].Trim();

        // X-Api-Key: <key>
        var apiKeyHeader = Request.Headers["X-Api-Key"].ToString();
        if (!string.IsNullOrWhiteSpace(apiKeyHeader))
            return apiKeyHeader.Trim();

        // ?access_token=<key> — для WebSocket/SignalR (заголовок задать нельзя)
        var token = Request.Query["access_token"].ToString();
        if (!string.IsNullOrWhiteSpace(token))
            return token.Trim();

        return null;
    }
}
