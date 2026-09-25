using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.Mcp;

// Шов IHiggsfieldAccess для редактора картинок (ADR-016, раздел 1): вертикаль Images видит
// только токен, а не HiggsfieldOAuthService. AdminOwnerId наружу не выходит — траты
// редактора пишутся на того, кто нажал кнопку, а не на владельца аккаунта Higgsfield.
public sealed class HiggsfieldAccessAdapter(HiggsfieldOAuthService oauth) : IHiggsfieldAccess
{
    public string? AccessToken() => oauth.EnsureFresh();
}
