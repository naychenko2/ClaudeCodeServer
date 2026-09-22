using System.Text;
using ClaudeHomeServer.Services.Docs;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож гигиены СОБСТВЕННОЙ карты репозитория (корневой CLAUDE.md): размер и мёртвые ссылки.
///
/// Разбор не свой — зовётся та же фича, что стоит за кнопкой «Убрать карту»
/// (<see cref="ProjectMapScanner"/>). Из-за этого расхождение «кнопка показывает одно, CI
/// проверяет другое» невозможно по конструкции, а сканер заодно получает постоянную
/// обкатку на живом файле.
///
/// Зачем вообще: правило «держи карту компактной» висело в шапке CLAUDE.md без всякой
/// обратной связи — и не помешало файлу дорасти до 1013 строк. Уборка вернула его к 477,
/// но карта прибавляла дважды за время самой уборки. Правило без гейта — пожелание.
///
/// Длину секций сторож НЕ проверяет намеренно: у теста слишком грубый инструмент для
/// такого суждения, это дело фичи и человека.
/// </summary>
public class ProjectMapHygieneGuardTests(ProjectMapHygieneGuardTests.RepoScan scan)
    : IClassFixture<ProjectMapHygieneGuardTests.RepoScan>
{
    // Потолок размера карты в строках.
    //
    // Откуда число: на 2026-09-22, сразу после уборки карты с 1013 до 477 строк, файл
    // занимал 482 строки — порог взят с запасом около восьми процентов, чтобы гейт не был
    // красным с первого дня и чтобы обычная правка в пару строк не роняла сборку.
    //
    // Тест покраснел? Правильное действие — вынести новое описание в docs/ по правилу из
    // шапки карты, оставив в карте выжимку со ссылкой (формат — как у секций «Видео»,
    // «Десктопный агент», «Observability»). Поднять эту константу — ПОСЛЕДНЕЕ средство,
    // а не первое: карта грузится в контекст каждой сессии целиком, и платится она каждым
    // ходом каждого чата.
    //
    // Константа, а не настройка из конфига, — осознанно: поднятие порога обязано быть
    // видимым актом в коммите, строкой в дифе, которую ревьюер увидит и спросит «а почему
    // выросла». В конфиге такая правка тихая, а тихое послабление убивает любой гейт.
    private const int MapLineBudget = 520;

    // Сколько самых длинных секций показать при падении: человеку нужен список кандидатов
    // на вынос, а не весь отчёт сканера
    private const int TopSectionsInMessage = 3;

    [Fact]
    public void КартаРепозитория_НеПревышаетПотолокСтрок()
    {
        var report = scan.Report;

        report.Exists.Should().BeTrue($"сторож обязан читать настоящую карту ({scan.Root}/CLAUDE.md)");

        if (report.Lines <= MapLineBudget) return;

        var over = report.Lines - MapLineBudget;
        // Оценка, а не факт: токенизатор у каждой модели свой (сканер считает байты ÷ 3)
        var tokensPerLine = report.Lines > 0 ? (double)report.ApproxTokens / report.Lines : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"Карта проекта CLAUDE.md выросла до {report.Lines} строк при потолке {MapLineBudget} " +
                      $"(на {over} больше).");
        sb.AppendLine($"Весит это примерно {report.ApproxTokens} токенов в КАЖДОЙ сессии " +
                      $"(из них лишних около {(int)(over * tokensPerLine)}) — карта едет в контекст целиком, " +
                      $"каждым ходом каждого чата. {MapHygieneReport.TokensNote}.");
        sb.AppendLine("Что делать: вынести новое описание в docs/ по правилу из шапки карты, оставив в карте " +
                      "выжимку со ссылкой — формат как у секций «Видео», «Десктопный агент», «Observability». " +
                      $"Поднять MapLineBudget в {nameof(ProjectMapHygieneGuardTests)} — последнее средство, а не первое.");

        if (report.Sections.Count > 0)
        {
            sb.AppendLine("Самые длинные секции сейчас (кандидаты на вынос):");
            foreach (var s in report.Sections.Take(TopSectionsInMessage))
            {
                var refs = s.DocsRefs > 0
                    ? $", внутри {s.DocsRefs} живых ссылок на docs/* — похоже на пересказ готового документа"
                    : "";
                sb.AppendLine($"  • «{s.Title}» — {Lines(s.Lines)} (с {s.StartLine}-й строки файла){refs}");
            }
        }
        else
        {
            sb.AppendLine("Длинных секций сканер не нашёл — карта растёт равномерно, " +
                          "смотреть надо по последним коммитам в CLAUDE.md.");
        }

        Assert.Fail(sb.ToString());
    }

    [Fact]
    public void КартыРепозитория_НеСодержатМёртвыхСсылокИИмпортов()
    {
        var report = scan.Report;

        report.Exists.Should().BeTrue($"сторож обязан читать настоящую карту ({scan.Root}/CLAUDE.md)");

        if (report.DeadLinkCount == 0 && report.DeadImportCount == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("Гигиена карт проекта нарушена: " +
                      $"{Plural(report.DeadLinkCount, "мёртвая ссылка", "мёртвых ссылки", "мёртвых ссылок")} и " +
                      $"{Plural(report.DeadImportCount, "не раскрывшийся @-импорт",
                          "не раскрывшихся @-импорта", "не раскрывшихся @-импортов")} " +
                      $"(проверяются корневая CLAUDE.md и вложенные карты).");
        sb.AppendLine("Ссылка в никуда обманывает каждого, кто по ней пойдёт; не раскрывшийся импорт " +
                      "означает, что правило не едет в контекст вовсе.");

        foreach (var f in report.DeadLinks) sb.AppendLine("  • " + Describe(f, "ссылка"));
        foreach (var f in report.DeadImports) sb.AppendLine("  • " + Describe(f, "импорт"));

        // Отчёт сканера урезан потолками списков — честно говорим, что показано не всё
        if (report.Truncated)
            sb.AppendLine("  … список урезан потолком отчёта, показаны не все находки.");

        Assert.Fail(sb.ToString());
    }

    private static string Lines(int n) => Plural(n, "строка", "строки", "строк");

    // Склонение при числе: сообщение читает человек, и «62 строк» в нём — мусор
    private static string Plural(int n, string one, string few, string many)
    {
        var tens = n % 100;
        var ones = n % 10;
        var word = tens is >= 11 and <= 14 ? many
            : ones == 1 ? one
            : ones is >= 2 and <= 4 ? few
            : many;
        return $"{n} {word}";
    }

    // Строка находки: где стоит и, если сканер нашёл ровно одного кандидата, чем чинить
    private static string Describe(MapLinkFinding f, string what)
    {
        var where = f.Line > 0 ? $"{f.Path}:{f.Line}" : f.Path;
        var fix = f.Candidates.Count == 1
            ? $" — одноимённый файл в проекте один: {f.Candidates[0]}"
            : "";
        return $"{where} — {what} на «{f.Target}», файла нет{fix}";
    }

    /// <summary>
    /// Один скан дерева репозитория на весь класс: обход не бесплатен, а оба теста смотрят
    /// в один и тот же отчёт.
    /// </summary>
    public sealed class RepoScan
    {
        public string Root { get; }
        public MapHygieneReport Report { get; }

        public RepoScan()
        {
            Root = FindRepoRoot()
                   // Молчаливо зелёный сторож хуже отсутствующего: не нашли карту — падаем
                   // с диагнозом, а не «пропускаем» проверку (ровно так однажды вакуумно
                   // проходил сторож границ подсистем)
                   ?? throw new InvalidOperationException(
                       "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory +
                       " не встретил каталога с .git и backend/. Сторож гигиены карты работает " +
                       "на настоящем дереве и молча пропускать проверку не имеет права.");

            // Обход дерева репозитория — замерено 58–69 мс, поэтому метки Slow у сторожа нет
            Report = new ProjectMapScanner().Scan(Root);
        }

        // Подъём от каталога сборки тестов до корня репозитория. Считать уровни вложенности
        // нельзя — у разных раннеров (локально, CI, dotnet test из контейнера) они разные.
        // .git бывает и файлом: в git worktree это файл со ссылкой на основной репозиторий,
        // а исполнители задач работают как раз в worktree
        private static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if ((Directory.Exists(git) || File.Exists(git)) &&
                    Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
