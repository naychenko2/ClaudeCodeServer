using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Architecture;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Architecture;

// Проверка модели Viaduct: висящие ссылки, точки связей, которых у карточки нет, и дубли id —
// предупреждения, которые сервер отдаёт в ответах сохранения, сборки и arch_*.
public class ArchitectureModelValidatorTests
{
    // Чистая модель: связи с точками, которые пишет редактор, связь без точек (так пишут
    // генератор и тулсет) и табличная карточка с точками без номера
    private const string Clean = """
        {"state":{"model":{
          "systems":[{"id":"s1","name":"CCS","connections":[]}],
          "containers":[
            {"id":"c1","name":"Бэкенд","systemId":"s1","connections":[
              {"targetId":"c2","sourceHandle":"source-right-0","targetHandle":"target-top-1-s2"},
              {"targetId":"t1"}]},
            {"id":"c2","name":"Хранилище","systemId":"s1","connections":[]}
          ],
          "components":[
            {"id":"k1","name":"SessionManager","systemId":"s1","containerId":"c1","connections":[]},
            {"id":"t1","name":"sessions","containerId":"c2","columns":[],"connections":[
              {"targetId":"t2","sourceHandle":"source-bottom","targetHandle":"target-left"}]},
            {"id":"t2","name":"users","containerId":"c2","columns":[],"connections":[]}
          ],
          "codeElements":[{"id":"x1","name":"Run","componentId":"k1","containerId":"c1","systemId":"s1"}],
          "dataFlows":[{"id":"f1","name":"Поток","steps":[
            {"id":"st1","name":"Шаг","from":{"id":"c1"},"to":{"id":"c2"},
             "connections":[{"sourceId":"c1","targetId":"c2"}],"channelIds":["k1"]}]}]
        }},"version":0}
        """;

    private static JsonObject Doc(string json = Clean) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonObject Model(JsonObject doc) => (JsonObject)doc["state"]!["model"]!;

    private static JsonObject Element(JsonObject doc, string array, string id) =>
        Model(doc)[array]!.AsArray().OfType<JsonObject>().Single(o => (string?)o["id"] == id);

