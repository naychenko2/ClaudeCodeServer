namespace ClaudeHomeServer.Services.Composition;

// Шов проверки пользовательского/сервисного JWT вне MVC-пайплайна.
//
// Используется там, где готового ctx.User нет: gateway /api/modules/**,
// сторонняя аутентификация в middleware, любые pre-MVC сценарии. Семантика
// 1:1 с `JwtService.ValidateUserToken` — вернуть sub (userId) либо null,
// если подпись/срок недействительны или токен отозван сменой пароля.
//
// Сигнатура не отдаёт claims, версию сессий и сам токен — намеренно: всё
// это зона безопасности Main, и расширение контракта привело бы к росту прав
// у вертикали. Вертикали здесь проверяют «свой ли это пользователь» и
// перекладывают авторизацию (роль/флаги) на свои швы (IUserStore, FeatureFlagService).
//
// Адаптер в Main — тонкая обёртка над `JwtService`.
public interface IUserTokenValidator
{
    string? ValidateUserToken(string? token);
}
