namespace ClaudeHomeServer.Services.Composition.Llm;

// Реализация ITierModelResolver (Core) поверх AppSettingsService (Main).
// Тонкая обёртка: делегирует в AppSettingsService.TierModel. Создана ради
// выноса Llm в отдельный .csproj — Llm больше не имеет прямого доступа к
// AppSettingsService.
//
// Регистрируется в Composition там же, где сам AppSettingsService.
public sealed class AppSettingsTierModelAdapter(AppSettingsService appSettings) : ITierModelResolver
{
    public string? TierModel(ModelTier tier) => appSettings.TierModel(tier);
}
