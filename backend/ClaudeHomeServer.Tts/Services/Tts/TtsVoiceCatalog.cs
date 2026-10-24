namespace ClaudeHomeServer.Services.Tts;

// Белый список голосов синтеза (SpeechKit v3) — по нему проверяется имя из конфига.
//
// Состав проверен живыми запросами 20.08.2026: каждое имя отвечает 200 на v3. Восемь
// голосов достались от v1 (alena, jane, omazh, marina, filipp, zahar, ermil, madirus),
// остальные живут только в v3. `alena` в публичном списке голосов документации не
// значится, но API её принимает — держим, не закладываясь на вечность (повторно проверено
// 24.10.2026: 200 с аудио и без роли, и с good). Тогда же добавлены saule_ru, zamira_ru,
// zhanar_ru и yulduz_ru — только v3, каждая роль проверена живым запросом.
//
// Подписи и пол голоса живут ЗДЕСЬ, а не на фронте: разъехавшись с белым списком, они
// дали бы в форме выбор голоса, которого сервер не примет.
//
// Модель синтеза — тоже поле каталога, а не персоны: имя голоса однозначно задаёт модель
// (голос LiveTTS без model даёт 403, голос general с model=livetts — 400, имена не
// пересекаются), и второй источник правды рядом с голосом только разошёлся бы с первым.
// Голоса LiveTTS (sofia, vera, irina, denis, sergey, vasily) замерены живыми запросами
// 24.10.2026: каждый отвечает 200 без роли и с каждым амплуа из карты ниже, MP3 проходит.
public static class TtsVoiceCatalog
{
    // Голос, на который падаем при незнакомом имени в конфиге: есть и в v1, и в v3
    public const string Default = "zahar";

    // Модели синтеза SpeechKit v3. Строки, а не enum: новый вложенный тип потребовал бы
    // новой строки allow-list границ, а в формат хранения модель не попадает вовсе
    public const string General = "general";
    public const string LiveTts = "livetts";

    // Пол — для группировки списка в форме: имена вроде madi_ru человеку ничего не говорят
    public enum Gender { Female, Male }

    // Голос для показа человеку. Voice — КАНОНИЧЕСКОЕ имя (алиасы наружу не выходят,
    // иначе один голос дал бы в списке две строки)
    public record VoiceInfo(string Voice, string Label, Gender Gender, IReadOnlyList<string> Roles,
        string Model = General);

