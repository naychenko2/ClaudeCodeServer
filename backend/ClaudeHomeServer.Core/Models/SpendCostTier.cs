namespace ClaudeHomeServer.Models;

// Ценовой класс расхода: единственная точка правды правила «дорого / дёшево / бесплатно».
// Класс считается НА ЧТЕНИИ из source/provider/model — схему хранилища и бэкфилл не трогаем:
// после смены правила достаточно перечитать данные.
//
// Правило (в этом порядке):
//   1. источник free → бесплатно (запись о бесплатной модели, провайдер может быть любым);
//   2. бесплатный исполнитель (SpendSources.IsFree: локальный движок, «*:free», «*-direct»)
//      → бесплатно;
//   3. провайдер claude (включая подписки sub-*, NormalizeProvider) → дорого;
//   4. любой другой облачный провайдер (minimax, glm, kimi, deepseek, alibaba…) → дёшево.
//
// Словарь «claude → дорого» живёт ТОЛЬКО здесь: дублировать его в коде нельзя.
public static class SpendCostTiers
{
    public const string Free = "free";
    public const string Cheap = "cheap";
    public const string Expensive = "expensive";

    public static string Of(string source, string provider, string? model)
    {
        if (source == SpendSources.Free) return Free;
        if (SpendSources.IsFree(provider, model)) return Free;
        if (SpendSources.NormalizeProvider(provider) == "claude") return Expensive;
        return Cheap;
    }

    // Ранг для выбора класса узла, когда в одной группе смешались классы: «дороже — важнее».
    public static int Rank(string tier) => tier switch
    {
        Expensive => 3,
        Cheap => 2,
        _ => 1,
    };
}
