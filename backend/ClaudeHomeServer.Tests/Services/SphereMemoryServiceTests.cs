using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Полка памяти сферы: запись/поиск, изоляция сфер, перезагрузка стора, удаление вместе со сферой
public class SphereMemoryServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly SphereMemoryService _svc;

    public SphereMemoryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sphere-mem-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();
        _svc = new SphereMemoryService(_config);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Add_Дефолты_ManualFact_БезПроисхождения()
    {
        var e = _svc.Add("u1", "s1", "  общий стек сферы — .NET  ");

        e.Text.Should().Be("общий стек сферы — .NET");
        e.Source.Should().Be(TeamMemorySource.Manual);
        e.Type.Should().Be(TeamMemoryType.Fact);
        e.ProjectId.Should().Be("s1");
        e.PromotedFrom.Should().BeNull();
    }

    [Fact]
    public async Task Search_НаходитЗапись_ИзвестнойСферы()
    {
        _svc.Add("u1", "s1", "деплой идёт через blue-green выкладку");
        _svc.Add("u1", "s1", "названия веток начинаются с feature");

        var found = await _svc.SearchAsync("u1", "s1", "blue-green деплой");

        found.Should().ContainSingle().Which.Text.Should().Contain("blue-green");
    }

    [Fact]
    public async Task Recall_ВыдаётЗаголовокПамятиСферы()
    {
        _svc.Add("u1", "s1", "релизы по четвергам");

        var r = await _svc.BuildRecallBlockAsync("u1", "s1", "релизы четверг");

        r.Text.Should().StartWith("## Память сферы");
        r.Used.Should().ContainSingle();
    }

    [Fact]
    public async Task ДвеСферы_Изолированы_ВПоискеИСписке()
    {
        _svc.Add("u1", "s1", "секрет первой сферы про кроликов");
        _svc.Add("u1", "s2", "секрет второй сферы про черепах");

        (await _svc.SearchAsync("u1", "s1", "черепах")).Should().BeEmpty();
        (await _svc.SearchAsync("u1", "s2", "кроликов")).Should().BeEmpty();
        _svc.List("u1", "s1").Should().ContainSingle().Which.Text.Should().Contain("кроликов");
        _svc.Count("u1", "s2").Should().Be(1);
    }

    [Fact]
    public void ДваВладельца_СОдинаковымИдСферы_Изолированы()
    {
        _svc.Add("u1", "s1", "запись первого владельца");

        _svc.List("u2", "s1").Should().BeEmpty();
        _svc.Count("u2", "s1").Should().Be(0);
    }

    [Fact]
    public async Task DeleteAllForSphere_УдаляетТолькоСвоюСферу_ИВозвращаетЧисло()
    {
        _svc.Add("u1", "s1", "первая запись сферы один");
        _svc.Add("u1", "s1", "вторая запись сферы один");
        _svc.Add("u1", "s2", "запись сферы два");

        var deleted = await _svc.DeleteAllForSphereAsync("u1", "s1");

        deleted.Should().Be(2);
        _svc.Count("u1", "s1").Should().Be(0);
        _svc.List("u1", "s2").Should().ContainSingle();
        _svc.AllScopes().Should().ContainSingle().Which.Should().Be(("u1", "s2"));
    }

    [Fact]
    public async Task DeleteAllForSphere_ПереживаетПерезагрузкуСтора()
    {
        _svc.Add("u1", "s1", "запись на удаление");
        _svc.Add("u1", "s2", "запись остаётся");
        await _svc.DeleteAllForSphereAsync("u1", "s1");

        using var reloaded = new SphereMemoryService(_config);

        reloaded.Count("u1", "s1").Should().Be(0);
        reloaded.Count("u1", "s2").Should().Be(1);
    }

    [Fact]
    public void PromotedFrom_СохраняетсяВСтореИЧитаетсяПослеПерезагрузки()
    {
        var at = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        _svc.Add("u1", "s1", "поднятая из проекта договорённость",
            TeamMemoryType.Convention, promotedFrom: new MemoryPromotion("p1", "e1", at));

        using var reloaded = new SphereMemoryService(_config);

        var e = reloaded.List("u1", "s1").Should().ContainSingle().Subject;
        e.Source.Should().Be(TeamMemorySource.Manual);
        e.PromotedFrom.Should().Be(new MemoryPromotion("p1", "e1", at));
    }

    [Fact]
    public void Файлы_Стора_ЛежатВDataСОжидаемымиИменами()
    {
        _svc.Add("u1", "s1", "запись для проверки файла");

        File.Exists(Path.Combine(_dir, "sphere-memory.json")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "team-memory.json")).Should().BeFalse();
    }

    [Fact]
    public void DatasetName_ИмеетФормуUsernameSphereId()
    {
        SphereMemoryService.DatasetName("alice", "s-42").Should().Be("alice:sphere:s-42");
    }
}
