using System.Text;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Файл пар «вход + выжимка» для судьи выдумок.
///
/// Главная метрика оси B — число утверждений выжимки, которые НЕ выводятся из входа, —
/// автоматически не считается: подстрока умеет отвечать «упомянуто ли», но не «следует ли».
/// Судье (облачной модели либо человеку) нужна сама пара текстов, поэтому прогон её
/// выкладывает файлом, а не оставляет в памяти теста.
///
/// Судья ОБЛАЧНЫЙ по правилу Батареи I «прибор не должен быть объектом»: локальная модель,
/// сама будучи объектом замера, не может судить собственные выдумки. Если облачного судьи
/// нет под рукой (протухший OAuth), файл разбирается руками — и в отчёте прямо пишется,
/// что судейство не автоматизировано.
///
/// Кроме пары в файл едут две подсказки судье, обе — продукт машинной части и обе с
/// оговоркой о её слепоте:
///  • пропущенные факты (машина не нашла якорей) — судья видит ложные пропуски;
///  • отказы с пометкой «подозрение» (тема есть, отрицания рядом нет) — судья решает,
///    переворот это или оборот вне списка маркеров.
///
/// Каталог — переменная окружения LOCALBENCH_JUDGE_DIR, по умолчанию временный каталог
/// системы. В репозиторий файлы не кладутся намеренно: это сырьё прогона, а не итог, и
/// итог живёт в документе замеров.
/// </summary>
public sealed class LocalBenchJudgeDump(string place, string executor)
{
    public const string DirEnv = "LOCALBENCH_JUDGE_DIR";

    private readonly StringBuilder _body = new();
    private int _cases;

    /// <summary>Каталог, в который лягут файлы пар.</summary>
    public static string Directory()
    {
        var overridden = Environment.GetEnvironmentVariable(DirEnv);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Path.GetTempPath(), "localbench-judge")
            : overridden;
    }

    /// <summary>Добавить пару одного кейса.</summary>
    public void Add(string caseId, string input, string? summary,
        IReadOnlyList<FactOutcome> outcomes)
    {
        _cases++;
        _body.AppendLine($"## Кейс {caseId}");
        _body.AppendLine();
        _body.AppendLine("### Вход (единственный источник правды)");
        _body.AppendLine();
        _body.AppendLine("```text");
        _body.AppendLine(input.TrimEnd());
        _body.AppendLine("```");
        _body.AppendLine();
        _body.AppendLine("### Выжимка места");
        _body.AppendLine();
        _body.AppendLine("```text");
        _body.AppendLine(string.IsNullOrWhiteSpace(summary) ? "(пусто — место промолчало)" : summary.Trim());
        _body.AppendLine("```");
        _body.AppendLine();

        var missed = outcomes.Where(o => !o.Recalled).ToArray();
        _body.AppendLine("Машина считает пропущенными: "
                         + (missed.Length == 0
                             ? "ничего"
                             : string.Join("; ", missed.Select(o => $"{o.Fact.Id} — {o.Fact.What}")))
                         + ". Проверь: якорь мог не сработать на синониме.");

        var suspects = outcomes
            .Where(o => o.Negation == NegationVerdict.Suspect)
            .ToArray();
        if (suspects.Length > 0)
            _body.AppendLine("ПОДОЗРЕНИЕ на переворот отказа: "
                             + string.Join("; ", suspects.Select(o => $"{o.Fact.Id} — {o.Fact.What}"))
                             + ". Реши: отказ передан верно или превращён в решение?");
        _body.AppendLine();
        _body.AppendLine("Вердикт судьи: ");
        _body.AppendLine();
        _body.AppendLine("---");
        _body.AppendLine();
    }

    /// <summary>Записать файл. Возвращает путь — он печатается в вывод теста.</summary>
    public string Write()
    {
        var dir = Directory();
        System.IO.Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{place}.md");
        File.WriteAllText(path, Head() + _body, Encoding.UTF8);
        return path;
    }

    private string Head()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Пары «вход + выжимка» для судьи: место {place}");
        sb.AppendLine();
        sb.AppendLine($"Исполнитель выжимок: {executor}. Кейсов: {_cases}.");
        sb.AppendLine($"Снято: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}.");
        sb.AppendLine();
        sb.AppendLine("Задание судье по каждому кейсу:");
        sb.AppendLine();
        sb.AppendLine("1. **Выдумки.** Перечисли утверждения выжимки, которые НЕ следуют из входа. "
                      + "Единственный источник правды — блок «Вход»: знание о проекте со стороны "
                      + "не засчитывается, иначе судится не выжимка, а эрудиция судьи.");
        sb.AppendLine("2. **Отказы.** Если во входе есть «решили НЕ делать X» или «отвергли Y», "
                      + "проверь, не превращён ли отказ в решение. Переворот — провал кейса "
                      + "независимо от того, сколько фактов доехало.");
        sb.AppendLine("3. **Ложные пропуски.** Машина ищет факты подстрокой и промахивается на "
                      + "синонимах. Про каждый «пропущенный» факт скажи, есть ли он в выжимке на самом деле.");
        sb.AppendLine();
        sb.AppendLine("Судья обязан быть НЕ тем, кто делал выжимки: прибор не может быть объектом.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        return sb.ToString();
    }
}