    // madirus и madi_ru — одно и то же имя, v3 принимает оба написания
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "alena", "jane", "omazh", "marina", "filipp", "zahar", "ermil", "madirus", "madi_ru",
        "dasha", "julia", "lera", "masha", "alexander", "kirill", "anton",
        "saule_ru", "zamira_ru", "zhanar_ru", "yulduz_ru",
        "sofia", "vera", "irina", "denis", "sergey", "vasily",
    };

    // Амплуа голоса: SpeechKit отвечает 400 на роль, которую голос не тянет, поэтому
    // проверяем заранее. Состав замерен прямыми запросами 20.08.2026 — у filipp и madi_ru
    // ролей нет вовсе, у остальных свой набор. Роль — СТРОКА, а не enum: старый бинарь
    // споткнулся бы на десериализации нового enum'а в personas.json, и пришлось бы
    // поднимать версию схемы бэкапа (BackupSchema).
    private static readonly Dictionary<string, string[]> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alena"] = ["good"],
        ["jane"] = ["good", "evil"],
        ["omazh"] = ["evil"],
        ["marina"] = ["friendly", "whisper"],
        ["zahar"] = ["good"],
        ["ermil"] = ["good"],
        ["dasha"] = ["good", "friendly"],
        ["julia"] = ["strict"],
        ["lera"] = ["friendly"],
        ["masha"] = ["good", "strict", "friendly"],
        ["alexander"] = ["good"],
        ["kirill"] = ["good", "strict"],
        ["anton"] = ["good"],
        // neutral у всех — это умолчание без роли, в карту его не пишем
        ["saule_ru"] = ["strict", "whisper"],
        ["zamira_ru"] = ["strict", "friendly"],
        ["zhanar_ru"] = ["strict", "friendly"],
        ["yulduz_ru"] = ["strict", "friendly", "whisper"],
        // filipp, madirus и madi_ru ролей не поддерживают — их здесь нет намеренно
        //
        // LiveTTS (24.10.2026). neutral не пишем ни у кого: у sofia, vera, sergey и vasily
        // явный neutral даёт 400, у denis и irina он неотличим от «без роли»
        ["sofia"] = ["casual", "support"],
        ["vera"] = ["casual", "support"],
        ["irina"] = ["formal", "support", "narrator", "sales"],
        ["denis"] = ["casual", "formal", "sales"],
        ["sergey"] = ["support", "sales"],
        ["vasily"] = ["support", "sales"],
    };

    // Подписи описывают голос НА СЛУХ, а не пересказывают имя: выбрать голос глазами
    // нельзя, и «masha» человеку не говорит ничего. Порядок — как показывать в форме.
    //
    // Голоса LiveTTS — В КОНЦЕ своей группы, никогда не первыми: от порядка зависит «первый
    // голос» в редакторе звука и подписи формы, и дорогая модель не должна стать умолчанием
    private static readonly VoiceInfo[] Catalog =
    [
        new("alena", "Алёна · спокойная, ровная", Gender.Female, RolesFor("alena")),
        new("marina", "Марина · мягкая, может шептать", Gender.Female, RolesFor("marina")),
        new("jane", "Джейн · выразительная", Gender.Female, RolesFor("jane")),
        new("omazh", "Омаж · низкая, с холодком", Gender.Female, RolesFor("omazh")),
        new("dasha", "Даша · молодая, дружелюбная", Gender.Female, RolesFor("dasha")),
        new("julia", "Юлия · сдержанная, строгая", Gender.Female, RolesFor("julia")),
        new("lera", "Лера · лёгкая, приветливая", Gender.Female, RolesFor("lera")),
        new("masha", "Маша · живая, с широким диапазоном", Gender.Female, RolesFor("masha")),
        new("saule_ru", "Сауле · ровная, может шептать", Gender.Female, RolesFor("saule_ru")),
        new("zamira_ru", "Замира · ровная, дружелюбная", Gender.Female, RolesFor("zamira_ru")),
        new("zhanar_ru", "Жанар · сдержанная, приветливая", Gender.Female, RolesFor("zhanar_ru")),
        new("yulduz_ru", "Юлдуз · гибкая, может шептать", Gender.Female, RolesFor("yulduz_ru")),
        new("sofia", "София · живая, разговорная", Gender.Female, RolesFor("sofia"), LiveTts),
        new("vera", "Вера · живая, участливая", Gender.Female, RolesFor("vera"), LiveTts),
        new("irina", "Ирина · живая, дикторская", Gender.Female, RolesFor("irina"), LiveTts),
        new("zahar", "Захар · глубокий, уверенный", Gender.Male, RolesFor("zahar")),
        new("filipp", "Филипп · нейтральный, деловой", Gender.Male, RolesFor("filipp")),
        new("ermil", "Ермил · тёплый, неторопливый", Gender.Male, RolesFor("ermil")),
        new("madi_ru", "Мади · ровный, чуть отстранённый", Gender.Male, RolesFor("madi_ru")),
        new("alexander", "Александр · собранный", Gender.Male, RolesFor("alexander")),
        new("kirill", "Кирилл · чёткий, может быть строгим", Gender.Male, RolesFor("kirill")),
        new("anton", "Антон · молодой, лёгкий", Gender.Male, RolesFor("anton")),
        new("denis", "Денис · живой, разговорный", Gender.Male, RolesFor("denis"), LiveTts),
        new("sergey", "Сергей · живой, поддерживающий", Gender.Male, RolesFor("sergey"), LiveTts),
        new("vasily", "Василий · живой, убеждающий", Gender.Male, RolesFor("vasily"), LiveTts),
    ];

    // Полный каталог: 25 различимых голосов, а не 26 записей белого списка — madirus и
    // madi_ru это одно и то же, и в списке им место одно
    public static IReadOnlyList<VoiceInfo> All => Catalog;

    // Модель голоса; незнакомое имя — general (в запрос оно всё равно не попадёт)
    public static string ModelFor(string? voice)
    {
        var name = Canonical(voice);
        return name is null ? General : Catalog.First(v => v.Voice == name).Model;
    }

    // Темп настраивается только у general: LiveTTS на speed отвечает 400
    public static bool SupportsSpeed(string? voice) => ModelFor(voice) == General;

    // Сам белый список — для теста «у каждого имени есть подпись»: перебор копии списка
    // пропустил бы голос, добавленный сюда без строки в Catalog
    internal static IReadOnlyCollection<string> KnownNames => Known;

    public static bool IsKnown(string? voice) =>
        !string.IsNullOrWhiteSpace(voice) && Known.Contains(voice.Trim());

    // Роли голоса; пусто — голос говорит только нейтрально. Отсутствие в карте означает
    // именно «ролей нет», а не «голос неизвестен» (проверка имени — отдельно, IsKnown)
    public static IReadOnlyList<string> RolesFor(string? voice) =>
        !string.IsNullOrWhiteSpace(voice) && Roles.TryGetValue(voice.Trim(), out var roles)
            ? roles
            : [];

    // Каноническое имя голоса: madirus и madi_ru — один голос, но в каталоге он под вторым
    // именем. Без нормализации персона, которой голос ставили алиасом, открылась бы в форме
    // как «голос не выбран» — список ищет совпадение по каноническому ключу
    public static string? Canonical(string? voice)
    {
        if (!IsKnown(voice)) return null;
        var name = voice!.Trim();
        return Catalog.FirstOrDefault(v => v.Voice.Equals(name, StringComparison.OrdinalIgnoreCase))?.Voice
               ?? (name.Equals("madirus", StringComparison.OrdinalIgnoreCase) ? "madi_ru" : name);
    }

    public static bool SupportsRole(string? voice, string? role) =>
        !string.IsNullOrWhiteSpace(role)
        && RolesFor(voice).Contains(role.Trim(), StringComparer.OrdinalIgnoreCase);

    // Незнакомый голос не повод глушить озвучку: опечатка в конфиге стоила бы всей речи
    // (фронт запомнил бы отказ и ушёл на голос браузера до конца сессии)
    public static string Resolve(string? voice) => IsKnown(voice) ? voice!.Trim() : Default;
}
