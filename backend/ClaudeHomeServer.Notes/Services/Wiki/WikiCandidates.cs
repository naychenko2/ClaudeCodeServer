using System.Text;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Q1-S3 (1А): кандидаты синтеза по пересечению значимых слов (поправка ADR-039, решение 12;
// прежний порядок — «заголовок или алиас встречается в тексте окна дословно»). Чистая функция
// над снимком страниц раздела: диска не читает, ничего не пишет; второй точки правды о
// состоянии раздела нет (тот же снимок, что у каталога промпта).
// Дословное совпадение заголовка или алиаса остаётся группой первой очереди — как было
// сейчас; поверх неё — оценка: пересечение основ окна с заголовком (вес ×2), сводкой (та же
// проекция, что у строки каталога CatalogLine, Q1-S2) и алиасами. Страница с оценкой 0 — не
// кандидат. Карточки source — не кандидаты: они цель сносок, а не страницы знаний (тот же
// предикат, что у PickCandidatePages). Морфология сложнее «основа = 6 символов» — вне шага.
public static class WikiCandidates
{
    /// <summary>Top-N порядка кандидатов равен потолку промпта MaxCandidatePages (5).
    /// Прежнее значение 6 давало мёртвый хвост: PickCandidatePages отрезал шестую страницу
    /// всегда, и «TopN=6» не значило ничего. Потолок промпта остаётся внешним
    /// (MaxCandidatePages в WikiIngestService); TopN упорядочивает ровно столько, сколько
    /// дойдёт до промпта.</summary>
    public const int TopN = 5;

    /// <summary>Минимальная длина значимого токена, символов: слова короче не дают основ и
    /// не приносят очков.</summary>
    public const int MinWordLength = 4;

    /// <summary>Длина основы, потолок символов.</summary>
    public const int StemMaxChars = 6;

    // Вес пересечения с заголовком относительно сводки и алиасов: слово из заголовка —
    // сигнал предмета, то же слово из сводки — сигнал слабее. Мутация «вес ×1» краснит
    // именно тест «ЗаголовокВеситБольшеСводки» (c2 карточки).
    private const int TitleWeight = 2;

    // Стоп-список: самые частые служебные слова (русские и английские), записанные в той же
    // нормализации, что и токены (нижний регистр, ё→е). Он не полон: чего в списке нет —
    // даёт очки; список снимает лишь то, что встречается почти в любом окне и предмета не
    // называет.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // Доменные слова самой вики: они стоят в заголовках и сводках почти любой страницы
        // и предмета не называют — окно с ними дало бы очки любой странице (×2 за заголовок).
        // Формы перечислены по полному токену (стоп-слова снимаются до обрезки основы):
        // основы «стран», «разде», «сводк» в список не пишем — под них попали бы и настоящие
        // предметы («страна», «раздел книги», «сводка» как тема). «вики» — здесь: ровно
        // MinWordLength символов, AddWord его пропускает и Stem не укорачивает (4-буквенный
        // токен остаётся целиком), — мета-окно с «вики» дало бы ×2 любой странице с «вики»
        // в заголовке. «чат» в списке не нужен: токен короче MinWordLength, его отсекает
        // длина.
        "вики",
        "страница", "страницы", "странице", "страницу", "страницей", "страниц", "страницам", "страницах",
        "раздел", "раздела", "разделу", "разделом", "разделе", "разделы", "разделов", "разделам", "разделах",
        "сводка", "сводки", "сводке", "сводку", "сводкой",
        "источник", "источника", "источнику", "источником", "источнике", "источники", "источников",
        "источникам", "источниках",
        "заметка", "заметки", "заметке", "заметку", "заметкой", "заметок", "заметкам", "заметках",
        // Указательные, местоимения и их формы.
        "этого", "этой", "этом", "этим", "этих", "этот", "того", "тому", "этими", "теми",
        "такой", "такая", "такое", "такие", "такого", "таком", "таких", "таким",
        "какой", "какая", "какое", "какие", "какого", "каком", "каких", "каким",
        "который", "которая", "которое", "которые", "которого", "которой", "котором", "которых", "которым",
        "моего", "моей", "моему", "моим", "моих", "моею",
        "твоего", "твоей", "твоему", "твоим", "твоих",
        "нашего", "нашей", "нашем", "наши", "нашим", "наших",
        "вашего", "вашей", "вашем", "ваши", "вашим", "ваших",
        "своего", "своей", "своем", "свое", "свои", "своим", "своих", "свой", "своими",
        "него", "нему", "ними", "себе", "себя",
        "самого", "самой", "самом", "сами", "самим", "самых", "саму",
        "каждый", "каждая", "каждое", "каждые",
        // Связки, частицы, наречия.
        "когда", "если", "чтобы", "чтоб", "потому", "поэтому", "либо", "будто",
        "очень", "тоже", "также", "всего", "всем", "всех", "весь", "только", "просто",
        "совсем", "даже", "именно", "почти", "еще",
        // Копулятивные и модальные формы.
        "было", "были", "была", "быть", "буду", "будем", "будет", "будете", "будут", "есть",
        "стало", "стала", "стали", "стал", "может", "могла", "могли",
        "должен", "должна", "должно", "должны", "надо", "нужно", "можно", "является",
        // Предлоги и производные от них (4+ букв; короткие отсекает длина токена).
        "между", "через", "около", "после", "перед", "вокруг", "вместо", "насчет", "внутри",
        // Английские служебные слова той же роли.
        "that", "this", "with", "from", "have", "been", "were", "they", "them", "their",
        "there", "then", "than", "into", "will", "would", "could", "should", "about",
        "which", "when", "where", "while", "what", "just", "also", "some", "such", "must",
        "does", "done", "being", "your", "whom", "over", "under", "again", "once", "here",
        "because", "before", "after", "during", "without", "within", "against", "between",
        "through", "since", "until", "unless", "although", "though", "however", "therefore",
        "thus", "hence", "cannot", "might", "shall", "every", "each", "other", "same", "using",
    };

    /// <summary>Значимые слова текста: множество основ. Нормализация: NFC, нижний регистр,
    /// ё→е (тот же класс, что у резолва ByName, плюс подмена буквы). Токен — подряд букв
    /// длиной не меньше MinWordLength; всё, что не буква, — разделитель (дефис и апостроф
    /// ломают слово). Стоп-слова исключаются по полному токену, до обрезки основы.
    /// «Печёный» и «печеный» дают одну основу: «ё» не различается.</summary>
    public static HashSet<string> SignificantWords(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
            return words;
        var norm = text.Normalize(System.Text.NormalizationForm.FormC)
            .ToLowerInvariant().Replace('ё', 'е');
        var token = new StringBuilder();
        foreach (var ch in norm)
        {
            if (char.IsLetter(ch))
                token.Append(ch);
            else
            {
                AddWord(words, token);
                token.Clear();
            }
        }
        AddWord(words, token);
        return words;
    }

    /// <summary>Top-n кандидатов из снимка страниц раздела по тексту окна. Неразобранные
    /// страницы и карточки source — не кандидаты. Дословное совпадение заголовка или алиаса
    /// (та же нормализация, что у индекса ByName) — группа первой очереди, как сейчас.
    /// Далее — оценка: пересечение основ окна с заголовком (×2), сводкой (та же проекция,
    /// что у строки каталога) и алиасами; страница с оценкой 0 не берётся. При равной
    /// оценке и без дословного совпадения — порядок по pageKey (детерминирован).
    /// n ≤ 0 — пусто.</summary>
    public static List<WikiGate.Draft> Pick(IReadOnlyList<WikiGate.Draft> pages, string windowText, int n)
    {
        if (n <= 0)
            return [];
        var window = SignificantWords(windowText);
        var wanted = WikiDedup.NormalizeName(windowText);
        var scored = new List<(bool Exact, int Score, WikiGate.Draft Draft)>();
        foreach (var draft in pages)
        {
            var page = WikiPage.Parse(draft.Content).Model;
            if (page is null || page.Frontmatter.Type == WikiFrontmatter.TypeSource)
                continue;
            var names = new List<string> { WikiDedup.NormalizeName(page.Title) };
            names.AddRange(page.Frontmatter.Aliases.Select(WikiDedup.NormalizeName));
            var exact = names.Any(nm => nm.Length > 0 && wanted.Contains(nm, StringComparison.Ordinal));

            var score = 0;
            if (window.Count > 0)
            {
                score += TitleWeight * Intersection(window, page.Title);
                // Сводка — та же проекция, что у строки каталога промпта: summary шапки,
                // иначе «Сводка» страницы. Отдельного определения сводки не заводим.
                var summary = WikiProjections.SummaryOf(page.Frontmatter, draft.Content);
                if (summary is { Length: > 0 })
                    score += Intersection(window, summary);
                foreach (var alias in page.Frontmatter.Aliases)
                    score += Intersection(window, alias);
            }
            if (!exact && score == 0)
                continue;
            scored.Add((exact, score, draft));
        }
        return [.. scored
            .OrderByDescending(s => s.Exact)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Draft.Path, StringComparer.Ordinal)
            .Take(n)
            .Select(s => s.Draft)];
    }

    /// <summary>Полный нормализованный токен (нижний регистр, ё→е) — в стоп-списке: тот же
    /// признак, по которому SignificantWords снимает служебные слова. Автолинкер (Q2-A2)
    /// берёт его как фильтр: однословный заголовок из стоп-списка целью не становится.</summary>
    public static bool IsStopWord(string normalizedWord) => StopWords.Contains(normalizedWord);

    /// <summary>Основа нормализованного слова — та же Stem, что строят SignificantWords.
    /// Автолинкер (Q2-A2) сравнивает фразы по этим основам (словоформы), ссылку ставит
    /// только на точном совпадении.</summary>
    public static string StemOf(string normalizedWord) => Stem(normalizedWord);

    private static void AddWord(HashSet<string> words, StringBuilder token)
    {
        if (token.Length < MinWordLength)
            return;
        var word = token.ToString();
        if (StopWords.Contains(word))
            return;
        words.Add(Stem(word));
    }

    // Основа: первые StemMaxChars символов; для слова от 5 букв — без последней буквы
    // (флексии): «сборка» и «сборки» дают одну «сборк», «решение» и «решения» — «решен»
    // (c1 карточки). Четырёхбуквенный токен остаётся целиком: его основа не укорачивается
    // ниже самой длины слова («план» и «планы» дают «план»).
    private static string Stem(string word)
    {
        var cut = word.Length > StemMaxChars ? word[..StemMaxChars] : word;
        return cut.Length > MinWordLength ? cut[..^1] : cut;
    }

    private static int Intersection(HashSet<string> window, string text)
    {
        var count = 0;
        foreach (var stem in SignificantWords(text))
            if (window.Contains(stem))
                count++;
        return count;
    }
}
