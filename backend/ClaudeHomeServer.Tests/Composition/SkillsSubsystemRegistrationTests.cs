using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож подсистемы «Навыки» — заведён Этапом 3 (волна 2), когда вертикаль уехала
// в отдельную сборку `ClaudeHomeServer.Skills`. До выноса такого стража у Skills не
// было, хотя он есть у всех соседей (Video/Yandex/CodeGraph/Git/Memory и остальных);
// после физического разреза его отсутствие стало опасным: сторожа границ и юнит-тесты
// вертикали проверяют ССЫЛКИ и парсинг, но ни один из них не падает, если сборка
// перестанет подключаться к реальному DI-графу (снятая строка `new SkillsSubsystem()`
// в Program.cs, потерянный ProjectReference Main→Skills). Симптом такой поломки —
// молчаливый: приложение стартует, а SkillsController падает 500 на первом запросе.
//
// Тест живёт в `ClaudeHomeServer.Tests`, а не в `ClaudeHomeServer.Skills.Tests`,
// по прецеденту уже вынесенных Video и Yandex: стенду нужен `Program` из Main, а
// тестовая сборка вертикали намеренно не ссылается на Main (её тесты — чистый парсинг).
public class SkillsSubsystemRegistrationTests
{
    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().Build();

    [Fact]
    public void Register_SubsystemHasStableKeyAndTitle()
    {
        var subsystem = new SkillsSubsystem();

        subsystem.Key.Should().Be("skills");
        subsystem.Title.Should().Be("Навыки");
    }

    [Fact]
    public void Register_RegistersAllSkillsServices()
    {
        var services = new ServiceCollection();

        services.AddSubsystems(BuildConfig(), new SkillsSubsystem());

        // Все шесть singleton-ов вертикали: их состав — контракт подсистемы, а не
        // деталь. Уедет любой обратно в Program.cs — тест упадёт, ревью это поймает.
        services.Should().Contain(s => s.ServiceType == typeof(SkillsService));
        services.Should().Contain(s => s.ServiceType == typeof(SkillsCliService));
        services.Should().Contain(s => s.ServiceType == typeof(SkillTranslationService));
        services.Should().Contain(s => s.ServiceType == typeof(PluginSkillLocalizer));
        services.Should().Contain(s => s.ServiceType == typeof(SkillSuggestService));
        services.Should().Contain(s => s.ServiceType == typeof(SkillGenerationService));
    }

    // Сторож факта подключения в Program.cs: поднимает полный стенд через
    // `TestWebApplicationFactory<Program>` и резолвит типы Skills из РЕАЛЬНОГО DI-графа
    // (а не из изолированного ServiceCollection, как тест выше). Это единственная
    // проверка, которая ловит разрыв сборочной связи Main→Skills после выноса.
    [Fact]
    public void Program_RegistersSkillsSubsystem_ServicesResolvable()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        sp.GetRequiredService<SkillsService>();
        sp.GetRequiredService<SkillsCliService>();
        sp.GetRequiredService<SkillTranslationService>();
        sp.GetRequiredService<PluginSkillLocalizer>();
        sp.GetRequiredService<SkillSuggestService>();
        sp.GetRequiredService<SkillGenerationService>();
    }

    // Сторож швов Skills (этап 5, Llm → Skills): Llm держит ICommandExpansion (разворот
    // /skill в тексте хода) и ISkillSnapshotSource (каталог скиллов для снимка промпта)
    // как ОПЦИОНАЛЬНЫЕ аргументы ClaudeSession/LlmSessionAdapterFactory (null → молчаливая
    // деградация, не ошибка). Поэтому юнит-тесты вертикали НЕ падают, если композиционный
    // корень перестанет их резолвить: ход просто перестаёт разворачивать /skill, а снимок
    // промпта — получать секцию Skills. Ловим именно обрыв проводки: реальный DI-граф
    // обязан отдавать швы в их АДАПТЕРЫ, а не в null/no-op. Поведение разворота не
    // дублируем — оно покрыто в Skills.Tests; здесь только «правильный тип в контейнере».
    [Fact]
    public void Program_ResolvesSkillSeams_ToAdaptersNotNoOp()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        // GetRequiredService бросает, если регистрация потерялась (выпал из Program.cs);
        // is-проверка на конкретный адаптер падает, если тип подменили на no-op.
        sp.GetRequiredService<ICommandExpansion>().Should().BeOfType<CommandExpansionAdapter>();
        sp.GetRequiredService<ISkillSnapshotSource>().Should().BeOfType<SkillSnapshotSourceAdapter>();
    }
}
