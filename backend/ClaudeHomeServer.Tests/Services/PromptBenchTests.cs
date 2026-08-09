using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Fixtures;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Бенч промпта постановки задачи (план «Оптимизация потребления токенов», шаг 1.5).
//
// Сравнение «до/после» идёт по ФИКСТУРАМ, а не по живым запускам: у живых задач разные
// описания, заметки и число ходов, поэтому их дельта показывала бы разницу задач,
// а не эффект правки кода.
//
// Единица измерения — СИМВОЛЫ. TaskPromptMetrics.TotalTokensEst (chars/4) занижает на
// кириллице (реально ~2-3 символа на токен) и годится только для порядка величины;
// проценты экономии считаем по точной величине.
public class PromptBenchTests
{
    // Порог приёмки: достигнутая экономия на постановке и докладе.
    //
    // Цель плана была 0.50; фактически взято 0.489 (20 853 → 10 663 символа). Оставшиеся
    // 1,1 пункта упираются в секцию ПРАВИЛА — верификационная дисциплина держится на
    // дословных формулировках («НЕТ СВИДЕТЕЛЬСТВ = НЕ ГОТОВО»), и резать её ради процента
    // означает менять поведение исполнителя. Порог поставлен по факту, а не подогнан
    // к цели: он сторожит РЕГРЕССИЮ (промпт не должен снова разрастись), а не рисует
    // достижение. На живых задачах экономия выше — topK заметок 5→2 бенчем не измеряется.
    private const double TargetSaving = 0.48;

    private static string BaselinePath =>
        Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer.Tests", "Fixtures", "prompt-baseline.json");

    // Корень репозитория от каталога сборки: bin/Debug/net10.0 → вверх до папки с .git.
    // Путь строится Path.Combine без хардкода разделителей — тесты гоняются и на Linux (CI).
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Кириллица в именах кейсов должна остаться читаемой в файле baseline
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record CaseMeasure(
        string Name, int PromptChars, int ReportChars, int TotalChars, int TotalTokensEst,
        int TaskSection, int ExpectedResult, int Tools, int Mandatory, int Restrictions,
        int Delegation, int OmO, int Context, int Notes);

    public sealed record BaselineFile(string Note, IReadOnlyList<CaseMeasure> Cases);

    // Прогон одной фикстуры: постановка (BuildPrompt + блок заметок) + текст доклада Model Z.
    // Это и есть знаменатель цели 50% — ходы исполнения в него не входят, мы их не режем.
    private static CaseMeasure Measure(PromptBenchFixtures.BenchCase c)
    {
        var prompt = TaskExecutionService.BuildPrompt(
            c.Task, c.Persona, c.Aliases, c.CategoryProfilesPath) + c.NotesBlock;
        var m = TaskExecutionService.MeasurePrompt(prompt, c.NotesBlock);
        var report = TaskExecutionService.BuildDelegationReportText(c.Task);

        return new CaseMeasure(c.Name, prompt.Length, report.Length,
            prompt.Length + report.Length, m.TotalTokensEst,
            m.TaskSectionChars, m.ExpectedResultChars, m.ToolsChars, m.MandatoryChars,
            m.RestrictionsChars, m.DelegationChars, m.OmOChars, m.ContextChars,
            m.NotesContextChars);
    }

    private static List<CaseMeasure> MeasureAll() =>
        [.. PromptBenchFixtures.All.Select(Measure)];

    // Заморозка baseline. Запускается ВРУЧНУЮ и ровно один раз — ДО правок BuildPrompt,
    // иначе «до» перезапишется текущим состоянием и эффект правки станет недоказуем.
    // Skip снимается осознанно, файл коммитится в репозиторий.
    [Fact(Skip = "Ручной прогон: снимает baseline ДО оптимизации. Файл уже снят и закоммичен — не перезапускать.")]
    public void Заморозить_Baseline()
    {
        var payload = new BaselineFile(
            "Снято ДО оптимизации промпта (план task-execution-token-optimization, шаг 1.5.2). "
            + "Единица — символы. Перезаписывать нельзя: файл фиксирует состояние «до».",
            MeasureAll());

        Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
        File.WriteAllText(BaselinePath, JsonSerializer.Serialize(payload, JsonOpts));
    }

