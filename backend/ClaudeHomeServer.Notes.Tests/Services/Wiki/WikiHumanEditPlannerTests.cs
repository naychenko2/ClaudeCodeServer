using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// B3a (ADR-038, решения 1–3): разбор правки человека «было → стало» в операции пакета human.
// Чистая функция: тесты — таблица кейсов на текстах страниц.
public class WikiHumanEditPlannerTests
{
    private const string Path = "Общее/Тема.md";

    // Страница из секций: ссылки [^sN] в строках получают определения автоматически.
    private static string Page(
        string[]? svodka = null, string[]? hronologia = null, string[]? protivorechiya = null,
        string[]? istoriya = null, string[]? notes = null, string? fm = null, string title = "Тема")
    {
        var sections = new (string Name, string[]? Lines)[]
        {
            ("Сводка", svodka), ("Хронология", hronologia), ("Противоречия", protivorechiya), ("История", istoriya),
        };
        var sb = new StringBuilder();
        sb.Append("---\ntype: topic\n").Append(fm ?? "summary: Кратко\naliases: []\nowns: []\nsources: [Карточка1]\nstatus: active\n").Append("---\n\n");
        sb.Append("# ").Append(title).Append('\n');
        var maxRef = 0;
        foreach (var (name, lines) in sections)
        {
            sb.Append("\n## ").Append(name).Append('\n');
            foreach (var l in lines ?? [])
            {
                sb.Append(l).Append('\n');
                foreach (Match m in Regex.Matches(l, @"\[\^s(\d+)\]"))
                    maxRef = Math.Max(maxRef, int.Parse(m.Groups[1].Value));
            }
        }
        if (notes is not null)
        {
            sb.Append("\n## Заметки\n");
            foreach (var l in notes)
                sb.Append(l).Append('\n');
        }
        if (maxRef > 0)
            sb.Append('\n');
        for (var k = 1; k <= maxRef; k++)
            sb.Append($"[^s{k}]: [[Карточка{k}]] — «цитата номер {k}»\n");
        return sb.ToString();
    }

    private static WikiHumanEditPlanner.Plan Plan(string was, string now) =>
        WikiHumanEditPlanner.Build(Path, was, now);

    private static string Kinds(WikiHumanEditPlanner.Plan p) =>
        string.Join(",", p.Operations.Select(o => o.Kind));

    private static readonly string[] Base =
    [
        "Сервер работает на порту 5000 [^s1]",
        "Бэкенд написан на ASP.NET Core [^s2]",
    ];

