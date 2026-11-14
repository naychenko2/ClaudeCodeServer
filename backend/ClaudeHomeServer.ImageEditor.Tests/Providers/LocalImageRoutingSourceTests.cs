using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.ImageEditor.Providers;

// Таблица «Авто» из LocalMedia:Routing (Ш3): на лету, таблица из кода — умолчание и откат при невалидной секции
public class LocalImageRoutingSourceTests
{
    private static readonly IReadOnlyList<ImageEditModelInfo> Catalog = new LocalImageEditor(null).Models;

    // Секция из правил: так же, как её запишет человек в appsettings.Local.json
    private static Dictionary<string, string?> Section(IEnumerable<RouteRule> rules)
    {
        var d = new Dictionary<string, string?>();
        var i = 0;
        foreach (var r in rules)
        {
            var p = $"{LocalImageRoutingSource.SectionName}:{i++}";
            d[$"{p}:Id"] = r.Id;
            d[$"{p}:Reason"] = r.Reason;
            for (var k = 0; k < r.Ops.Count; k++) d[$"{p}:Ops:{k}"] = r.Ops[k].ToString();
            for (var k = 0; k < r.Models.Count; k++) d[$"{p}:Models:{k}"] = r.Models[k];
            // Признаки массивом: «People, Photo» → ["People", "Photo"]
            foreach (var (key, traits) in new[] { ("Any", r.Any), ("Forbid", r.Forbid) })
            {
                if (traits == RouteTrait.None) continue;
                var names = traits.ToString().Split(", ");
                for (var k = 0; k < names.Length; k++) d[$"{p}:{key}:{k}"] = names[k];
            }
        }
        return d;
    }

    // Таблица из кода, где генерация по умолчанию идёт в Krea
    private static IEnumerable<RouteRule> KreaByDefault() => LocalImageRouting.Table.Select(r =>
        r.Id == "gen-default" ? r with { Models = [LocalImageEditor.Krea2Turbo], Reason = "по умолчанию из конфига" } : r);

    private static IConfigurationRoot Config(Dictionary<string, string?> d) =>
        new ConfigurationBuilder().AddInMemoryCollection(d).Build();

    private static ImageEditOp GenOp => ImageEditOp.Generate;
    private static EditTraits Cat => new(false, 0, false, Prompt: "кот на подоконнике");

    [Fact]
    public void НетСекции_ТаблицаИзКода()
    {
        var src = new LocalImageRoutingSource(Config([]), Catalog);
        src.FromConfig.Should().BeFalse();
        src.Table.Should().BeSameAs(LocalImageRouting.Table);
    }

    [Fact]
    public void ТаблицаИзКода_ПроходитСвоюЖеПроверку() =>
        LocalImageRoutingSource.Parse(Config(Section(LocalImageRouting.Table)).GetSection(LocalImageRoutingSource.SectionName), Catalog)
            .Error.Should().BeNull();

    // Пример в appsettings.Local.example.json — копия таблицы из кода: скопированный пример ничего не меняет
    [Fact]
    public void ПримерВКонфиге_ВалиденИСовпадаетСТаблицейИзКода()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClaudeHomeServer", "appsettings.Local.example.json")))
            dir = dir.Parent;
        dir.Should().NotBeNull("пример конфига ищется вверх от каталога сборки тестов");
        var cfg = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(dir!.FullName, "ClaudeHomeServer", "appsettings.Local.example.json")).Build();
        var (table, error) = LocalImageRoutingSource.Parse(cfg.GetSection(LocalImageRoutingSource.SectionName), Catalog);
        error.Should().BeNull();
        table!.Select(Key).Should().Equal(LocalImageRouting.Table.Select(Key));
    }

    private static string Key(RouteRule r) =>
        $"{r.Id}|{string.Join(",", r.Ops)}|{r.Any}|{r.Forbid}|{string.Join(",", r.Models)}|{r.Reason}";

    [Fact]
    public void ВалиднаяСекция_РедакторВыбираетПоНей()
    {
        var editor = new LocalImageEditor(new LocalImageEditorTests.FakeMedia(), config: Config(Section(KreaByDefault())));
        var r = editor.Route(GenOp, EditMode.Auto, Cat)!;
        r.Model.Id.Should().Be(LocalImageEditor.Krea2Turbo);
        r.Reason.Should().Be("Авто → Krea 2 Turbo: по умолчанию из конфига");
    }

    [Fact]
    public void ПризнакиСтрокойЧерезЗапятую()
    {
        var d = Section(LocalImageRouting.Table);
        var i = LocalImageRouting.Table.ToList().FindIndex(r => r.Id == "people-gen");
        foreach (var k in d.Keys.Where(k => k.StartsWith($"{LocalImageRoutingSource.SectionName}:{i}:Any:")).ToList()) d.Remove(k);
        d[$"{LocalImageRoutingSource.SectionName}:{i}:Any"] = "People, Photo";
        var (table, error) = LocalImageRoutingSource.Parse(Config(d).GetSection(LocalImageRoutingSource.SectionName), Catalog);
        error.Should().BeNull();
        table!.Single(r => r.Id == "people-gen").Any.Should().Be(RouteTrait.People | RouteTrait.Photo);
    }

    [Fact]
    public void Перечитывается_НаЛету()
    {
        var cfg = Config([]);
        var editor = new LocalImageEditor(new LocalImageEditorTests.FakeMedia(), config: cfg);
        editor.Route(GenOp, EditMode.Auto, Cat)!.Model.Id.Should().Be(LocalImageEditor.FluxKlein);

        foreach (var (k, v) in Section(KreaByDefault())) cfg[k] = v;
        cfg.Reload();
        editor.Route(GenOp, EditMode.Auto, Cat)!.Model.Id.Should().Be(LocalImageEditor.Krea2Turbo);
    }

    public static TheoryData<string, string?> Поломки => new()
    {
        { "0:Models:0", "no-such-model" },
        { "0:Ops:0", "Paint" },
        { "1:Any:0", "Photorealish" },
        { "1:Any:0", "4" },
        { "0:Reason", "" },
        { "1:Id", "mask" },
    };

    [Theory]
    [MemberData(nameof(Поломки))]
    public void НевалиднаяСекция_ОткатНаТаблицуИзКода(string key, string? value)
    {
        var d = Section(LocalImageRouting.Table);
        d[$"{LocalImageRoutingSource.SectionName}:{key}"] = value;
        var src = new LocalImageRoutingSource(Config(d), Catalog);
        src.FromConfig.Should().BeFalse();
        src.Table.Should().BeSameAs(LocalImageRouting.Table);
    }

    [Fact]
    public void НетОбщегоПравила_ОткатНаТаблицуИзКода()
    {
        var src = new LocalImageRoutingSource(Config(Section(LocalImageRouting.Table.Where(r => r.Id != "gen-default"))), Catalog);
        src.FromConfig.Should().BeFalse();
    }

    [Fact]
    public void СтавшаяНевалиднойНаЛету_ОткатНаТаблицуИзКода()
    {
        var cfg = Config(Section(KreaByDefault()));
        var src = new LocalImageRoutingSource(cfg, Catalog);
        src.FromConfig.Should().BeTrue();

        cfg[$"{LocalImageRoutingSource.SectionName}:0:Models:0"] = "no-such-model";
        cfg.Reload();
        src.Table.Should().BeSameAs(LocalImageRouting.Table);
    }
}
