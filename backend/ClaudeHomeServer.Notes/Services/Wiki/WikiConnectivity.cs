namespace ClaudeHomeServer.Services.Notes.Wiki;

// A5 (очередь 2, волна A): чистый замер связности корпуса — та же формула, которой Вера меряла
// боевую вику после ночи 19.11 (Q1-QA4, «Связность по формуле Александра»): степень страницы =
// |mentions| + |mentionedHere| + |backlinks| из «Связанных» (WikiRelated), а source и chronicle
// — отдельным счётчиком, в степень не входят: карточка источника и хроника — служебные связи
// происхождения, а не связь темы с темой. Доля со связью — страницы со степенью ≥ 1.
//
// Степень считается несрезанной: Build вызывается с limit = null — иначе страница с 10 обратными
// ссылками дал бы 8 (потолок GroupLimit искажает среднее на живом корпусе: Вера видела
// степени 9). Срез — только вывод группы, дедупликация между группами до него: дубль остаётся
// в старшей группе и не удваивает степень.
//
// Чистая функция без I/O: вход — снимок страниц (тот же, что даёт вызывающий WikiRelated.Build)
// и subset — пути измеряемых страниц. Путь вне снимка или не разбираемый как страница
// (проекция, чужой путь) не измеряется и в Count не входит: метрика не догадывается о тексте,
// которого не видит.
public static class WikiConnectivity
{
    /// <summary>Результат замера: AvgDegree — средняя степень по измеренным, LinkedShare —
    /// доля страниц со степенью ≥ 1, Count — сколько страниц измерено (subset ∩ снимок).
    /// При Count = 0 оба числа — 0 (пустой замер, а не деление на ноль).</summary>
    public sealed record Result(double AvgDegree, double LinkedShare, int Count);

    /// <summary>Замер по subset на снимке pages: pages — полный снимок (его видит Build —
    /// связи измеряются против всего снимка, а не против subset).</summary>
    public static Result Measure(IReadOnlyList<WikiGate.Draft> pages, IReadOnlyList<string> subset)
    {
        var sum = 0;
        var linked = 0;
        var count = 0;
        foreach (var path in subset)
        {
            var page = pages.FirstOrDefault(d => d.Path == path);
            if (page == default)
                continue; // вне снимка: меры нет
            var groups = WikiRelated.Build(page, pages, limit: null);
            var degree = groups
                .Where(g => g.Kind is WikiRelated.KindMentions or WikiRelated.KindMentionedHere or WikiRelated.KindBacklinks)
                .Sum(g => g.Items.Count);
            // source и chronicle — отдельный счётчик: в сумму не входят (формула замера).
            sum += degree;
            if (degree >= 1)
                linked++;
            count++;
        }
        if (count == 0)
            return new Result(0, 0, 0);
        return new Result((double)sum / count, (double)linked / count, count);
    }
}
