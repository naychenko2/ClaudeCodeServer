namespace ClaudeHomeServer.Services.Composition;

// Шов проверки ссылки внешнего доступа к дев-серверу (preview).
//
// Семантика 1:1 с `JwtService.ValidatePreviewToken`: вернуть
// (userId, projectId, serviceId, jti) либо null, если подпись/срок/аудитория
// недействительны или токен отозван сменой пароля.
//
// Отзыв по jti (нет записи в реестре) проверяет вызывающий сам — подпись
// об этом ничего не знает, и эта проверка живёт в ProjectServices.
//
// Сигнатура не отдаёт секрет, методы выдачи токенов и валидаторы office/
// desktop-токенов — намеренно: разные вертикали получают ровно то, что
// им положено, и не больше. Tokens = security boundary.
//
// Адаптер в Main — тонкая обёртка над `JwtService`.
public interface IPreviewTokenValidator
{
    (string UserId, string ProjectId, string ServiceId, string Jti)? ValidatePreviewToken(string? token);
}
