using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Приёмочный тест TypeScript/React-провайдера кодеграфа на НАСТОЯЩЕМ экстракторе:
// TypeScriptGraphProvider запускает Node-скрипт frontend/scripts/codegraph-extractor.mjs
// над frontend/src и мапит его JSON-снапшот; CodeGraphQueryService.NeighborsAsync находит
// AvatarMenu как входящую References-связь для SegmentedControl — через разрешение
// index-реэкспорта components/ui/index.ts. Требует Node в PATH, дерево frontend/ в репо
// и typescript из frontend/node_modules. Отсюда трейт "Node": backend-job CI не ставит
// npm-зависимости, и тест там исключён фильтром Category!=Node (по аналогии с Dns) —
// остаётся для локальных прогонов и Windows-хоста.
[Trait("Category", "Node")]
public class TypeScriptGraphProviderTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TypeScriptGraphProviderTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // Корень решения ищем снизу вверх от папки сборки, чтобы тест работал
    // из bin/… и на CI (платформонезависимость — см. LucideGlyphWhitelistGuardTests).
    private static string? FindRepoDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static TypeScriptGraphProvider CreateProvider() =>
        new(NullLogger<TypeScriptGraphProvider>.Instance, new ConfigurationBuilder().Build());

    [Fact]
    public async Task Frontend_AvatarMenu_References_SegmentedControl_Through_Index_Reexport()
    {
        var frontendSrc = FindRepoDir("frontend", "src");
        frontendSrc.Should().NotBeNull("frontend/src должен существовать в дереве репозитория");

        var codeGraph = _factory.Services.GetRequiredService<CodeGraphService>();
        var query = _factory.Services.GetRequiredService<CodeGraphQueryService>();

        // Настоящий провайдер: запуск Node-экстрактора и маппинг фактического контракта.
        var provider = CreateProvider();
        var raw = await provider.BuildAsync(frontendSrc!, CancellationToken.None);

        var segId = "components/ui/Segmented.tsx::SegmentedControl";
        raw.Nodes.Should().ContainKey(segId, "экстрактор строит узлы с Id «файл::имя»");
        raw.Nodes[segId].Kind.Should().Be(NodeKind.UiPrimitive,
            "Category экстрактора «ui-примитив» стыкуется в UiPrimitive");
        raw.Edges.Should().Contain(e =>
            e.Source == "features/projects/AvatarMenu.tsx::AvatarMenu" && e.Target == segId,
            "модульное ребро AvatarMenu.tsx::* разворачивается в именованные узлы файла, " +
            "а импорт из components/ui резолвится сквозь index-реэкспорт до Segmented.tsx");

        // Вид Constant: чистые данные (токены дизайн-системы) отличаются от объекта со
        // стрелочными методами (api) — решение принимает экстрактор по AST инициализатора.
        raw.Nodes.Should().ContainKey("lib/design.ts::C");
        raw.Nodes["lib/design.ts::C"].Kind.Should().Be(NodeKind.Constant,
            "C — объект из строк без функций, чистые данные");
        raw.Nodes.Should().ContainKey("lib/api.ts::api");
        raw.Nodes["lib/api.ts::api"].Kind.Should().Be(NodeKind.Util,
            "api — объект со стрелочными методами, это поведение, не константа");

        codeGraph.RegisterProvider(".ts", provider);
        codeGraph.RegisterProvider(".tsx", provider);

        // Явное перестроение графа для frontend/src.
        await codeGraph.RebuildAsync(frontendSrc!, CancellationToken.None);

        // Регрессия блокера ревью: провайдер выше зарегистрирован на .ts И .tsx — сервис
        // обязан вызвать его ОДИН раз и не задвоить рёбра на merge (10 380 вместо 5 190,
        // степень TS-узлов ×2). Снимок после RebuildAsync — по числу рёбер ровно один прогон.
        var snapshot = await codeGraph.GetSnapshotAsync(frontendSrc!, CancellationToken.None);
        snapshot.Should().NotBeNull("RebuildAsync сохранил граф в персистентность");
        snapshot!.Edges.Should().HaveCount(raw.Edges.Count,
            "один TS-провайдер на .ts/.tsx — один прогон экстрактора, рёбра не задваиваются");
        snapshot.Edges.Should().HaveCount(
            snapshot.Edges.DistinctBy(e => (e.Source, e.Target, e.Relation)).Count(),
            "merge дедупит рёбра по (Source, Target, Relation)");

        // Входящие References для SegmentedControl.
        var outcome = await query.NeighborsAsync(
            frontendSrc!, "SegmentedControl",
            direction: "in", relation: "References",
            limit: 100, CancellationToken.None);

        outcome.HasGraph.Should().BeTrue("граф для frontend/src построен");
        outcome.Result.Should().NotBeNull("узел SegmentedControl найден в графе");
        outcome.Result!.Neighbors
            .Select(n => n.Fqn)
            .Should().Contain(fqn => fqn.EndsWith("::AvatarMenu"),
                "AvatarMenu импортирует SegmentedControl из components/ui, " +
                "граф разрешает index-реэкспорт и строит References-ребро");
    }
}
