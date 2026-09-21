using System.Reflection;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Llm;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Замер места <c>persona-voice</c> на живой модели (Батарея II, ось A).
///
/// НАХОДКА ПРО ШОВ (задача просила описать её отдельно). Это единственное из семи мест,
/// которое НЕ подключается двумя делегатами: у него нет сервиса. Промпт строит приватный
/// <c>PersonasController.BuildVoicePrompt</c>, а вызывает раннер сам экшен контроллера,
/// поэтому «прогнать кейс через настоящий сервис» упирается в выбор из двух зол:
///
///  • поднять HTTP-стенд (WebApplicationFactory) и постучаться в <c>POST
///    /api/personas/ai/voice</c> — путь продуктовый целиком, но в трассу кейса попали бы
///    ЧУЖИЕ вызовы раннера: у поднятого приложения есть фоновые места, и замер времени
///    после первого же такого вызова стал бы неправдой;
///  • взять продуктовый промпт рефлексией — что и сделано ниже.
///
/// Копии промпта в тесте нет ни в одном из вариантов: копия разошлась бы с продуктом на
/// первой же правке, и замер мерил бы несуществующее место. Рефлексия хрупка иначе —
/// переименование метода ломает тест, — но ломает ГРОМКО и с понятным диагнозом
/// (проверка ниже), а не тихо, как разошедшаяся копия.
///
/// Вывод для харнесса: шов «место = сервис + ICheapTextRunner» держится на том, что у
/// места есть сервис. Родись такое место сегодня — его стоило бы вынести из контроллера
/// в сервис, как у задач и заметок; менять ради него харнесс смысла нет.
///
/// ЗАМЕР, А НЕ ГЕЙТ: порогов валидности тест не утверждает.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class PersonaVoiceLocalBenchTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Замер_подбора_голоса_персоны()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.PersonaVoice);
        var buildPrompt = ProductionPromptBuilder();

        var report = await LocalBenchLoop.RunAsync(bank, runner,
            invoke: async c =>
            {
                var request = new AiVoiceRequest(
                    Name: c.Input,
                    Role: TaskClassifyLocalBenchTests.Text(c.Context, "role"),
                    Description: TaskClassifyLocalBenchTests.Text(c.Context, "description"),
                    Character: TaskClassifyLocalBenchTests.Text(c.Context, "character"),
                    Tone: TaskClassifyLocalBenchTests.Text(c.Context, "tone"));
                var prompt = (string)buildPrompt.Invoke(null, [request])!;

                // Вызов повторяет строку экшена AiVoice: место, промпт, модель-фолбэк.
                var raw = await runner.RunAsync(LocalActionCatalog.PersonaVoice, prompt, "haiku");

                // Что место выдало бы человеку: продуктовый разбор ответа плюс эталон —
                // голос, который у персоны выбран на самом деле.
                var picked = PersonaVoiceOracle.Picked(raw);
                var expected = TaskClassifyLocalBenchTests.Text(c.Expect, "voice") ?? "-";
                var shown = picked is null
                    ? "голос не выбран"
                    : picked.Value.Voice + (picked.Value.Role is null ? "" : " " + picked.Value.Role);
                return $"→ {shown}; у персоны {expected}";
            },
            judge: (_, turns) => PersonaVoiceOracle.Violation(turns.Last.RawAnswer),
            output,
            reference: PlaceReferences.PersonaVoice);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }

    // Продуктовый сборщик промпта места. Приватен — иного способа взять его без копии нет;
    // пропажа метода валит замер сразу и с объяснением, а не отдаёт тихо неверные числа.
    private static MethodInfo ProductionPromptBuilder()
    {
        var method = typeof(PersonasController).GetMethod("BuildVoicePrompt",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(method is not null,
            "PersonasController.BuildVoicePrompt не найден: промпт места persona-voice "
            + "переехал или переименован. Замер обязан идти ПРОДУКТОВЫМ промптом — "
            + "почините ссылку, копию промпта в тест не заводите.");
        return method!;
    }
}
