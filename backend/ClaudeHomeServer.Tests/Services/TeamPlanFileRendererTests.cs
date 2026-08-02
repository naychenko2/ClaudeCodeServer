using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Рендер полного плана «Командной реализации» в markdown-файл (решение владельца 2026-08-02,
// docs/architecture/team-implement-mode.md, раздел «Замысел в карточке и полный план файлом»).
public class TeamPlanFileRendererTests : IDisposable
{
    private readonly string _root;

    public TeamPlanFileRendererTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "teamplanfile_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static TeamImplementPlan MakePlan(int version = 1) => new()
    {
        Request = "Добавить экспорт в CSV",
        Summary = "Эндпоинт + кнопка",
        Version = version,
        Intent = "Идём через готовый эндпоинт экспорта и кнопку в тулбаре.\nСложную фильтрацию не делаем — только текущий вид.",
        Assumptions = ["Формат — CSV с запятой, не TSV"],
        Subtasks =
        [
            new TeamImplementSubtask
            {
                Title = "Эндпоинт экспорта", Goal = "GET /api/tasks/export отдаёт CSV",
                ExecutorPersonaId = "persona-1", ExecutorRationale = "Серверная часть — его зона",
                Files = ["backend/Controllers/TasksController.cs"], Wave = 1,
                DoneCriteria = "200 и корректный CSV",
            },
            new TeamImplementSubtask
            {
                Title = "Кнопка «Экспорт»", Goal = "Кнопка в тулбаре",
                ExecutorPersonaId = "persona-2", ExecutorRationale = "UI — фронтенд",
                Files = ["frontend/src/components/Toolbar.tsx"], Wave = 2,
            },
        ],
    };

    private static string Label(string? id) => id switch
    {
        "persona-1" => "Backend-разработчик (Денис)",
        "persona-2" => "Frontend-разработчик (Кира)",
        _ => id ?? "не назначен",
    };

    // --- Слаг чата: нормализация и защита пути ---

    [Fact]
    public void ChatSlug_ПробелыИЗнакиПрепинания_СтановятсяДефисами()
    {
        TeamPlanFileRenderer.ChatSlug("Экспорт задач: CSV!", "session-1234567890")
            .Should().Be("экспорт-задач-csv-session-");
    }

    [Fact]
    public void ChatSlug_ПопыткаТраверсала_НеСодержитРазделителейИТочек()
    {
        var slug = TeamPlanFileRenderer.ChatSlug("../../../etc/passwd", "session-abcdefgh");

        slug.Should().NotContain("..").And.NotContain("/").And.NotContain("\\");
    }

    [Fact]
    public void ChatSlug_ПустоеИмя_ПадаетНаСуффиксСессии()
    {
        TeamPlanFileRenderer.ChatSlug(null, "session-abcdefgh").Should().Be("session-");
    }

    [Fact]
    public void ChatSlug_РазныеСессииОдноИмя_РазныеСлаги()
    {
        // Коллизия имён (частый случай — дефолтное «Новый чат») не должна сталкивать версии
        // разных штабов в одну папку
        var a = TeamPlanFileRenderer.ChatSlug("Новый чат", "aaaaaaaa-1111-1111-1111-111111111111");
        var b = TeamPlanFileRenderer.ChatSlug("Новый чат", "bbbbbbbb-2222-2222-2222-222222222222");

        a.Should().NotBe(b);
    }

    [Fact]
    public void RelativePath_ВсегдаВПапкеПлановКоманды()
    {
        var rel = TeamPlanFileRenderer.RelativePath("../../evil", "session-xxxxxxxx", 1);

        rel.Should().StartWith("docs/plans/team/").And.EndWith("/plan-v1.md");
        // SafeJoin — вторая линия защиты: путь обязан резолвиться внутри корня без исключения
        var act = () => FileService.SafeJoinPublic(_root, rel);
        act.Should().NotThrow();
    }

    // --- Содержимое файла ---

    [Fact]
    public void Render_НесётЗамыселПодЗадачиИсполнителейИДопущения()
    {
        var md = TeamPlanFileRenderer.Render(MakePlan(), Label);

        md.Should().Contain("# План командной реализации v1");
        md.Should().Contain("Добавить экспорт в CSV");
        md.Should().Contain("## Замысел").And.Contain("Сложную фильтрацию не делаем");
        md.Should().Contain("### Волна 1").And.Contain("### Волна 2");
        md.Should().Contain("Эндпоинт экспорта").And.Contain("GET /api/tasks/export отдаёт CSV");
        md.Should().Contain("Backend-разработчик (Денис)").And.Contain("Серверная часть — его зона");
        md.Should().Contain("`backend/Controllers/TasksController.cs`");
        md.Should().Contain("200 и корректный CSV");
        md.Should().Contain("Кнопка «Экспорт»").And.Contain("Frontend-разработчик (Кира)");
        md.Should().Contain("## Допущения").And.Contain("Формат — CSV с запятой, не TSV");
    }

    [Fact]
    public void Render_ПустойЗамысел_БлокНеРисуется()
    {
        var plan = MakePlan();
        plan.Intent = "";

        TeamPlanFileRenderer.Render(plan, Label).Should().NotContain("## Замысел");
    }

    [Fact]
    public void Render_БезChanges_БлокНеРисуется()
    {
        TeamPlanFileRenderer.Render(MakePlan(), Label).Should().NotContain("## Что изменилось");
    }

    [Fact]
    public void Render_СChanges_БлокПрисутствует()
    {
        var plan = MakePlan(version: 2);
        plan.Changes = ["Добавлена под-задача по XLSX-формату"];

        TeamPlanFileRenderer.Render(plan, Label).Should().Contain("## Что изменилось")
            .And.Contain("Добавлена под-задача по XLSX-формату");
    }

    // --- Запись файла ---

    [Fact]
    public void TryWrite_УспешнаяЗапись_ВозвращаетОтносительныйПутьИСоздаётФайл()
    {
        var plan = MakePlan();

        var rel = TeamPlanFileRenderer.TryWrite(_root, "Экспорт в CSV", "session-abcdefgh", plan, Label);

        rel.Should().NotBeNull();
        var full = Path.Combine(_root, rel!.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(full).Should().BeTrue();
        File.ReadAllText(full).Should().Contain("Эндпоинт экспорта");
    }

    [Fact]
    public void TryWrite_ВерсияОтдельнымФайлом_ПерваяЦела()
    {
        var v1 = MakePlan(version: 1);
        var v2 = MakePlan(version: 2);
        v2.Changes = ["Добавлена валидация формата"];

        var relV1 = TeamPlanFileRenderer.TryWrite(_root, "Экспорт", "session-abcdefgh", v1, Label)!;
        var relV2 = TeamPlanFileRenderer.TryWrite(_root, "Экспорт", "session-abcdefgh", v2, Label)!;

        relV1.Should().NotBe(relV2);
        relV1.Should().EndWith("plan-v1.md");
        relV2.Should().EndWith("plan-v2.md");
        var fullV1 = Path.Combine(_root, relV1.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullV1).Should().BeTrue("перепланирование не должно перезаписывать предыдущую версию");
        File.ReadAllText(fullV1).Should().NotContain("Добавлена валидация формата");
    }

    [Fact]
    public void TryWrite_ОшибкаЗаписи_ВозвращаетNullИНеБросает()
    {
        var plan = MakePlan();
        var rel = TeamPlanFileRenderer.RelativePath("Экспорт", "session-abcdefgh", plan.Version);
        // Занимаем путь файла директорией — File.WriteAllText по нему бросит
        Directory.CreateDirectory(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));

        var act = () => TeamPlanFileRenderer.TryWrite(_root, "Экспорт", "session-abcdefgh", plan, Label);

        act.Should().NotThrow();
        act().Should().BeNull();
    }
}
