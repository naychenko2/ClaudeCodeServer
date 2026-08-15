using ClaudeHomeServer.Services.Tts;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Синтез речи Yandex SpeechKit: конфигурированность и нарезка текста под лимит v1
public class YandexTtsServiceTests
{
    private static YandexTtsService Make(Dictionary<string, string?>? values = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build();
        return new YandexTtsService(Mock.Of<IHttpClientFactory>(), config,
            NullLogger<YandexTtsService>.Instance);
    }

    [Fact]
    public void IsConfigured_БезКлючаИлиFolderId_False()
    {
        Make().IsConfigured.Should().BeFalse();
        Make(new() { ["Yandex:SpeechKit:ApiKey"] = "key" }).IsConfigured
            .Should().BeFalse("без folderId запрос к SpeechKit не собрать");
        Make(new() { ["Yandex:SpeechKit:FolderId"] = "folder" }).IsConfigured.Should().BeFalse();
        Make(new()
        {
            ["Yandex:SpeechKit:ApiKey"] = "key",
            ["Yandex:SpeechKit:FolderId"] = "folder",
        }).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task Synthesize_БезКонфига_ВозвращаетNull()
    {
        (await Make().SynthesizeAsync("Привет")).Should().BeNull();
    }

    // --- SplitForSynthesis: нарезка под лимит 5000 символов на запрос ---

    [Fact]
    public void Split_КороткийТекст_ОднимКуском()
    {
        YandexTtsService.SplitForSynthesis("Привет. Как дела?", 5000)
            .Should().ContainSingle().Which.Should().Be("Привет. Как дела?");
    }

    [Fact]
    public void Split_ПустойТекст_БезКусков()
    {
        YandexTtsService.SplitForSynthesis("   ", 5000).Should().BeEmpty();
        YandexTtsService.SplitForSynthesis("", 5000).Should().BeEmpty();
    }

    [Fact]
    public void Split_ДлинныйТекст_РежетсяПоГраницамПредложений()
    {
        // Три предложения по ~40 символов, лимит 100: первые два влезают вместе, третье отдельно
        var s1 = "Первое предложение ровно про погоду дня.";
        var s2 = "Второе предложение о планах на вечер.";
        var s3 = "Третье предложение завершает рассказ.";
        var chunks = YandexTtsService.SplitForSynthesis($"{s1} {s2} {s3}", 100);

        chunks.Should().HaveCount(2);
        chunks[0].Should().Be($"{s1} {s2}");
        chunks[1].Should().Be(s3);
        chunks.Should().OnlyContain(c => c.Length <= 100);
    }

    [Fact]
    public void Split_ПредложениеДлиннееЛимита_РежетсяЖёстко()
    {
        var monster = new string('а', 250); // одно «предложение» без знаков препинания
        var chunks = YandexTtsService.SplitForSynthesis(monster, 100);

        chunks.Should().HaveCount(3);
        chunks.Should().OnlyContain(c => c.Length <= 100);
        string.Concat(chunks).Should().Be(monster, "текст не должен теряться при жёсткой нарезке");
    }

    [Fact]
    public void Split_ТекстБольшеЛимита_НичегоНеТеряет()
    {
        var text = string.Join(" ", Enumerable.Range(0, 300).Select(i => $"Предложение номер {i}."));
        text.Length.Should().BeGreaterThan(YandexTtsService.MaxCharsPerRequest);

        var chunks = YandexTtsService.SplitForSynthesis(text, YandexTtsService.MaxCharsPerRequest);

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Should().OnlyContain(c => c.Length <= YandexTtsService.MaxCharsPerRequest);
        // Склейка кусков с точностью до пробелов равна исходнику
        string.Join(" ", chunks).Should().Be(text);
    }
}
