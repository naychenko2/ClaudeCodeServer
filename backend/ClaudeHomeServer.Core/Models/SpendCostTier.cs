namespace ClaudeHomeServer.Models;

// Ценовой класс расхода: единственная точка правды правила «дорого / дёшево / бесплатно».
// Класс считается НА ЧТЕНИИ из source/provider/model — схему хранилища и бэкфилл не трогаем:
// после смены правила достаточно перечитать данные.
//
// Правило (в этом порядке):
//   1. источник free → бесплатно (запись о бесплатной модели, провайдер может быть любым);
//      (кроме прямых адаптеров «*-direct»: у них source=free класс не определяет);
//   2. локальный движок (ollama, llama-server), модель «*:free» или провайдер «freellm*»
//      (локальный прокси на свою GPU) → бесплатно; «*-direct» считается по базовому ключу
//      («minimax-direct» → minimax → дёшево, «freellmapi-direct» → freellmapi → бесплатно);
//   3. провайдер Claude: «claude», «claude-<N>», «sub-*» → дорого;
//   4. провайдер НЕИЗВЕСТЕН (пустой ключ) → дорого: цена неизвестна, и занижать её нельзя —
//      потерянный ключ claude-траты не должен выглядеть «дёшево»;
//   5. любой другой облачный провайдер (minimax, glm, kimi, deepseek, alibaba…) → дёшево.
//
// Словарь «claude → дорого» живёт ТОЛЬКО здесь: дублировать его в коде нельзя.
public static class SpendCostTiers
{
    public const string Free = "free";
    public const string Cheap = "cheap";
    public const string Expensive = "expensive";

    // Провайдеры «freellm*» (в т.ч. «freellmapi» — локальный прокси на свою GPU) считаются
    // бесплатными: трата на них не стоит денег. Правило живёт в SpendSources.IsFreeProviderKey
    // и общее с признаком источника SpendSources.IsFree — класс не должен разъехаться с источником.
    // Фронт это правило как источник правды не дублирует: карточка
    // провайдера берёт класс отсюда (costTier в /api/models) и называет цену один раз.
    // Копия правила на фронте есть — она живёт ТОЛЬКО как откат на случай неизвестного класса
    // (frontend/src/lib/spendSources.ts): при расширении правила здесь откат надо продублировать,
    // иначе при недоступном /api/models обе фронтовые поверхности промолчат о бесплатности.
    private static bool IsFreeProviderKey(string provider) =>
        SpendSources.IsFreeProviderKey(provider);

    // Прямой адаптер платного провайдера («minimax-direct») — тот же провайдер: класс считается
    // по базовому ключу без суффикса, а source=free его бесплатным не делает.
    private const string DirectSuffix = "-direct";

    private static string BaseKey(string provider) =>
        provider.EndsWith(DirectSuffix, StringComparison.OrdinalIgnoreCase)
            ? provider[..^DirectSuffix.Length]
            : provider;

