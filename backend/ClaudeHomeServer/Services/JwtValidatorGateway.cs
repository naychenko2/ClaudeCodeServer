using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services;

// Реализация узких JWT-швов для вертикалей Modules и ProjectServices.
// Один адаптер закрывает оба контракта намеренно: разделение нужно на
// стороне потребителя (вертикали получают ровно то, что им разрешено),
// а не на стороне реализации — обе держим в одном месте поверх `JwtService`.
//
// Адаптер НЕ добавляет логики сверх того, что уже есть в `JwtService`:
// расширение контракта привело бы к росту прав у вертикали, и в зоне
// безопасности это прямой путь к утечке (секрет, версия сессий,
// выдача токенов других видов — здесь этого быть не должно).
//
// Зарегистрирован в `Program.cs` рядом с другими швами как singleton.
public sealed class JwtValidatorGateway : IUserTokenValidator, IPreviewTokenValidator
{
    private readonly JwtService _jwt;

    public JwtValidatorGateway(JwtService jwt)
    {
        _jwt = jwt;
    }

    public string? ValidateUserToken(string? token) => _jwt.ValidateUserToken(token);

    public (string UserId, string ProjectId, string ServiceId, string Jti)?
        ValidatePreviewToken(string? token) => _jwt.ValidatePreviewToken(token);
}
