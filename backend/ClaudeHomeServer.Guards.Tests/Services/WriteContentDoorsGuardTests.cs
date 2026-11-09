using System.Reflection;
using ClaudeHomeServer.Services.Access;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// S2c-8: снимок дверей WriteContent. Право записи контента проекта открывается участнику
/// ПОИМЁННО (разрез f899602f §6/§7), поэтому каждое HTTP-действие контроллера с
/// <c>[ProjectScope(Right = WriteContent)]</c> обязано быть названо в снимке строкой
/// «Контроллер.Действие». Новое действие с WriteContent вне снимка — красное: право не
/// открывается само, его открывают поимённо. Мёртвая строка снимка — красная: дверь снята
/// или переименована — строку убирают с вердиктом в дифе.
///
/// Источник правды — сборка, а не текст (образец <c>SubsystemBoundaryTests</c>): действие
/// определяется по HTTP-атрибуту глагола, действующее право — через
/// <c>ProjectScopeAttribute.For</c> (атрибут метода, иначе класса). Сборки с контроллерами
/// форс-загружены в статическом конструкторе: без этого изолированный прогон прошёл бы
/// вакуумно по незагруженным сборкам (причина — комментарий в <c>SubsystemBoundaryTests</c>);
/// нижняя граница найденных дверей не даёт сторожу пройти по пустоте.
/// </summary>
public class WriteContentDoorsGuardTests
{
    private const string AllowListPath =
        "backend/ClaudeHomeServer.Guards.Tests/Services/WriteContentDoorsAllowList.txt";

    static WriteContentDoorsGuardTests()
    {
        // Форс-загрузка сборок с контроллерами: Main (обычные референсы текут транзитно,
        // включая Plugins) и динамические модули — Main не видит их типов на компиляции
        _ = typeof(ClaudeHomeServer.Controllers.FilesController).Assembly;
        _ = typeof(ClaudeHomeServer.Notes.Controllers.NotesController).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Spend.Controllers.SpendController).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Architecture.ArchitectureController).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Plugins.Controllers.PluginBridgeController).Assembly;
        _ = typeof(ClaudeHomeServer.Services.ImageEditor.Controllers.ImageEditorEndpoints).Assembly;
        _ = typeof(ClaudeHomeServer.Services.AudioEditor.Controllers.AudioEditorEndpoints).Assembly;
        _ = typeof(ClaudeHomeServer.Services.VideoEditor.Controllers.VideoEditorEndpoints).Assembly;
    }

    private static readonly Type[] VerbAttributes =
    [
        typeof(HttpGetAttribute), typeof(HttpPostAttribute), typeof(HttpPutAttribute),
        typeof(HttpDeleteAttribute), typeof(HttpPatchAttribute), typeof(HttpHeadAttribute),
    ];

    [Fact]
    public void ДвериWriteContent_ВнеСнимка_Отсутствуют()
    {
        var doors = ScanDoors();
        var snapshot = ReadSnapshot();

        // Вакуумно-зелёный сторож хуже отсутствующего: семь дверей FilesController,
        // переведённых в S2c-8, обязаны быть видны
        doors.Count.Should().BeGreaterThan(5,
            "сторож обязан видеть переведённые двери FilesController — сборки форс-загружены");

        var outside = doors.Where(d => !snapshot.Contains(d)).ToList();
        outside.Should().BeEmpty(
            "право записи контента открывается участнику поимённо; новая дверь с WriteContent — " +
            "только с явной строкой снимка и вердиктом в дифе:\n" +
            string.Join("\n", outside.Select(d => "  • " + d)));
    }

    [Fact]
    public void Снимок_НеСодержитДверейКоторыхНетВКоде()
    {
        var doors = ScanDoors();
        var stale = ReadSnapshot().Where(d => !doors.Contains(d)).ToList();
        stale.Should().BeEmpty(
            "дверь снята с WriteContent или переименована — уберите строку снимка с вердиктом " +
            "в дифе:\n" + string.Join("\n", stale.Select(d => "  • " + d)));
    }

    // «Контроллер.Действие» каждого HTTP-действия с действующим правом WriteContent
    private static HashSet<string> ScanDoors()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name is null || !name.StartsWith("ClaudeHomeServer", StringComparison.Ordinal)) continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = [.. ex.Types.Where(t => t is not null)!]; }

            foreach (var type in types)
            {
                if (!type.IsClass || type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type)) continue;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!method.GetCustomAttributes().Any(a => VerbAttributes.Contains(a.GetType()))) continue;
                    if (ProjectScopeAttribute.For(method)?.Right != ProjectRight.WriteContent) continue;
                    result.Add(type.Name + "." + method.Name);
                }
            }
        }
        return result;
    }

    private static HashSet<string> ReadSnapshot()
    {
        var root = FindRepoRoot();
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(Path.Combine(root, AllowListPath)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            result.Add(line);
        }
        return result;
    }

    // .git бывает файлом (worktree), поэтому проверяем и каталог, и файл
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git)) &&
                Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory + " не встретил .git и backend/.");
    }
}