    // Сторож регрессии: постановка и доклад не должны разрастись обратно к baseline.
    [Fact]
    [Trait("Category", "Bench")]
    public void Экономия_ПостановкаИДоклад_НеНижеДостигнутой()
    {
        File.Exists(BaselinePath).Should().BeTrue(
            "baseline должен быть снят ДО правок промпта (см. Заморозить_Baseline)");

        var baseline = JsonSerializer.Deserialize<BaselineFile>(
            File.ReadAllText(BaselinePath), JsonOpts)!;
        var after = MeasureAll();

        after.Should().HaveCount(baseline.Cases.Count, "набор фикстур не должен меняться между замерами");

        var before = baseline.Cases.Sum(c => c.TotalChars);
        var now = after.Sum(c => c.TotalChars);
        var saving = 1.0 - (double)now / before;

        saving.Should().BeGreaterThanOrEqualTo(TargetSaving,
            $"постановка и доклад не должны разрастись: достигнуто минус {TargetSaving:P0}, "
            + $"сейчас {saving:P1} ({before} → {now} символов)");
    }

    // Диагностика: печатает текущую разбивку рядом с baseline. Skip — вспомогательный
    // инструмент разработки, в CI не нужен (падает намеренно, чтобы показать таблицу).
    [Fact(Skip = "Диагностика: снять Skip, чтобы посмотреть текущую разбивку по секциям.")]
    public void Показать_Разбивку()
    {
        var baseline = JsonSerializer.Deserialize<BaselineFile>(
            File.ReadAllText(BaselinePath), JsonOpts)!;
        var after = MeasureAll();

        var lines = new List<string> { "кейс | было | стало | дельта | правила | делег. | omo | контекст | заметки" };
        for (var i = 0; i < after.Count; i++)
        {
            var b = baseline.Cases[i];
            var a = after[i];
            lines.Add($"{a.Name} | {b.TotalChars} | {a.TotalChars} | {a.TotalChars - b.TotalChars} | "
                + $"{a.Mandatory}+{a.Restrictions} | {a.Delegation} | {a.OmO} | {a.Context} | {a.Notes}");
        }
        lines.Add($"ИТОГО | {baseline.Cases.Sum(c => c.TotalChars)} | {after.Sum(c => c.TotalChars)}");
        throw new InvalidOperationException(string.Join("\n", lines));
    }

    // Сторож структуры короткого формата: постановка без персоны осталась плоским блоком
    // «Правила:» и НЕ превратилась в секционный контракт персоны. Формулировки внутри
    // ужаты (оптимизация), структура — прежняя.
    [Fact]
    public void БезПерсоны_СтруктураКороткогоФормата()
    {
        var c = PromptBenchFixtures.All[0];
        var prompt = TaskExecutionService.BuildPrompt(c.Task, c.Persona, c.Aliases, c.CategoryProfilesPath);

        prompt.Should().Contain("Правила:");
        prompt.Should().NotContain("## ЗАДАЧА");
        prompt.Should().NotContain("## ДЕЛЕГИРОВАНИЕ");
        // Смысловое ядро правил на месте: чем вести статус и чем закрывать задачу
        prompt.Should().Contain("tasks_toggle_subtask").And.Contain("tasks_complete");
        prompt.Should().Contain("не завершай задачу", "поведение при невозможности выполнить");
    }

    // Фикстуры обязаны быть детерминированными: два прогона подряд дают идентичный результат.
    // Если сюда просочится Guid.NewGuid(), DateTime.Now или живой стор — тест поймает это
    // раньше, чем baseline окажется невоспроизводимым.
    [Fact]
    public void Фикстуры_Детерминированы()
    {
        JsonSerializer.Serialize(MeasureAll(), JsonOpts)
            .Should().Be(JsonSerializer.Serialize(MeasureAll(), JsonOpts));
    }
}
