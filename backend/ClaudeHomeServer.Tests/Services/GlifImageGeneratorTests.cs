using ClaudeHomeServer.Services.Images;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Драйвер glif: разбор ответов джобы на зафиксированных образцах (сеть не трогаем)
// и выключенность без токена.
public class GlifImageGeneratorTests
{
    private static GlifImageGenerator Generator(string? token)
    {
        var values = new Dictionary<string, string?>();
        if (token is not null) values["Glif:McpToken"] = token;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new GlifImageGenerator(
            Mock.Of<IHttpClientFactory>(), config, NullLogger<GlifImageGenerator>.Instance);
    }

    [Fact]
    public void БезТокена_ДрайверВыключен()
    {
        Generator(null).Enabled.Should().BeFalse();
        Generator("   ").Enabled.Should().BeFalse("пробелы — не токен");
        Generator("glif_v1_xxx").Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task БезТокена_ГенерацияВозвращаетПусто_БезСети()
    {
        // Фабрика клиентов — заглушка: если драйвер полезет в сеть, тест упадёт по NRE
        var images = await Generator(null).GenerateManyAsync("иконка проекта", 2, null);
        images.Should().BeEmpty();
    }

    [Fact]
    public void Модели_ЭтоУровниАгента()
    {
        Generator("t").Models.Select(m => m.Id).Should().Equal("lite", "smart", "genius");
    }

    [Fact]
    public void Парсер_СтартComposeProject_ДаётJobIdИИнтервал()
    {
        // Форма из outputSchema живого tools/list (2026-08-15)
        var body = """
            {"jsonrpc":"2.0","id":1,"result":{
              "content":[{"type":"text","text":"Project job started"}],
              "structuredContent":{
                "outputType":"project_job_started",
                "projectId":"p-1","projectUrl":"https://glif.app/chat/p-1",
                "jobId":"job-1","status":"working","statusMessage":null,
                "pollIntervalSeconds":5}}}
            """;

        var job = GlifJobParser.Parse(body);

        job.State.Should().Be(GlifJobState.Working);
        job.JobId.Should().Be("job-1");
        job.ProjectId.Should().Be("p-1");
        job.PollIntervalSeconds.Should().Be(5);
        job.MediaUrls.Should().BeEmpty();
    }

    [Fact]
    public void Парсер_SSEКадр_РазбираетсяКакJson()
    {
        var body = "event: message\r\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"structuredContent\":"
                   + "{\"outputType\":\"project_job_started\",\"jobId\":\"job-sse\",\"status\":\"working\","
                   + "\"pollIntervalSeconds\":3}}}\r\n\r\n";

        var job = GlifJobParser.Parse(body);

        job.JobId.Should().Be("job-sse");
        job.State.Should().Be(GlifJobState.Working);
        job.PollIntervalSeconds.Should().Be(3);
    }

    [Fact]
    public void Парсер_ЗавершённаяДжоба_ДаётСсылкиНаМедиа()
    {
        // Боевая форма completed-ответа get_job_status (см. GlifCostParserTests)
        var body = """
            {"result":{
              "content":[
                {"type":"text","text":"Done"},
                {"type":"resource_link","name":"icon.jpg","uri":"https://glifusercontent.com/i:r/abc.jpg"}
              ],
              "structuredContent":{
                "outputType":"image","projectId":"p-1","jobId":"job-1","status":"completed",
                "media":[{"url":"https://glifusercontent.com/i:r/abc.jpg","title":"icon.jpg","kind":"image"},
                         {"url":"https://res.cloudinary.com/dzkwltgyd/image/upload/x.png","kind":"image"}]},
              "_meta":{"glif":{"outputType":"image","jobId":"job-1","status":"completed","subtractedCredits":"4.6"}}}}
            """;

        var job = GlifJobParser.Parse(body);

        job.State.Should().Be(GlifJobState.Completed);
        job.JobId.Should().Be("job-1");
        // Один и тот же файл из media[] и resource_link — одной ссылкой
        job.MediaUrls.Should().Equal(
            "https://glifusercontent.com/i:r/abc.jpg",
            "https://res.cloudinary.com/dzkwltgyd/image/upload/x.png");
    }

    [Fact]
    public void Парсер_МедиаТолькоВTextБлоке_ТожеНаходятся()
    {
        // Дубль structuredContent JSON-строкой внутри text-блока
        var body = """
            {"result":{"content":[{"type":"text","text":"{\"outputType\":\"project_update\",\"jobId\":\"job-2\",\"status\":\"completed\",\"media\":[{\"url\":\"https://glifusercontent.com/i:r/x.png\"}]}"}]}}
            """;

        var job = GlifJobParser.Parse(body);

        job.State.Should().Be(GlifJobState.Completed);
        job.JobId.Should().Be("job-2");
        job.MediaUrls.Should().ContainSingle().Which.Should().Be("https://glifusercontent.com/i:r/x.png");
    }

    [Fact]
    public void Парсер_ПровалДжобы_ЭтоFailedСПричиной()
    {
        var body = """
            {"result":{"structuredContent":{"jobId":"job-3","status":"failed","statusMessage":"model unavailable"}}}
            """;

        var job = GlifJobParser.Parse(body);

        job.State.Should().Be(GlifJobState.Failed);
        job.Error.Should().Be("model unavailable");
        job.MediaUrls.Should().BeEmpty();
    }

    [Fact]
    public void Парсер_ОшибкаJsonRpcИisError_ЭтоFailed()
    {
        GlifJobParser.Parse("""{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"job not found"}}""")
            .Should().BeEquivalentTo(new { State = GlifJobState.Failed, Error = "job not found" });

        GlifJobParser.Parse("""{"result":{"isError":true,"content":[{"type":"text","text":"boom"}]}}""")
            .State.Should().Be(GlifJobState.Failed);
    }

    [Fact]
    public void Парсер_НезнакомыйСтатус_СчитаетсяРаботойАНеПровалом()
    {
        // Набор статусов get_job_status сервером не документирован — незнакомое
        // значение не должно ронять опрос
        GlifJobParser.Parse("""{"result":{"structuredContent":{"jobId":"j","status":"queued"}}}""")
            .State.Should().Be(GlifJobState.Working);
        GlifJobParser.Parse("""{"result":{"structuredContent":{"jobId":"j","status":"нечто"}}}""")
            .State.Should().Be(GlifJobState.Working);
    }

    [Fact]
    public void Парсер_МусорИПереименованныеПоля_ДаютПустоБезИсключения()
    {
        GlifJobParser.Parse(null).Should().Be(GlifJobResult.Empty);
        GlifJobParser.Parse("").Should().Be(GlifJobResult.Empty);
        GlifJobParser.Parse("не json вовсе").Should().Be(GlifJobResult.Empty);
        GlifJobParser.Parse("[1,2,3]").Should().Be(GlifJobResult.Empty);

        // Поля переименовали — состояние неизвестно, но ничего не падает
        var renamed = GlifJobParser.Parse("""{"result":{"structuredContent":{"task":"x","state":"cooking"}}}""");
        renamed.State.Should().Be(GlifJobState.Unknown);
        renamed.JobId.Should().BeNull();
        renamed.MediaUrls.Should().BeEmpty();
    }

    [Fact]
    public void Парсер_СнейкКейсПолей_ТожеПонимается()
    {
        var body = """
            {"result":{"structuredContent":{"job_id":"job-4","project_id":"p-4","status":"working","poll_interval_seconds":7}}}
            """;

        var job = GlifJobParser.Parse(body);

        job.JobId.Should().Be("job-4");
        job.ProjectId.Should().Be("p-4");
        job.PollIntervalSeconds.Should().Be(7);
    }

    [Fact]
    public void Парсер_БезСтатусаНоСМедиа_СчитаетсяГотовым()
    {
        var body = """
            {"result":{"structuredContent":{"jobId":"job-5","media":[{"uri":"https://glifusercontent.com/i:r/z.png"}]}}}
            """;

        GlifJobParser.Parse(body).State.Should().Be(GlifJobState.Completed);
    }

    [Fact]
    public void Парсер_УдачнаяДжоба_ТянетSummaryИПустойInteraction()
    {
        // Живой формат get_job_status (2026-08-16): рядом с media приходит summary,
        // interaction у обычного прогона — null
        var body = """
            {"result":{"structuredContent":{
              "outputType":"project_update","status":"completed",
              "summary":"Here's your icon.","jobId":"job-6",
              "media":[{"url":"https://i.glifusercontent.com/i:r/a.jpg","title":"icon","kind":"image"}],
              "interaction":null}}}
            """;

        var job = GlifJobParser.Parse(body);

        job.State.Should().Be(GlifJobState.Completed);
        job.Summary.Should().Be("Here's your icon.");
        job.Interaction.Should().BeNull("interaction: null — это не вопрос");
        job.HasAgentReply.Should().BeTrue();
        job.MediaUrls.Should().ContainSingle();
    }

    [Fact]
    public void Парсер_АгентОтветилВопросомВместоКартинки_ЭтоВидноПоSummaryИInteraction()
    {
        // Джоба completed, медиа нет, зато агент переспрашивает — отличаем от пустого отказа
        var body = """
            {"result":{"structuredContent":{
              "outputType":"project_update","status":"completed","jobId":"job-7",
              "summary":"Want me to iterate on the icon style?",
              "media":[],
              "interaction":{"type":"question","question":"Flat or 3D?"}}}}
            """;

        var job = GlifJobParser.Parse(body);

        job.State.Should().Be(GlifJobState.Completed);
        job.MediaUrls.Should().BeEmpty();
        job.Summary.Should().Be("Want me to iterate on the icon style?");
        job.Interaction.Should().Be("Flat or 3D?");
        job.HasAgentReply.Should().BeTrue();
    }

    [Fact]
    public void Парсер_ПустойФиналБезОтветаАгента_ОтличаетсяОтВопроса()
    {
        var job = GlifJobParser.Parse("""{"result":{"structuredContent":{"jobId":"job-8","status":"completed","media":[]}}}""");

        job.State.Should().Be(GlifJobState.Completed);
        job.MediaUrls.Should().BeEmpty();
        job.HasAgentReply.Should().BeFalse("сказать агенту было нечего — это пустой отказ, а не вопрос");
    }

    [Fact]
    public void Парсер_SummaryИInteractionВTextБлоке_ТожеНаходятся()
    {
        var body = """
            {"result":{"content":[{"type":"text","text":"{\"jobId\":\"job-9\",\"status\":\"completed\",\"summary\":\"Need more detail\",\"interaction\":\"Which palette?\"}"}]}}
            """;

        var job = GlifJobParser.Parse(body);

        job.Summary.Should().Be("Need more detail");
        job.Interaction.Should().Be("Which palette?");
        job.HasAgentReply.Should().BeTrue();
    }

    [Fact]
    public void Парсер_ЧужаяФормаInteraction_НеРонитРазбор()
    {
        // Объект без знакомых полей — берём сырой JSON, лишь бы было что показать в логе
        var odd = GlifJobParser.Parse("""{"result":{"structuredContent":{"jobId":"j","status":"completed","interaction":{"foo":1}}}}""");
        odd.Interaction.Should().Be("""{"foo":1}""");

        // Массив/число вопросом не считаем
        GlifJobParser.Parse("""{"result":{"structuredContent":{"jobId":"j","status":"completed","interaction":[1,2]}}}""")
            .HasAgentReply.Should().BeFalse();
    }
}
