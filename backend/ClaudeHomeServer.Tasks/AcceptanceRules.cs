using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Tasks;

// Чистые правила критериев приёмки и свидетельств (образец — DefectRules): без хранилища и HTTP.
// Единственное место проверки «каждый критерий закрыт свидетельством»: вызывает только
// TaskManager на переходе в Done, тулсет и контроллер текст отказа получают из исключения.
public static class AcceptanceRules
{
    // Приводит список критериев к хранимому виду: обрезает текст, пустой текст — отказ,
    // пустой Id получает «c{n}» (первый свободный), повтор Id — отказ.
    public static List<AcceptanceCriterion> Normalize(IEnumerable<AcceptanceCriterion>? criteria)
    {
        var result = new List<AcceptanceCriterion>();
        if (criteria is null) return result;
        var list = criteria.ToList();
        var taken = list.Select(c => c.Id?.Trim()).Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.Ordinal);
        var next = 1;
        foreach (var c in list)
        {
            var text = c.Text?.Trim() ?? "";
            if (text.Length == 0)
                throw new InvalidOperationException("Критерий приёмки без текста: заполните text.");
            var id = c.Id?.Trim() ?? "";
            if (id.Length == 0)
            {
                while (taken.Contains($"c{next}")) next++;
                id = $"c{next}";
                taken.Add(id);
            }
            if (result.Any(r => r.Id == id))
                throw new InvalidOperationException($"Повтор id критерия приёмки: «{id}».");
            result.Add(new AcceptanceCriterion(id, text));
        }
        return result;
    }

    // Добавляет свидетельства к существующим: по одному на критерий, повторное свидетельство
    // по тому же критерию заменяет прежнее. Ссылка на неизвестный критерий и пустые how/result — отказ.
    public static List<AcceptanceEvidence> MergeEvidence(
        IReadOnlyList<AcceptanceEvidence> existing,
        IEnumerable<AcceptanceEvidence> incoming,
        IReadOnlyList<AcceptanceCriterion> criteria)
    {
        var merged = existing.ToList();
        foreach (var e in incoming)
        {
            var criterionId = e.CriterionId?.Trim() ?? "";
            if (criteria.All(c => c.Id != criterionId))
                throw new InvalidOperationException(
                    $"Свидетельство ссылается на неизвестный критерий «{criterionId}».");
            if (string.IsNullOrWhiteSpace(e.How) || string.IsNullOrWhiteSpace(e.Result))
                throw new InvalidOperationException(
                    $"Свидетельство по критерию «{criterionId}» без how/result: укажите, чем проверено и что получилось.");
            merged.RemoveAll(x => x.CriterionId == criterionId);
            merged.Add(new AcceptanceEvidence(criterionId, e.How.Trim(), e.Result.Trim()));
        }
        return merged;
    }

    // Смена текста критерия при том же Id отбрасывает его свидетельство (fail-closed, как удаление
    // критерия): старое свидетельство не может закрыть новый смысл. Осиротевшие здесь не трогаем.
    public static List<AcceptanceEvidence> DropStaleEvidence(
        IReadOnlyList<AcceptanceEvidence> evidence,
        IReadOnlyList<AcceptanceCriterion> before,
        IReadOnlyList<AcceptanceCriterion> after) =>
        evidence.Where(e =>
        {
            var was = before.FirstOrDefault(c => c.Id == e.CriterionId);
            var now = after.FirstOrDefault(c => c.Id == e.CriterionId);
            return was is null || now is null || was.Text == now.Text;
        }).ToList();

    // Гейт закрытия: у задачи с непустым Acceptance на каждый критерий нужно свидетельство.
    // Без Acceptance — no-op (старые задачи закрываются как раньше).
    public static void EnsureClosable(
        IReadOnlyList<AcceptanceCriterion> criteria, IReadOnlyList<AcceptanceEvidence> evidence)
    {
        if (criteria.Count == 0) return;
        var open = criteria
            .Where(c => !evidence.Any(e => e.CriterionId == c.Id
                && !string.IsNullOrWhiteSpace(e.How) && !string.IsNullOrWhiteSpace(e.Result)))
            .ToList();
        if (open.Count == 0) return;
        var list = string.Join("; ", open.Select(c => $"{c.Id}: {c.Text}"));
        throw new InvalidOperationException(
            $"Задачу нельзя закрывать: нет свидетельств по критериям приёмки ({open.Count} из {criteria.Count}) — " +
            $"{list}. Передайте evidence: [{{criterionId, how, result}}] по каждому.");
    }

    // Исполнитель не меняет критерии: совпадающий список допустим (клиенты шлют поля целиком).
    public static bool SameCriteria(IReadOnlyList<AcceptanceCriterion> a, IReadOnlyList<AcceptanceCriterion> b) =>
        a.SequenceEqual(b);
}
