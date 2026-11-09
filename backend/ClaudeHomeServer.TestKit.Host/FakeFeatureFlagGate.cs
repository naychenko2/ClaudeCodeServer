using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Tests.Helpers;

// Тестовая fake-реализация IFeatureFlagGate (Core). Семантика 1:1 с
// FeatureFlagService.IsEnabled: смотрит override в UserStore, иначе
// возвращает false (нейтральный дефолт для тестов). Тесты, которым нужно
// включить флаг, ставят override через `_users.SetFeatureFlag(...)` — Fake
// прочитает на следующем IsEnabled.
//
// Создан ради выноса Dossiers/Memory (Этап 5): FeatureFlagService перестал быть
// прямой зависимостью вертикалей, заменён на IFeatureFlagGate. Тесты, которые
// раньше конструировали `new FeatureFlagService(...)`, теперь оборачивают его
// в этот fake — иначе сигнатура не сойдётся.
//
// Public ради доступа из других тестовых проектов (Knowledge.Tests, Dossiers.Tests).
public sealed class FakeFeatureFlagGate(UserStore users) : IFeatureFlagGate
{
    public bool IsEnabled(string userId, string key)
    {
        // Семантика IsEnabled в FeatureFlagService: смотрит override в UserStore
        // (поле `FeatureFlags`), иначе возвращает default из каталога. Каталог
        // здесь не используем: тесты оперируют только override через
        // `_users.SetFeatureFlag(...)`; для неперекрытых флагов дефолт тестам не нужен.
        var overrides = users.GetById(userId)?.FeatureFlags;
        if (overrides != null && overrides.TryGetValue(key, out var v)) return v;
        return false;
    }
}

