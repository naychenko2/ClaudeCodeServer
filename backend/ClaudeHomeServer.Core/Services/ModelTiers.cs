namespace ClaudeHomeServer.Services;

// Разбор уровня модели, пришедшего с провода (задача, персона): "strong|medium|weak",
// регистр не важен. Только белый список трёх имён — Enum.TryParse сюда не годится: он
// принимает индексы enum ("0" → Strong, со знаком "+0" мимо любого digit-guard) и списки
// флагов ("strong,weak" → Weak) независимо от FlagsAttribute. Мусор от LLM должен давать
// 400, а не молча уезжать на самую дорогую модель.
//
// Вынесен из `AppSettingsService.cs` в Core (Этап 5, вынос Tasks): независимая
// stateless-валидация, как `PersonaLabel`/`Slugifier`. Прежде классы вертикалей
// (Tasks/Skills) ради одного `ModelTiers.IsValidWireValue` тянули `AppSettingsService`
// целиком — God-объект с десятками методов и доступом к data/app-settings.json.
public static class ModelTiers
{
    // Текст ошибки 400 при неизвестном уровне — один на все точки входа (задачи, персоны)
    public const string WireError = "Уровень модели должен быть strong, medium или weak";

    public static bool TryParse(string? value, out ModelTier tier)
    {
        tier = default;
        switch (value?.Trim().ToLowerInvariant())
        {
            case "strong": tier = ModelTier.Strong; return true;
            case "medium": tier = ModelTier.Medium; return true;
            case "weak": tier = ModelTier.Weak; return true;
            default: return false;
        }
    }

    // Значение поля в запросе: null — не прислали, "" — сброс, иначе — обязан быть уровнем
    public static bool IsValidWireValue(string? value) =>
        value is null || value.Trim().Length == 0 || TryParse(value, out _);
}