    // Провайдер Claude: «claude», нумерованные профили «claude-2», «claude-3»… и подписки «sub-*».
    // NormalizeProvider не трогаем: он группирует ось «провайдер», у него другая семантика.
    private static bool IsClaudeKey(string key)
    {
        if (key == "claude" || key.StartsWith("sub-", StringComparison.Ordinal)) return true;
        const string prefix = "claude-";
        return key.StartsWith(prefix, StringComparison.Ordinal)
               && key.Length > prefix.Length
               && key.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    // Общая часть правила для записи и каталога: по базовому ключу провайдера и модели.
    private static string ByKey(string provider, string? model)
    {
        var key = BaseKey(provider);
        // «local» — локальная генерация картинок/видео/звука (source=local): своя GPU, денег не стоит.
        if (key is "ollama" or "llama-server" or "local") return Free;
        if (model is not null && model.EndsWith(":free", StringComparison.OrdinalIgnoreCase)) return Free;
        if (IsFreeProviderKey(key)) return Free;
        if (IsClaudeKey(key)) return Expensive;
        // Провайдер неизвестен → цена неизвестна. «Дёшево» здесь означало бы занижение
        // расхода, поэтому неизвестный провайдер показываем как «дорого».
        if (string.IsNullOrWhiteSpace(key)) return Expensive;
        return Cheap;
    }

    public static string Of(string source, string provider, string? model)
    {
        // Локальность записи — по source=free, но не для прямого адаптера платного провайдера.
        if (source == SpendSources.Free
            && !provider.EndsWith(DirectSuffix, StringComparison.OrdinalIgnoreCase)) return Free;
        return ByKey(provider, model);
    }

    // Класс по ОДНОМУ провайдеру (без записи расхода): локальный движок → бесплатно,
    // claude (и подписки sub-*) → дорого, прочие облачные → дёшево,
    // неизвестный (пустой) ключ → дорого. Правило то же, что у Of,
    // поэтому живёт здесь же: словарь «claude → дорого» в коде не дублируется.
    // Класс по провайдеру без источника (каталог провайдеров /api/models) считается тем же
    // правилом: локальный — бесплатно, остальное — по ключу провайдера.
    //
    // Про локальный провайдер с нестандартным ключом («local-qwen»): в каталоге его бесплатность
    // известна по флажку isLocal, а в записи расхода — по источнику free. Расхождения на живых
    // данных нет: локальные записи всегда помечаются source=free (запись траты и проход
    // реклассификации SpendStore.ReclassifyLocalAsFree), поэтому Of по такой записи тоже даёт
    // free. Синхронизировать две точки по-другому нельзя: ключи локальных провайдеров
    // машин-специфичны и в Core не перечислены.
    //
    // Про «:free» (модель с бесплатным суффиксом, напр. «nvidia/nemotron:free» у openrouter):
    // в каталоге класс ПРОВАЙДЕРА, а не модели, и провайдер с такими моделями обычно отдаёт
    // и платные тоже — объявлять его целиком бесплатным значило бы занизить класс. Поэтому
    // правило «:free» живёт на уровне записи расхода (Of → free, столбик «Бесплатные» в
    // аналитике), а здесь провайдер с такими моделями остаётся «дёшево»: цена его моделей
    // известна, просто часть из них бесплатна. Это осознанное расхождение уровней, а не
    // разнобой правил: один и тот же провайдер в каталоге и в аналитике — «дёшево».
    public static string OfProvider(string provider, bool isLocal) =>
        isLocal ? Free : ByKey(provider, null);

    // Класс расхода определён только для осей «провайдер» и «модель»: класс — свойство
    // модели/провайдера, у оси «персоны»/«роли» его неоткуда взять. Одна точка правды на
    // оба API (/api/spend и разбивка расхода фичи), иначе контракт разъезжается между ними.
    public static bool AxisHasTier(string axis) => axis is "provider" or "model";

    // Класс строки, в которой смешались записи разных классов: класс с наибольшей суммой токенов;
    // при равенстве — более дорогой (занижать расход нельзя). Один процент чужих записей
    // не перекрашивает строку: «самый дорогой присутствующий» давал MiniMax «дорого»
    // из-за записей с провайдером claude. Пустая группа → null.
    public static string? Dominant(IReadOnlyDictionary<string, long> tokensByTier)
    {
        string? best = null;
        long bestTokens = 0;
        foreach (var (tier, tokens) in tokensByTier)
        {
            if (best is null || tokens > bestTokens || (tokens == bestTokens && Rank(tier) > Rank(best)))
            {
                best = tier;
                bestTokens = tokens;
            }
        }
        return best;
    }

    // Ранг только для разрешения равенства в Dominant: «дороже — важнее».
    private static int Rank(string tier) => tier switch
    {
        Expensive => 3,
        Cheap => 2,
        _ => 1,
    };
}
