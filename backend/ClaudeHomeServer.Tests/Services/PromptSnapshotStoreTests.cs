using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Хранилище снимков промпта: gzip-файлы data/prompt-snapshots/{sessionId}/{id}.json.gz.
// Пути строим от Path.GetTempPath() — тесты гоняются в Linux-CI, Windows-литералы там
// считаются относительными.
public class PromptSnapshotStoreTests : IDisposable
{
    private readonly string _dataDir;
    private readonly PromptSnapshotStore _store;

    public PromptSnapshotStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "ccs-prompt-snapshots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dataDir, "projects.json"),
            })
            .Build();
        _store = new PromptSnapshotStore(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static PromptSnapshotDraft Draft(string text = "Правила проекта",
        CliLayerDto? cliLayer = null) =>
        new(Applied: true, InheritedFromId: null,
            Sections: [new PromptSectionDto("project", "Промпт проекта", text)],
            CliArgs: ["--print"], McpServers: ["tasks"], Model: "opus", Mode: "acceptEdits",
            CliLayer: cliLayer);

    [Fact]
    public void Снимок_ЧитаетсяОбратноЦеликом()
    {
        var id = _store.Save("chat1", Draft());

        id.Should().NotBeNull();
        var loaded = _store.Load("chat1", id!);
        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(id);
        loaded.Applied.Should().BeTrue();
        loaded.Sections.Should().ContainSingle().Which.Text.Should().Be("Правила проекта");
        loaded.CliArgs.Should().Equal("--print");
        loaded.Model.Should().Be("opus");
    }

    [Fact]
    public void Ретеншн_ОставляетПоследние50Ходов()
    {
        var ids = new List<string>();
        for (var i = 0; i < 55; i++)
            ids.Add(_store.Save("chat1", Draft($"ход {i}"))!);

        // Свежие пятьдесят на месте, самые старые вытеснены
        _store.Load("chat1", ids[^1]).Should().NotBeNull();
        _store.Load("chat1", ids[^50]).Should().NotBeNull();
        _store.Load("chat1", ids[0]).Should().BeNull();
        Directory.GetFiles(Path.Combine(_dataDir, "prompt-snapshots", "chat1"))
            .Should().HaveCount(PromptSnapshotStore.MaxPerSession);
    }

    [Fact]
    public void УдалениеЧата_УноситВсеЕгоСнимки()
    {
        var id = _store.Save("chat1", Draft())!;
        var otherId = _store.Save("chat2", Draft())!;

        _store.DeleteAll("chat1");

        _store.Load("chat1", id).Should().BeNull();
        // Соседний чат не задет
        _store.Load("chat2", otherId).Should().NotBeNull();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../etc/passwd")]
    [InlineData("chat/../..")]
    [InlineData("")]
    public void НебезопасныйId_НеЧитаетсяИНеПишется(string id)
    {
        // Оба сегмента становятся частью пути к файлу: белый список, а не Path.GetFileName
        // (тот пропускает «..», а на Linux ещё и «..\..\» целиком — обратный слеш там
        // легальный символ имени)
        _store.Save(id, Draft()).Should().BeNull();
        _store.Load("chat1", id).Should().BeNull();
    }

    [Fact]
    public void ДлиннаяСекция_ОбрезаетсяСПометкой()
    {
        var huge = new string('я', 300 * 1024);
        var id = _store.Save("chat1", Draft(huge))!;

        var text = _store.Load("chat1", id)!.Sections[0].Text;
        text.Length.Should().BeLessThan(huge.Length);
        text.Should().EndWith("обрезано");
    }

    [Fact]
    public void СоставИнструментов_ДописываетсяВГотовыйСнимок()
    {
        var id = _store.Save("chat1", Draft())!;

        _store.AttachCliLayer("chat1", id, ["Read", "Edit"], [new McpServerInfo("tasks", "connected")]);

        var loaded = _store.Load("chat1", id)!;
        loaded.CliLayer!.Tools.Should().Equal("Read", "Edit");
        loaded.CliLayer.McpServers.Should().ContainSingle().Which.Name.Should().Be("tasks");
        // Остальное не испорчено перезаписью файла
        loaded.Sections.Should().ContainSingle();
    }

    [Fact]
    public void СоставИнструментов_НаВытесненныйСнимок_НеПадает()
    {
        var act = () => _store.AttachCliLayer("chat1", "1700000000000-abcd", ["Read"], []);

        act.Should().NotThrow();
    }

    [Fact]
    public void ПовторныйСлойCLI_НеДублируетсяАСсылается()
    {
        // CLAUDE.md весит десятки КБ и меняется редко: второй ход с тем же содержимым
        // ссылается на снимок-донор, а при чтении файлы разворачиваются обратно
        var layer = new CliLayerDto(
            Files: [new PromptSectionDto("CLAUDE.md", "CLAUDE.md", "правила", "cli-file")]);

        var first = _store.Save("chat1", Draft(cliLayer: layer))!;
        var second = _store.Save("chat1", Draft(cliLayer: layer))!;

        var loaded = _store.Load("chat1", second)!;
        loaded.CliLayerFrom.Should().Be(first);
        loaded.CliLayer!.Files.Should().ContainSingle().Which.Text.Should().Be("правила");
    }

    // ===== TruncatedSections: пометка в снимке об урезанных секциях (задача dc641949) =====

    [Fact]
    public void TruncatedSections_БезСрезки_ВСнимкеНетПоля()
    {
        // Обычный ход: бюджет не превышен, TruncatedSections=null. JSON-сериализация
        // (DefaultIgnoreCondition.WhenWritingNull) скрывает null-поле на диске, и при
        // загрузке оно остаётся null — UI не должен показывать пустую плашку "что
        // обрезали" в шторке для штатного хода.
        var id = _store.Save("chat1", Draft())!;
        var loaded = _store.Load("chat1", id)!;
        loaded.TruncatedSections.Should().BeNull();
    }

    [Fact]
    public void TruncatedSections_СЗаметкой_СохраняютсяИЧитаются()
    {
        // Срезка случилась: ApplyBudget вернул непустой список. Шторка UI должна видеть
        // ПОЛНЫЙ текст каждой пометки (что обрезали, сколько символов сейчас, почему
        // секция вырезана). Trim проходит по тем же потолкам, что и для Sections — заметки
        // короткие, усечение не активируется, контракт — ключ остаётся (truncated:code-graph).
        var truncated = new List<PromptSectionDto>
        {
            new("(truncated:code-graph)", "Секция «code-graph» обрезана",
                "Секция удалена из промпта: cmdline превысил 30 000 символов. После удаления обвязка уложилась в бюджет (29 800/30 000).",
                Kind: "truncated"),
        };
        var id = _store.Save("chat1", new PromptSnapshotDraft(
            Applied: true, InheritedFromId: null,
            Sections: [new PromptSectionDto("project", "Промпт проекта", "короткий")],
            CliArgs: ["--print"], McpServers: ["tasks"], Model: "opus", Mode: "acceptEdits",
            CliLayer: null, TruncatedSections: truncated))!;

        var loaded = _store.Load("chat1", id)!;
        loaded.TruncatedSections.Should().NotBeNull()
            .And.HaveCount(1);
        loaded.TruncatedSections![0].Key.Should().Be("(truncated:code-graph)",
            "ключ помечает, какая именно секция срезана — UI группирует по нему");
        loaded.TruncatedSections[0].Kind.Should().Be("truncated",
            "Kind отличает пометку от обычных system/turn/cli-file секций");
        loaded.TruncatedSections[0].Text.Should().Contain("30 000",
            "пометка содержит цифры — пользователь видит, НАСКОЛЬКО обвязка ушла за лимит");
    }

    [Fact]
    public void TruncatedSections_ПустойСписок_ВСнимкеНетПоля()
    {
        // Защита от регрессии: непустой массив нулевой длины не должен превратиться
        // в "плашку без содержимого". TrimOrNull возвращает null → JSON-сериализация
        // с WhenWritingNull уносит поле целиком.
        var id = _store.Save("chat1", new PromptSnapshotDraft(
            Applied: true, InheritedFromId: null,
            Sections: [new PromptSectionDto("project", "Промпт проекта", "короткий")],
            CliArgs: ["--print"], McpServers: ["tasks"], Model: "opus", Mode: "acceptEdits",
            CliLayer: null, TruncatedSections: []))!;

        _store.Load("chat1", id)!.TruncatedSections.Should().BeNull();
    }
}