    [Fact]
    public void ЧистаяМодель_НаходокНет() =>
        ArchitectureModelValidator.Validate(Doc()).Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("""{"state":{}}""")]
    [InlineData("""{"state":1}""")]
    [InlineData("""{"state":[]}""")]
    [InlineData("""{"state":{"model":"x"}}""")]
    [InlineData("[]")]
    [InlineData("1")]
    public void НетМоделиИлиНеJson_НаходокНет(string? content) =>
        ArchitectureModelValidator.ValidateContent(content).Should().BeEmpty();

    // --- Висящие ссылки ---

    [Fact]
    public void УдалённыйКонтейнер_ВисятРодительДетейИСвязьСоседа()
    {
        var doc = Doc();
        var containers = Model(doc)["containers"]!.AsArray();
        containers.Remove(Element(doc, "containers", "c1"));
        // Связь соседа на удалённый контейнер — так бывает, когда правят файл руками
        Element(doc, "containers", "c2")["connections"] = new JsonArray(new JsonObject
        {
            ["targetId"] = "c1", ["sourceHandle"] = "source-right-0", ["targetHandle"] = "nonsense",
        });

        var findings = ArchitectureModelValidator.Validate(doc);

        findings.Where(f => f.Kind == ArchitectureModelValidator.KindDanglingParent)
            .Select(f => f.ElementId).Should().BeEquivalentTo("k1", "x1");
        findings.Should().Contain(f => f.Kind == ArchitectureModelValidator.KindDanglingParent
            && f.Text.Contains("containerId → c1") && f.Text.Contains("«SessionManager»"));
        var link = findings.Should().ContainSingle(f => f.Kind == ArchitectureModelValidator.KindDanglingConnection).Subject;
        link.ElementId.Should().Be("c2");
        link.Text.Should().Contain("связь → c1 — такого элемента нет");
        // Висящая связь не дублируется находкой про точки
        findings.Should().NotContain(f => f.Kind == ArchitectureModelValidator.KindUnknownHandle);
        // Шаг потока смотрит на удалённый c1 концом и связью — одна находка на шаг
        var step = findings.Should().ContainSingle(f => f.Kind == ArchitectureModelValidator.KindDanglingFlowStep).Subject;
        step.ElementId.Should().Be("f1");
        step.Text.Should().Contain("шаг «Шаг» ссылается на c1");
    }

    [Fact]
    public void РодительНеТойКоллекции_Висит()
    {
        var doc = Doc();
        // systemId указывает на контейнер: такой id в модели есть, но системы с ним нет
        Element(doc, "containers", "c2")["systemId"] = "c1";

        ArchitectureModelValidator.Validate(doc).Should().ContainSingle()
            .Which.Text.Should().Contain("systemId → c1 — такого элемента среди систем нет");
    }

    [Fact]
    public void ПустаяСтрокаВСсылке_КакОтсутствие()
    {
        var doc = Doc();
        Element(doc, "containers", "c2")["systemId"] = "";
        Element(doc, "containers", "c2")["connections"] = new JsonArray(new JsonObject { ["targetId"] = "" });
        ArchitectureModelValidator.Validate(doc).Should().BeEmpty();
    }

    // --- Точки связей ---

    [Theory]
    [InlineData("source-right", "target-left-0", "sourceHandle «source-right»")]
    [InlineData("source-right-0", "target-left", "targetHandle «target-left»")]
    [InlineData("source-top-0", "target-left-0", "sourceHandle «source-top-0»")]
    public void НеизвестнаяТочкаОбычнойКарточки_Находка(string source, string target, string expected)
    {
        var doc = Doc();
        var link = Element(doc, "containers", "c1")["connections"]![0]!.AsObject();
        link["sourceHandle"] = source;
        link["targetHandle"] = target;

        var finding = ArchitectureModelValidator.Validate(doc).Should().ContainSingle().Subject;
        finding.Kind.Should().Be(ArchitectureModelValidator.KindUnknownHandle);
        finding.ElementId.Should().Be("c1");
        finding.Text.Should().Contain(expected).And.Contain("→ «Хранилище» не нарисуется");
    }

    [Fact]
    public void ВсеТочкиРедактора_Принимаются()
    {
        // Полный набор имён, которые пишет бандл Viaduct: сторона-номер и слоты
        string[] sources =
        [
            "source-right-0", "source-right-0-s1", "source-bottom-1-s0", "source-bottom-1", "source-bottom-1-s2",
            "source-left-2", "source-left-2-s1", "source-top-3-s0", "source-top-3", "source-top-3-s2",
        ];
        string[] targets =
        [
            "target-left-0", "target-left-0-s1", "target-top-1-s0", "target-top-1", "target-top-1-s2",
            "target-bottom-2-s0", "target-bottom-2", "target-bottom-2-s2", "target-right-3", "target-right-3-s1",
        ];
        foreach (var (s, t) in sources.Zip(targets))
        {
            var doc = Doc();
            var link = Element(doc, "containers", "c1")["connections"]![0]!.AsObject();
            link["sourceHandle"] = s;
            link["targetHandle"] = t;
            ArchitectureModelValidator.Validate(doc).Should().BeEmpty($"точки {s} → {t} редактор пишет сам");
        }
    }

    [Fact]
    public void ТабличнаяКарточка_НомернаяТочкаЧужая()
    {
        var doc = Doc();
        var link = Element(doc, "components", "t1")["connections"]![0]!.AsObject();
        link["sourceHandle"] = "source-right-0";

        ArchitectureModelValidator.Validate(doc).Should().ContainSingle()
            .Which.Text.Should().Contain("«source-right-0»").And.Contain("есть source-right, source-bottom");
    }

    // --- Дубли id ---

    [Fact]
    public void ДубльIdМеждуУровнями_ОднаНаходкаСОбоимиЭлементами()
    {
        var doc = Doc();
        Element(doc, "components", "k1")["id"] = "c2";
        // Дети и связи k1 теперь смотрят в пустоту — отвязываем, чтобы проверить только дубль
        Model(doc)["codeElements"] = new JsonArray();
        Model(doc)["dataFlows"] = new JsonArray();

        var finding = ArchitectureModelValidator.Validate(doc).Should().ContainSingle().Subject;
        finding.Kind.Should().Be(ArchitectureModelValidator.KindDuplicateId);
        finding.ElementId.Should().Be("c2");
        finding.Text.Should().Contain("повторяется (2)").And.Contain("контейнер «Хранилище»")
            .And.Contain("компонент «SessionManager»");
    }

    [Fact]
    public void IdПотокаСовпадаетСIdЭлемента_НеДубль()
    {
        var doc = Doc();
        Model(doc)["dataFlows"]![0]!["id"] = "c1";
        ArchitectureModelValidator.Validate(doc).Should().BeEmpty();
    }

    [Fact]
    public void Render_ОбрезаетХвост()
    {
        var findings = Enumerable.Range(0, ArchitectureModelValidator.MaxShown + 3)
            .Select(i => new ArchitectureModelFinding(ArchitectureModelValidator.KindDuplicateId, $"e{i}", $"находка {i}"))
            .ToList();
        var text = ArchitectureModelValidator.Render(findings);
        text.Should().Contain("- находка 0").And.Contain("…и ещё 3.").And.NotContain("находка 22");
    }
}
