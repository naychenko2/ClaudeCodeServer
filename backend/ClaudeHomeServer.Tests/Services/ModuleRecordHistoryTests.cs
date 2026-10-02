using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// История чата и записи модулей (ADR-019 §2, §6): module_record переживает круг «запись →
// чтение» с произвольным data, а записи чатов картинки v2 (image_launch, image_file_moved,
// imageSnapshot) читаются и после сноса v2 — golden-файл ниже написан руками в формате
// history.json, а не сериализатором, иначе он проверял бы сам себя.
public class ModuleRecordHistoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "module_record_" + Guid.NewGuid().ToString("N"));
    private readonly ChatHistoryService _history;

    public ModuleRecordHistoryTests()
    {
        Directory.CreateDirectory(_tempDir);
        _history = new ChatHistoryService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_tempDir, "projects.json") })
            .Build());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const string Golden = """
        [
          {"kind":"user_message","text":"поправь фон","imageSnapshot":{"revision":"r3","attached":true},"timestamp":1759000000000},
          {"kind":"image_launch","by":"human","prompt":"убрать провод","provider":"fal","model":"fal-ai/flux-fill","count":2,
           "estimate":{"amount":0.1,"unit":"usd","approx":true,"source":"catalog"},"jobId":"j1","timestamp":1759000000100},
          {"kind":"image_file_moved","from":"images/hero.png","to":"images/hero-2.png","timestamp":1759000000200},
          {"kind":"module_record","module":"imageeditor","recordType":"image_thread",
           "data":{"threadId":"t1","stackId":"s1"},"fallback":"Картинка: hero.png","timestamp":1759000000300},
          {"kind":"module_record","module":"video","recordType":"clip","data":null,"fallback":"Видео: intro.mp4"}
        ]
        """;

    private async Task<List<StoredMessage>> LoadGolden()
    {
        const string csid = "golden-csid";
        await _history.SaveAsync(csid, [new StoredTextMessage("заглушка")]);
        var path = Directory.GetFiles(_tempDir, "history.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(path, Golden);
        return await _history.LoadAsync(csid);
    }

    [Fact]
    public async Task Записи_чата_картинки_v2_читаются()
    {
        var history = await LoadGolden();

        history.Should().HaveCount(5, "ни одна запись не выпала из-за незнакомого kind");
        var user = history[0].Should().BeOfType<StoredUserMessage>().Subject;
        user.ImageSnapshot.Should().Be(new StoredImageSnapshot("r3", true));
        var launch = history[1].Should().BeOfType<StoredImageLaunchMessage>().Subject;
        launch.Prompt.Should().Be("убрать провод");
        launch.Estimate!.Amount.Should().Be(0.1);
        var moved = history[2].Should().BeOfType<StoredImageFileMovedMessage>().Subject;
        moved.To.Should().Be("images/hero-2.png");
    }

    [Fact]
    public async Task Запись_модуля_читается_без_самого_модуля()
    {
        var history = await LoadGolden();

        var anchor = history[3].Should().BeOfType<StoredModuleRecord>().Subject;
        anchor.Module.Should().Be("imageeditor");
        anchor.RecordType.Should().Be("image_thread");
        anchor.Data!.Value.GetProperty("threadId").GetString().Should().Be("t1");
        anchor.Fallback.Should().Be("Картинка: hero.png");
        anchor.Timestamp.Should().Be(1759000000300);

        var unknown = history[4].Should().BeOfType<StoredModuleRecord>().Subject;
        unknown.Module.Should().Be("video", "ядро не знает модулей и не отбрасывает чужие записи");
        unknown.Fallback.Should().Be("Видео: intro.mp4");
    }

    [Fact]
    public async Task Запись_модуля_переживает_круг_записи_и_чтения()
    {
        var data = JsonSerializer.SerializeToElement(new { threadId = "t9", nested = new { steps = new[] { 1, 2 } } });
        await _history.SaveAsync("round", [new StoredModuleRecord
        {
            Module = "imageeditor", RecordType = "image_launch", Data = data, Fallback = "Вы запустили: …", Timestamp = 5,
        }]);

        var raw = File.ReadAllText(Directory.GetFiles(_tempDir, "history.json", SearchOption.AllDirectories).Single());
        raw.Should().Contain("\"kind\":\"module_record\"").And.Contain("\"recordType\":\"image_launch\"");

        var loaded = (await _history.LoadAsync("round")).Should().ContainSingle().Which.Should().BeOfType<StoredModuleRecord>().Subject;
        loaded.Data!.Value.GetProperty("nested").GetProperty("steps")[1].GetInt32().Should().Be(2);
        loaded.Fallback.Should().Be("Вы запустили: …");
    }
}
