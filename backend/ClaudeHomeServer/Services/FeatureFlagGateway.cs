using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services;

// Реализация узкого шва проверки флагов для Modules.
//
// Адаптер НЕ добавляет логики сверх того, что уже есть в `FeatureFlagService`:
// расширение контракта привело бы к росту прав у вертикали, и Modules получила
// бы доступ к каталогу определений и записи флагов — этого в Core не должно
// быть по построению.
//
// Зарегистрирован в `Program.cs` рядом с другими швами как singleton.
public sealed class FeatureFlagGateway : IModuleFeatureFlagReader
{
    private readonly FeatureFlagService _flags;

    public FeatureFlagGateway(FeatureFlagService flags)
    {
        _flags = flags;
    }

    public bool IsEnabled(string userId, string key) => _flags.IsEnabled(userId, key);
}
