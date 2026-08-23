using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// Чистая логика конспектов обсуждений (ADR-004 §6): сбор ленты, путь файла в ветке,
// строка каталога LLM-мест — без git, сторов и моделей.
public class DossierDiscussionUnitTests
{
    // --- (а) лента = реплики диалога: пользователь + ассистент; протокол хода (thinking,
    // инструменты, файлы) и тексты сабагентов — шум, в конспект не идут ---

    [Fact]
    public void BuildFeed_БерётТолькоРепликиДиалога()
    {
        var messages = new StoredMessage[]
        {
            new StoredUserMessage("обсудим архитектуру конспектов"),
            new StoredThinkingMessage("думаю над вариантами"),
            new StoredTextMessage("Смотрю код экспортёра…", parentToolUseId: "toolu-1"),
            new StoredToolUseMessage { Id = "tu-1", Name = "Read" },
            new StoredTextMessage("Предлагаю конспект вместо транскрипта"),
            new StoredFileChangedMessage("a.cs", 1, 0),
        };

        var feed = DossierDiscussionService.BuildFeed(messages, 40_000);

        feed.Should().Contain("Пользователь:");
        feed.Should().Contain("обсудим архитектуру конспектов");
        feed.Should().Contain("Ассистент:");
        feed.Should().Contain("Предлагаю конспект вместо транскрипта");
        feed.Should().NotContainAny(["думаю над вариантами", "Смотрю код экспортёра"],
            "thinking и промежуточные реплики — не позиции участников обсуждения");
    }

    // --- (б) потолок ленты: переполнение режет С НАЧАЛА — развязка обсуждения ценнее завязки ---

    [Fact]
    public void BuildFeed_ПотолокОбрезаетСНачала()
    {
        var head = "ЗАВЯЗКА-ОБСУЖДЕНИЯ " + new string('а', 500);
        var tail = " РАЗВЯЗКА-ОБСУЖДЕНИЯ";
        var messages = new StoredMessage[] { new StoredUserMessage(head + tail) };

        var feed = DossierDiscussionService.BuildFeed(messages, 200);

        feed.Length.Should().BeLessOrEqualTo(200);
        feed.Should().NotContain("ЗАВЯЗКА", "при переполнении отрезается начало ленты");
        feed.Should().Contain("РАЗВЯЗКА", "хвост обсуждения — решения и итоги — сохраняется целиком");
    }

    // --- (в) путь файла конспекта: год чата, 7-символьный префикс id, транслит темы ---

    [Fact]
    public void DiscussionPath_ГодЧатаПрефиксИТранслитТемы()
    {
        var path = DossierGitExporter.DiscussionPath(2026, "0123456789abcdef", "Обсуждение архитектуры");

        path.Should().Be("discussions/2026/0123456-obsuzhdenie-arkhitektury.md");
    }

    // --- (г) место каталога: отдельная строка «Паспортов изменений», Large, без локали ---

    [Fact]
    public void Каталог_МестоКонспекта_ProfilLargeБезЛокали()
    {
        var action = LocalActionCatalog.Find(LocalActionCatalog.DiscussionDigest);

        action.Should().NotBeNull("ключ discussion-digest обязан быть в каталоге");
        action!.Group.Should().Be("Паспорта изменений");
        action.Profile.Should().Be(CheapProfile.Large);
        action.DefaultLocal.Should().BeFalse("конспект уезжает в git и наружу — дефолт claude, как Changelog");
        action.Agentic.Should().BeFalse("one-shot выжимка, не агентная сессия");
    }
}
