namespace ClaudeHomeServer.Services.ImageEditor;

// Шов к инстансной интеграции Higgsfield (ADR-016, раздел 1): реализация — адаптер над
// HiggsfieldOAuthService в Main, драйвер редактора видит только этот интерфейс.
// Одного метода хватает: «поставщик доступен» = AccessToken() != null, отдельный флаг
// врал бы между двумя вызовами. Токен берётся на КАЖДЫЙ запрос к Higgsfield, а не раз на
// задачу. AdminOwnerId через шов не выходит намеренно: траты пишутся на того, кто нажал.
public interface IHiggsfieldAccess
{
    // null — интеграция не подключена, отключена админом или токен не обновился
    string? AccessToken();
}
