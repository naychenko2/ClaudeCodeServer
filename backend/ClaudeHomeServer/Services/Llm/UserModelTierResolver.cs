using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Резолвер per-user слотов тиров моделей. Приоритет: личный слот пользователя → глобальный
// слот инстанса (AppSettings). Неизвестный или анонимный ownerId не падает — уходит на общие слоты.
public sealed class UserModelTierResolver(UserStore users, AppSettingsService appSettings)
{
    public string? ModelFor(ModelTier tier, string? ownerId)
    {
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            var user = users.GetById(ownerId);
            if (user is not null)
            {
                var slot = tier switch
                {
                    ModelTier.Strong => user.ModelTierStrong,
                    ModelTier.Weak => user.ModelTierWeak,
                    _ => user.ModelTierMedium,
                };
                if (!string.IsNullOrWhiteSpace(slot)) return slot.Trim();
            }
        }

        return appSettings.TierModel(tier);
    }
}
