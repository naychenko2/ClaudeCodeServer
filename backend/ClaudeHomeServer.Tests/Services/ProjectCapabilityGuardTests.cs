using System.Reflection;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож G1 (ADR-016 §4, план §4), часть «по отражению»: каждый вход с параметром проекта —
/// действие контроллера, метод хаба, инструмент <c>wsp</c> — обязан нести группу матрицы.
/// Default-deny: новый вход без разметки краснеет, даже если он файлов не трогает — тогда
/// его честно помечают <see cref="ProjectCapabilityArea.Platform"/>.
///
/// Сборки берутся по файлам из каталога тестов, а не списком <c>typeof</c>: новая вертикаль
/// попадает под сторож сама. Поведенческая часть — <c>ProjectCapabilityGuardHttpTests</c>.
/// </summary>
public class ProjectCapabilityGuardTests
{
    // Шаблон маршрута, несущий проект: {projectId} где угодно или api/projects/{id}
    private static readonly Regex ProjectRoute = new(
        @"\{projectId(:[^}]*)?\}|(^|/)api/projects/\{id(:[^}]*)?\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IReadOnlyList<Assembly> ProductAssemblies()
    {
        var dir = AppContext.BaseDirectory;
        return Directory.EnumerateFiles(dir, "ClaudeHomeServer*.dll")
            .Where(f => !Path.GetFileName(f).Contains(".Tests", StringComparison.Ordinal))
            .Select(f => Assembly.LoadFrom(f))
            .ToList();
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static IEnumerable<Type> Controllers() => ProductAssemblies()
        .SelectMany(SafeTypes)
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t));

    private static IEnumerable<Type> Hubs() => ProductAssemblies()
        .SelectMany(SafeTypes)
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Hub).IsAssignableFrom(t));

    // Действия контроллера с параметром проекта: маршрут или параметр метода projectId
    internal static IEnumerable<MethodInfo> ProjectActions(Type controller)
    {
        var classRoutes = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
            .Select(r => r.Template).DefaultIfEmpty("").ToList();
        foreach (var m in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.DeclaringType == typeof(ControllerBase) || m.DeclaringType == typeof(Controller)
                || m.DeclaringType == typeof(object)) continue;
            var verbs = m.GetCustomAttributes<HttpMethodAttribute>().ToList();
            if (verbs.Count == 0) continue;
            var templates = verbs.SelectMany(v => v.Template is { } t && t.StartsWith('/')
                    ? [t.TrimStart('/')]
                    : classRoutes.Select(c => c.TrimEnd('/') + "/" + (v.Template ?? "")));
            var byParam = m.GetParameters().Any(p => string.Equals(p.Name, "projectId", StringComparison.OrdinalIgnoreCase));
            if (byParam || templates.Any(t => ProjectRoute.IsMatch(t.TrimStart('/'))))
                yield return m;
        }
    }

    internal static IEnumerable<MethodInfo> ProjectHubMethods(Type hub) =>
        hub.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Any(p => p.Name == "projectId"));

    [Fact]
    public void ВходыКонтроллеров_СПроектом_РазмеченыГруппой()
    {
        var controllers = Controllers().ToList();
        // Защита от вакуумного прохода: сборки не подгрузились — сторож обязан это заметить
        controllers.Select(c => c.Name).Should().Contain(["FilesController", "GitController", "ProjectsController"]);

        var actions = controllers.SelectMany(ProjectActions).ToList();
        actions.Should().HaveCountGreaterThan(100);

        var unmarked = actions
            .Where(m => ProjectCapabilityAttribute.For(m) is null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();
        string.Join("\n", unmarked).Should().BeEmpty(
            "вход с параметром проекта обязан нести [ProjectCapability(группа)] (сторож G1, ADR-016 §4); "
            + "не трогает файлов — ProjectCapabilityArea.Platform");
    }

    [Fact]
    public void МетодыХабов_СПроектом_РазмеченыГруппой()
    {
        var methods = Hubs().SelectMany(ProjectHubMethods).ToList();
        methods.Select(m => m.Name).Should().Contain(["CreateTerminal", "JoinProject"]);

        methods.Where(m => ProjectCapabilityAttribute.For(m) is null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Aggregate("", (a, b) => a + b + "\n")
            .Should().BeEmpty("метод хаба с projectId обязан нести [ProjectCapability(группа)] (сторож G1)");
    }

    [Fact]
    public void ИнструментыWsp_СПроектом_РазмеченыГруппой()
    {
        var withProject = WorkspaceToolset.AllTools
            .Where(t => t.InputSchema["properties"]?.AsObject().ContainsKey("projectId") == true)
            .Select(t => t.Name)
            .ToList();
        withProject.Should().Contain(["files_read", "git_status", "knowledge_search"]);

        string.Join("\n", withProject.Where(t => !WorkspaceToolset.ToolCapability.ContainsKey(t)))
            .Should().BeEmpty("инструмент wsp с projectId обязан иметь группу в WorkspaceToolset.ToolCapability (сторож G1)");
    }

    [Fact]
    public void ФайловыеВходы_ИменноФайловойГруппы()
    {
        // Разметка «для галочки» (Platform на файловом контроллере) ломала бы G1 молча
        string[] fileBound = ["FilesController", "GitController", "PreviewController", "SkillsController"];
        var wrong = Controllers()
            .Where(c => fileBound.Contains(c.Name))
            .SelectMany(ProjectActions)
            .Where(m => ProjectCapabilityAttribute.For(m)?.Area is null or ProjectCapabilityArea.Platform)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();
        string.Join("\n", wrong).Should().BeEmpty();

        string[] serverContent = ["KnowledgeController", "CodeGraphController", "DocsController", "DossiersController", "ProjectMapController"];
        Controllers()
            .Where(c => serverContent.Contains(c.Name))
            .SelectMany(ProjectActions)
            .Where(m => ProjectCapabilityAttribute.For(m)?.Area != ProjectCapabilityArea.ServerContent)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Aggregate("", (a, b) => a + b + "\n")
            .Should().BeEmpty();
    }
}
