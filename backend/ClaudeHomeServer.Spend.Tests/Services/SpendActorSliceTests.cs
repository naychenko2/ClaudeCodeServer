using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// S2a-6b: срез чтения расхода по участникам общего проекта. Ключ хранилища — арендатор
// (OwnerId), актор (ActorUserId) — разрез: владелец видит свои общие проекты с разбивкой по
// участникам, участник — только свои записи, посторонний — ничего. Записи без актора
// (старый формат, серверный ход) относятся владельцу.
public class SpendActorSliceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spend-actor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SpendStore NewStore(int detailDays = 30) => new(_dir, detailDays);

    private static SpendAnalyticsService NewAnalytics(SpendStore store)
    {
        var sessions = new Mock<ISessionDirectory>();
        sessions.Setup(s => s.GetById(It.IsAny<string>())).Returns((Session?)null);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(It.IsAny<string>())).Returns((Project?)null);
        var tasks = new Mock<ITaskLookup>();
        tasks.Setup(t => t.GetById(It.IsAny<string>())).Returns((TaskItem?)null);
        var personas = new Mock<IPersonaLookup>();
        personas.Setup(p => p.GetByIdInternal(It.IsAny<string>())).Returns((Persona?)null);
        var users = new Mock<IUserStore>();
        users.Setup(u => u.GetById(It.IsAny<string>())).Returns((User?)null);
        var llm = new Mock<IModelResolver>();
        llm.Setup(l => l.ResolveModelOrDefault(It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((string? model, string? provider) => model ?? $"{provider}-default");
        return new SpendAnalyticsService(store, sessions.Object, projects.Object, tasks.Object,
            personas.Object, users.Object, llm.Object);
    }

    // Общий проект owner с двумя участниками: ходы u-a и u-b лежат под ключом owner.
    // Личный чат owner — его собственный актор.
    private static SpendRecord Turn(DateTime ts, string owner, string? actor, string? project,
        long input = 10, long output = 5) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = ts,
            OwnerId = owner,
            ProjectId = project,
            SessionId = project is null ? "s-personal" : "s-shared",
            ActorUserId = actor,
            Provider = "claude",
            Model = "opus",
            Source = SpendSources.ChatTurn,
            InputTokens = input,
            OutputTokens = output,
        };

    private static readonly DateOnly Day = new(2026, 7, 20);
    private static readonly DateTime Ts = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Владелец_ВидитРазбивкуПоАкторамУчастников_ПоСвоемуОбщемуПроекту()
    {
        var store = NewStore();
        store.Record(Turn(Ts, "owner", "u-a", "p-shared", input: 100));
        store.Record(Turn(Ts, "owner", "u-b", "p-shared", input: 200));
        store.Record(Turn(Ts, "owner", "owner", null));

        var analytics = NewAnalytics(store);
        // «Своё» для владельца — весь его общий проект (чужие ходы в нём) плюс его личные чаты
        var slices = analytics.Slices(Day, Day, new SpendFilter(Me: "owner"));
        slices.Should().HaveCount(3);

        var nodes = SpendAnalyticsService.GroupRaw(slices, "user");
        nodes.Should().HaveCount(3)
            .And.Contain(n => n.Key == "u-a")
            .And.Contain(n => n.Key == "u-b")
            .And.Contain(n => n.Key == "owner");
        nodes[0].Key.Should().Be("u-b"); // сортировка по весу: 200+5 больше 100+5
    }

    [Fact]
    public void Участник_ВидитТолькоСвоиЗаписи_ДваУчастника()
    {
        var store = NewStore();
        store.Record(Turn(Ts, "owner", "u-a", "p-shared", input: 100));
        store.Record(Turn(Ts, "owner", "u-b", "p-shared", input: 200));
        store.Record(Turn(Ts, "u-a", "u-a", null)); // его личный проект

        var analytics = NewAnalytics(store);
        var ownA = analytics.Slices(Day, Day, new SpendFilter(Me: "u-a"));
        ownA.Should().HaveCount(2)
            .And.OnlyContain(s => s.OwnerId == "u-a" || s.ActorUserId == "u-a");
        ownA.Should().NotContain(s => s.ActorUserId == "u-b");

        var ownB = analytics.Slices(Day, Day, new SpendFilter(Me: "u-b"));
        ownB.Should().ContainSingle().Which.ActorUserId.Should().Be("u-b");
    }

    [Fact]
    public void Посторонний_НеВидитНичего()
    {
        var store = NewStore();
        store.Record(Turn(Ts, "owner", "u-a", "p-shared"));

        var analytics = NewAnalytics(store);
        analytics.Slices(Day, Day, new SpendFilter(Me: "u-z")).Should().BeEmpty();
    }

    // Старый формат (строка jsonl без поля): читается без миграции, актора нет, запись
    // относится владельцу — участнику она не открывается.
    [Fact]
    public void СтарыеЗаписи_БезАктора_ЧитаютсяИОтносятсяВладельцу()
    {
        Directory.CreateDirectory(_dir);
        var oldLine = JsonSerializer.Serialize(new
        {
            id = "old-1", timestamp = Ts, ownerId = "owner", projectId = "p-shared",
            sessionId = "s-shared", provider = "claude", model = "opus",
            source = SpendSources.ChatTurn, inputTokens = 50, outputTokens = 5,
            cacheReadTokens = 0, cacheCreationTokens = 0, generations = 0, durationMs = 0,
        });
        File.WriteAllText(Path.Combine(_dir, "turns-2026-07-20.jsonl"), oldLine + "\n");

        var store = NewStore();
        var old = Assert.Single(store.DetailsBetween(Day, Day));
        old.ActorUserId.Should().BeNull();

        var analytics = NewAnalytics(store);
        analytics.Slices(Day, Day, new SpendFilter(Me: "owner")).Should().ContainSingle();
        analytics.Slices(Day, Day, new SpendFilter(Me: "u-a")).Should().BeEmpty();
    }

    // Свёрнутый день: актор — часть составного ключа. Без него участник за детальным окном
    // своих записей не увидел бы, а разбивка владельца по участникам пропала бы.
    [Fact]
    public void Rollup_СохраняетАктора_УчастникВидитСвоёЗаОкном()
    {
        var store = NewStore();
        var oldTs = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        store.Record(Turn(oldTs, "owner", "u-a", "p-shared", input: 100));
        store.Record(Turn(oldTs, "owner", "u-b", "p-shared", input: 200));

        store.RollupOlderThan(new DateOnly(2026, 7, 10));

        var daily = store.DailyBetween(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1));
        daily.Should().HaveCount(2)
            .And.OnlyContain(d => d.OwnerId == "owner");
        daily.Should().Contain(d => d.ActorUserId == "u-a").And.Contain(d => d.ActorUserId == "u-b");

        var analytics = NewAnalytics(store);
        analytics.Slices(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1), new SpendFilter(Me: "u-a"))
            .Should().ContainSingle().Which.ActorUserId.Should().Be("u-a");
    }

    // MAJOR вердикта efa973f5: ключ свёртки дневных проходов (MergeByKey) обязан включать актора,
    // как и Aggregate. Иначе часовой ReclassifyLocalAsFree, поменявший Source строкам свёрнутого
    // дня, сливает строки двух участников одного чата/модели/дня в одну: расход переатрибутируется,
    // второй участник теряет свой срез.
    [Fact]
    public void Reclassify_СвёрнутыйДень_НеСливаетУчастников_КаждыйСоСвоейСтрокой()
    {
        var store = NewStore();
        var old = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        // Два участника одного чата/модели/дня на локальном движке под ключом арендатора
        store.Record(new SpendRecord { OwnerId = "owner", ProjectId = "p-shared", SessionId = "s-shared",
            ActorUserId = "u-a", Provider = "local-qwen", Model = "qwen3.8-27b",
            InputTokens = 100, OutputTokens = 5, Timestamp = old });
        store.Record(new SpendRecord { OwnerId = "owner", ProjectId = "p-shared", SessionId = "s-shared",
            ActorUserId = "u-b", Provider = "local-qwen", Model = "qwen3.8-27b",
            InputTokens = 200, OutputTokens = 7, Timestamp = old });

        store.RollupOlderThan(new DateOnly(2026, 7, 10)); // Aggregate: две строки, каждая со своим актором

        store.ReclassifyLocalAsFree(p => p == "local-qwen").Should().Be(2); // часовых проход по daily.json

        var rows = store.DailyBetween(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1));
        rows.Should().HaveCount(2, "актор в ключе свёртки: строки с разными акторами не сливаются никогда");
        rows.Should().OnlyContain(r => r.Source == SpendSources.Free);
        rows.Single(r => r.ActorUserId == "u-a").InputTokens.Should().Be(100);
        rows.Single(r => r.ActorUserId == "u-b").InputTokens.Should().Be(200);

        // Срез чтения: каждый участник за пределами детального окна видит только своё
        var analytics = NewAnalytics(store);
        analytics.Slices(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1), new SpendFilter(Me: "u-a"))
            .Should().ContainSingle().Which.Input.Should().Be(100);
        analytics.Slices(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1), new SpendFilter(Me: "u-b"))
            .Should().ContainSingle().Which.Input.Should().Be(200);
    }
}
