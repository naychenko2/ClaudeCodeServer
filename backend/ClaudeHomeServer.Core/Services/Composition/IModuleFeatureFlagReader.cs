namespace ClaudeHomeServer.Services.Composition;

// Шов проверки флагов видимости модуля вне контроллеров.
//
// Используется в host/gateway-middleware (Modules) на границе процессов: модуль
// скрыт per-user, поэтому прежде чем отдавать ему host-канал или пропускать
// вызов через /api/modules/{id}/**, нужно спросить effective-значение его
// флага. Семантика 1:1 с `FeatureFlagService.IsEnabled(userId, key)`:
// вернуть true только если override или default для ключа включён.
//
// Сигнатура НЕ отдаёт каталог, определения, методы записи и `GetEffective`:
// вертикаль здесь проверяет «виден ли модуль этому пользователю» и больше
// ничего. Расширение контракта привело бы к росту прав у Modules и прямой
// зависимости от внутренней модели флагов.
//
// Адаптер в Main — тонкая обёртка над `FeatureFlagService`.
public interface IModuleFeatureFlagReader
{
    bool IsEnabled(string userId, string key);
}
