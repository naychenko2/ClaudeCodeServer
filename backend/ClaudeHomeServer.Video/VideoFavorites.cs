using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Video;

/// <summary>
/// Избранные каналы пользователя: ключи вида <c>smotrim:1</c> (провайдер + id канала).
///
/// Ключ составной, а не голый id: у источников свои пространства id, и «1» у СМОТРИМ
/// с «1» у YouTube совпали бы. Провайдера в ключ кладёт фронт, разбирать его здесь
/// незачем — сервер только хранит и валидирует форму.
///
/// Различие «не настраивал» и «снял всё» принципиально: у первого показываем дефолт
/// (иначе новый пользователь получил бы пустую полосу вместо эфира), у второго —
/// пустую полосу с приглашением. Отсюда nullable-список в <see cref="Models.User"/>:
/// null — не трогал, пустой массив — осознанно пусто.
/// </summary>
public static partial class VideoFavorites
{
    /// <summary>
    /// Что показываем, пока человек ничего не отметил: главный федеральный, круглосуточные
    /// новости и две региональные врезки. Список короткий намеренно — это отправная точка
    /// для правки, а не попытка угадать вкус.
    /// </summary>
    public static readonly IReadOnlyList<string> Defaults =
    [
        "smotrim:1",    // Россия 1
        "smotrim:3",    // Россия 24
        "smotrim:678",  // Россия 24. Ярославль
        "smotrim:580",  // Россия 1. Крым
        "smotrim:355",  // ТНТ Music
    ];

    /// <summary>
    /// Потолок набора. Полоса каналов и её попап — это переключатель, а не второй каталог:
    /// с полусотней строк в выпадающем списке искать канал станет дольше, чем в каталоге.
    /// </summary>
    public const int MaxFavorites = 30;

    /// <summary>
    /// Форма ключа. Заведомо мусорные значения отбрасываем молча, как это делает «Стена»:
    /// гонка «канал пропал из каталога, пока полоса открыта» не должна ронять сохранение.
    /// </summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && KeyPattern().IsMatch(key);

    /// <summary>Дедуп с сохранением порядка + отбор валидных + обрезка по потолку.</summary>
    public static List<string> Normalize(IEnumerable<string?> keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var key in keys)
        {
            if (!IsValidKey(key)) continue;
            if (!seen.Add(key!)) continue;
            result.Add(key!);
            if (result.Count >= MaxFavorites) break;
        }
        return result;
    }

    [GeneratedRegex(@"^[a-z0-9-]{1,32}:[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex KeyPattern();
}
