using System.Net;
using System.Text;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Мусор в ответах ComfyUI (C1, финальное ревью): чужие клиенты общего стенда кладут в
// extra_data/позиции/историю ЛЮБОЙ JSON-тип. GetValue<T>() на несовпадении типа бросает
// InvalidOperationException, который ронял монитор очереди (и, через него, весь сервер).
// Разбор обязан выживать: не-строка → null/пусто, не-массив → пусто, не-объект → пропуск.
public class ComfyClientGarbageTests
{
    // Фейк: произвольное JSON-тело на заданный путь (queue / system_stats / history)
    private sealed class RawFake : HttpMessageHandler
    {
        public string? Queue { get; set; }
        public string? SystemStats { get; set; }
        public string? History { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            string? body;
            if (path == "/queue") body = Queue;
            else if (path == "/system_stats") body = SystemStats;
            else if (path.StartsWith("/history/", StringComparison.Ordinal)) body = History;
            else body = null;
            var content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static (ComfyClient Client, RawFake Fake) Build()
    {
        var fake = new RawFake();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        return (new ComfyClient(new FakeComfyFactory(fake), config), fake);
    }

    // ─── /queue: client_id любого типа, prompt_id-мусор, extra_data не-объект, списки не-массивы ──

    [Fact]
    public async Task Очередь_ЧисловойClientId_НеБросает_ИДастNull()
    {
        var (client, fake) = Build();
        fake.Queue = """
        {
          "queue_running": [[0, "p1", {"1": {"class_type": "KSampler"}}, {"client_id": 42}]],
          "queue_pending": []
        }
        """;

        var state = await client.GetQueueAsync(CancellationToken.None);

        state.Running.Should().HaveCount(1);
        state.Running[0].PromptId.Should().Be("p1");
        state.Running[0].ClientId.Should().BeNull("числовой client_id — не строка, значит null");
    }

    [Fact]
    public async Task Очередь_ОбъектныйИМассивныйClientId_НеБросают()
    {
        var (client, fake) = Build();
        // client_id — объект и массив (оба не-строки)
        fake.Queue = """
        {
          "queue_running": [
            [0, "p1", {"1": {"class_type": "KSampler"}}, {"client_id": {"a": 1}}],
            [1, "p2", {"1": {"class_type": "KSampler"}}, {"client_id": ["x", "y"]}]
          ],
          "queue_pending": []
        }
        """;

        var state = await client.GetQueueAsync(CancellationToken.None);

        state.Running.Should().HaveCount(2);
        state.Running.Should().OnlyContain(i => i.ClientId == null, "не-строковый client_id → null");
    }

    [Fact]
    public async Task Очередь_ЧисловойPromptId_Пропускается()
    {
        var (client, fake) = Build();
        // prompt_id — число (не строка): пустой id отфильтровывается, разбор не падает
        fake.Queue = """
        {
          "queue_running": [[0, 7, {"1": {"class_type": "KSampler"}}, {"client_id": "ccs-local-media-x"}]],
          "queue_pending": []
        }
        """;

        var state = await client.GetQueueAsync(CancellationToken.None);

        state.Running.Should().BeEmpty("числовой prompt_id → пустой id → отфильтрован");
    }

    [Fact]
    public async Task Очередь_ExtraDataНеОбъект_НеБросает()
    {
        var (client, fake) = Build();
        // extra_data (4-й элемент) — строка, не объект: client_id не читаем
        fake.Queue = """
        {
          "queue_running": [[0, "p1", {"1": {"class_type": "KSampler"}}, "not-an-object"]],
          "queue_pending": []
        }
        """;

        var state = await client.GetQueueAsync(CancellationToken.None);

        state.Running.Should().HaveCount(1);
        state.Running[0].ClientId.Should().BeNull();
    }

    [Fact]
    public async Task Очередь_СпискиНеМассивы_НеБросают()
    {
        var (client, fake) = Build();
        // queue_running / queue_pending — не массивы (объект/число/строка)
        fake.Queue = """
        {
          "queue_running": {"a": 1},
          "queue_pending": 5
        }
        """;

        var state = await client.GetQueueAsync(CancellationToken.None);

        state.Running.Should().BeEmpty();
        state.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task Очередь_ЭлементКороткий_НеБросает()
    {
        var (client, fake) = Build();
        // Элемент короче 2 (нет prompt_id) и граф — число, не объект
        fake.Queue = """
        {
          "queue_running": [[0], [1, "p1", 42]],
          "queue_pending": []
        }
        """;

        var state = await client.GetQueueAsync(CancellationToken.None);

        state.Running.Should().HaveCount(1, "короткий элемент [0] отфильтрован");
        state.Running[0].PromptId.Should().Be("p1");
        state.Running[0].ClassTypes.Should().BeEmpty("граф-число → class_types пусто");
    }

    // ─── /system_stats: system не-объект, comfyui_version не-строка ───────────────────────────

    [Fact]
    public async Task SystemStats_SystemНеОбъект_НеБросает()
    {
        var (client, fake) = Build();
        fake.SystemStats = """
        { "system": "not-an-object" }
        """;

        var stats = await client.GetSystemStatsAsync(CancellationToken.None);

        stats.Should().NotBeNull();
        stats!.Version.Should().BeNull("system не-объект → версии нет");
        stats.Alive.Should().BeTrue();
    }

    [Fact]
    public async Task SystemStats_ЧисловаяВерсия_НеБросает()
    {
        var (client, fake) = Build();
        fake.SystemStats = """
        { "system": { "comfyui_version": 37 } }
        """;

        var stats = await client.GetSystemStatsAsync(CancellationToken.None);

        stats.Should().NotBeNull();
        stats!.Version.Should().BeNull("числовая версия — не строка");
    }

    // ─── /history: мусорные status/outputs ─────────────────────────────────────────────────────

    [Fact]
    public async Task История_МусорныйStatus_НеБросает()
    {
        var (client, fake) = Build();
        // status_str — число, completed — число, outputs — строка
        fake.History = """
        {
          "p1": {
            "status": { "status_str": 5, "completed": 1, "messages": "garbage" },
            "outputs": "not-an-object"
          }
        }
        """;

        var history = await client.GetHistoryAsync("p1", CancellationToken.None);

        history.Should().NotBeNull();
        history!.Completed.Should().BeFalse("completed=1 (число) — не bool true");
    }

    [Fact]
    public async Task История_МусорныеФайлы_НеБросают()
    {
        var (client, fake) = Build();
        // filename — число, type — число, subfolder — объект
        fake.History = """
        {
          "p1": {
            "status": { "status_str": "success", "completed": true, "messages": [] },
            "outputs": { "1": { "images": [ { "filename": 7, "type": 1, "subfolder": {"a": 1} } ] } }
          }
        }
        """;

        var history = await client.GetHistoryAsync("p1", CancellationToken.None);

        history.Should().NotBeNull();
        history!.Files.Should().BeEmpty("filename-число → пустое имя → файл не собран");
    }
}
