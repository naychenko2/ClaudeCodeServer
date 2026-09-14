namespace ClaudeHomeServer.Services.Composition;

// Узкий шов проверки фич-флага для выноса Turn (Этап 5). Контрибьюторы системного
// промпта используют только `IsEnabled(userId, key)` — гейт секций (specialty-prompt-sections,
// change-dossiers-recall). Полный FeatureFlagService содержит GetDefinitions/GetEffective/
// Exists и тянет ModuleRegistry (внешние модули) — вертикали Turn они не нужны, а
// прямая ссылка привела бы к циклу Turn → Main → Modules.
//
// Не дубль `IModuleFeatureFlagReader` (тот обслуживает проверку видимости модуля
// вне контроллеров по тому же ключу "module-{id}"): имя привязано к модулям и
// использование для секций промпта ввело бы читателя в заблуждение. Прецедент
// разделения — IPersonaLookup vs IPersonaDirectory.
//
// Семантика 1:1 с `FeatureFlagService.IsEnabled`: true только если override или
// дефолт для ключа включён. Сигнатура НЕ отдаёт каталог/определения/запись.
public interface IFeatureFlagGate
{
    bool IsEnabled(string userId, string key);
}