    [Fact]
    public void Без_изменений_операций_нет()
    {
        var plan = Plan(Page(Base), Page(Base));
        plan.Ok.Should().BeTrue();
        plan.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Правка_формулировки_даёт_edit_только_изменённого()
    {
        var now = Page(["Сервер работает на порту 5001 [^s1]", Base[1]]);
        var plan = Plan(Page(Base), now);

        plan.Ok.Should().BeTrue();
        plan.Operations.Should().ContainSingle();
        var op = plan.Operations[0];
        op.Kind.Should().Be(WikiHumanEditPlanner.KindEdit);
        op.Section.Should().Be("Сводка");
        op.Target.Should().Be("Сервер работает на порту 5000");
        op.Text.Should().Be("Сервер работает на порту 5001");
    }

    [Fact]
    public void Неизменённое_утверждение_ИИ_не_попадает_в_операции()
    {
        // Человек правит одну строку из двух: вторая (ИИ) в операциях не встречается нигде.
        var plan = Plan(Page(Base), Page(["Сервер работает на порту 5001 [^s1]", Base[1]]));

        plan.Operations.Should().NotContain(o => o.Text != null && o.Text.Contains("ASP.NET"));
        plan.Operations.Should().NotContain(o => o.Target != null && o.Target.Contains("ASP.NET"));
    }

    [Fact]
    public void Добавление_даёт_append_в_секцию()
    {
        var plan = Plan(Page(Base), Page([.. Base, "Деплой делает Марк"]));

        plan.Ok.Should().BeTrue();
        var op = plan.Operations.Should().ContainSingle().Subject;
        op.Kind.Should().Be(WikiHumanEditPlanner.KindAppend);
        op.Section.Should().Be("Сводка");
        op.Text.Should().Be("Деплой делает Марк");
        op.Target.Should().BeNull();
    }

    [Fact]
    public void Добавление_в_другую_секцию_несёт_имя_секции()
    {
        var plan = Plan(Page(Base), Page(Base, hronologia: ["2026-11-02 запуск"]));

        var op = plan.Operations.Should().ContainSingle().Subject;
        op.Kind.Should().Be(WikiHumanEditPlanner.KindAppend);
        op.Section.Should().Be("Хронология");
    }

    [Fact]
    public void Удаление_даёт_supersede_снято_человеком_а_не_стирание()
    {
        var plan = Plan(Page(Base), Page([Base[0]]));

        plan.Ok.Should().BeTrue();
        var op = plan.Operations.Should().ContainSingle().Subject;
        op.Kind.Should().Be(WikiHumanEditPlanner.KindSupersede);
        op.Target.Should().Be("Бэкенд написан на ASP.NET Core");
        op.Reason.Should().Be("снято человеком");
        op.Text.Should().BeNull();
    }

    [Fact]
    public void Перестановка_строк_не_правка()
    {
        var plan = Plan(Page(Base), Page([Base[1], Base[0]]));

        plan.Ok.Should().BeTrue();
        plan.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Смена_только_номера_сноски_не_правка()
    {
        // Человек сносок не набирает: ссылки в строке для сравнения не участвуют.
        var plan = Plan(Page(Base), Page(["Сервер работает на порту 5000", Base[1]]));

        plan.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Только_Заметки_даёт_одну_операцию_notes_без_цитат()
    {
        var plan = Plan(Page(Base), Page(Base, notes: ["Мои мысли", "| a | b |"]));

        plan.Ok.Should().BeTrue();
        var op = plan.Operations.Should().ContainSingle().Subject;
        op.Kind.Should().Be(WikiHumanEditPlanner.KindNotes);
        op.Section.Should().Be("Заметки");
        op.Text.Should().Be("Мои мысли\n| a | b |");
        op.Target.Should().BeNull();
    }

    [Fact]
    public void Правка_Заметок_не_трогает_утверждения()
    {
        var plan = Plan(Page(Base, notes: ["было"]), Page(Base, notes: ["стало"]));

        Kinds(plan).Should().Be("notes");
        plan.Operations[0].Text.Should().Be("стало");
    }

    [Fact]
    public void Удаление_секции_Заметки_даёт_notes_с_пустым_текстом()
    {
        var plan = Plan(Page(Base, notes: ["что-то"]), Page(Base));

        var op = plan.Operations.Should().ContainSingle().Subject;
        op.Kind.Should().Be(WikiHumanEditPlanner.KindNotes);
        op.Text.Should().BeEmpty();
    }

    [Theory]
    [InlineData("sources: [Другая]")]
    [InlineData("status: disputed")]
    [InlineData("updated: 2026-11-02")]
    [InlineData("type: concept")]
    public void Правка_кодовых_полей_шапки_отказ(string changed)
    {
        var field = changed.Split(':')[0];
        var fm = "summary: Кратко\naliases: []\nowns: []\nsources: [Карточка1]\nstatus: active\n";
        var now = field switch
        {
            "sources" => fm.Replace("sources: [Карточка1]", changed),
            "status" => fm.Replace("status: active", changed),
            "updated" => fm + changed + "\n",
            _ => fm,
        };
        var nowPage = Page(Base, fm: now);
        if (field == "type")
            nowPage = nowPage.Replace("type: topic", changed);

        var plan = Plan(Page(Base), nowPage);

        plan.Ok.Should().BeFalse();
        plan.Reason.Should().Contain(field);
        plan.Operations.Should().BeEmpty();
    }

    [Fact]
    public void Правка_полей_модели_шапки_даёт_header()
    {
        var fm = "summary: Новое описание\naliases: [А, Б]\nowns: []\nsources: [Карточка1]\nstatus: active\n";
        var plan = Plan(Page(Base), Page(Base, fm: fm));

        plan.Ok.Should().BeTrue();
        plan.Operations.Select(o => (o.Kind, o.Target, o.Text)).Should().Equal(
            (WikiHumanEditPlanner.KindHeader, "summary", "Новое описание"),
            (WikiHumanEditPlanner.KindHeader, "aliases", "А, Б"));
    }

    [Fact]
    public void Новая_страница_даёт_create_и_append()
    {
        var plan = WikiHumanEditPlanner.Build(Path, null, Page([Base[0]], notes: ["заметка"]));

        plan.Ok.Should().BeTrue();
        Kinds(plan).Should().Be("create,header,append,notes");
        plan.Operations[0].Text.Should().Be("Тема");
    }

    [Fact]
    public void Переписанная_строка_целиком_даёт_supersede_и_append()
    {
        var plan = Plan(Page(Base), Page([Base[0], "Совсем другая мысль про деплой"]));

        Kinds(plan).Should().Be("supersede,append");
    }

    [Fact]
    public void Две_правки_и_добавление_в_одной_секции()
    {
        var was = Page(["Сервер работает на порту 5000 [^s1]", "Бэкенд написан на ASP.NET Core [^s2]", "Фронт на React 18 [^s3]"]);
        var now = Page(["Сервер работает на порту 5001", "Бэкенд написан на ASP.NET Core 9", "Фронт на React 18", "Новое утверждение человека"]);
        var plan = Plan(was, now);

        Kinds(plan).Should().Be("edit,edit,append");
    }

    [Fact]
    public void Заголовок_страницы_правке_не_подлежит()
    {
        var plan = Plan(Page(Base), Page(Base, title: "Другая тема"));

        plan.Ok.Should().BeFalse();
        plan.Reason.Should().Contain("заголовок");
    }

    [Fact]
    public void Неразобранный_текст_это_результат_с_причиной_а_не_исключение()
    {
        var plan = Plan(Page(Base), "просто текст без шапки");

        plan.Ok.Should().BeFalse();
        plan.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Мусор_на_входе_не_бросает()
    {
        var plan = WikiHumanEditPlanner.Build("", Page(Base), Page(Base));
        plan.Ok.Should().BeFalse();

        var broken = WikiHumanEditPlanner.Build(Path, "---\nzzz", Page(Base));
        broken.Ok.Should().BeFalse();
    }
}
