using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Валидатор значения маршрута для формы «конкретная модель» (ADR-009 §1, форма 8).
// Утверждённые тексты ошибок — docs/features/model-route-format-validation.md.
// Контракт: обычная модель / модель без поставщика (есть только как direct:) / неизвестная.
//
// Через HTTP кейс noProvider не смоделировать: каталог тестового хоста не содержит
// direct-моделей (все провайдеры с пустым ApiKey), поэтому классификатор тестируется напрямую
// как pure-функция по списку Value каталога.
public class LocalActionRouteValidatorTests
{
    // Каталог «как на проде»: обычные модели провайдеров + direct:-модели агрегатора.
    // MiniMax-M3 здесь — только как direct: (именно этот дефект и ловим).
    private static readonly string[] Catalog =
    [
        "opus", "haiku", "glm-5.2", "glm-5.2[1m]",
        "direct:MiniMax-M3", "direct:nvidia/nemotron-3-super-120b-a12b:free",
    ];

    // --- валидные: обычная модель (форма 8) ---

    [Fact]
    public void ОбычнаяМодельВКаталоге_Годится()
    {
        // Голое имя обычной модели — допустимое значение (ADR-009 §1, форма 8).
        // Примеры валидных: claude-haiku-4-5-20251001, glm-5.2[1m].
        LocalActionRouteValidator.ClassifyModelRoute("glm-5.2", Catalog).Should().BeNull();
        LocalActionRouteValidator.ClassifyModelRoute("glm-5.2[1m]", Catalog).Should().BeNull();
        LocalActionRouteValidator.ClassifyModelRoute("haiku", Catalog).Should().BeNull();
    }

    [Fact]
    public void ОбычнаяМодель_РегистрНеВажен()
    {
        // Сравнение по каталогу — OrdinalIgnoreCase: GLM-5.2 та же модель, что glm-5.2.
        LocalActionRouteValidator.ClassifyModelRoute("GLM-5.2", Catalog).Should().BeNull();
        LocalActionRouteValidator.ClassifyModelRoute("Opus", Catalog).Should().BeNull();
    }

    // --- валидные: модель прямого вызова с префиксом ---

    [Fact]
    public void DirectМодельСПрефиксом_Годится()
    {
        // direct:<id> — подвид формы 8 (ADR-009 §1): значение в каталоге целиком.
        LocalActionRouteValidator.ClassifyModelRoute("direct:MiniMax-M3", Catalog).Should().BeNull();
    }

    // --- невалидные: модель без поставщика (дефект с прода) ---

    [Fact]
    public void ГолоеИмяDirectМодели_БезПоставщика()
    {
        // Главный кейс дефекта: MiniMax-M3 записан без direct:, но в каталоге есть только
        // direct:MiniMax-M3. Ожидаем текст route.noProvider.api с подставленным именем.
        var verdict = LocalActionRouteValidator.ClassifyModelRoute("MiniMax-M3", Catalog);
        verdict.Should().NotBeNull();
        verdict.Should().Contain("«MiniMax-M3»");
        verdict.Should().Contain("без поставщика");
        verdict.Should().Contain("«Модели и расход»");
    }

    [Fact]
    public void ГолоеИмяDirectМодели_РегистрНеВажен()
    {
        // minimax-m3 в нижнем регистре всё равно бьётся по direct:MiniMax-M3 (OrdinalIgnoreCase).
        var verdict = LocalActionRouteValidator.ClassifyModelRoute("minimax-m3", Catalog);
        verdict.Should().NotBeNull();
        verdict.Should().Contain("без поставщика");
    }

    // --- невалидные: неизвестная модель ---

    [Fact]
    public void НеизвестнаяМодель_НееНетВовсе()
    {
        // Опечатка/чужое имя: ни голого, ни direct: в каталоге. Текст route.unknownModel.
        var verdict = LocalActionRouteValidator.ClassifyModelRoute("нет-такой-модели", Catalog);
        verdict.Should().NotBeNull();
        verdict.Should().Contain("«нет-такой-модели»");
        verdict.Should().Contain("не найдена среди доступных");
        verdict.Should().Contain("«Модели и расход»");
    }

    [Fact]
    public void ПустойКаталог_ВсеНеизвестно()
    {
        // Каталог не прогрелся / пуст: любая модель — неизвестная (fail-closed, не noProvider).
        var verdict = LocalActionRouteValidator.ClassifyModelRoute("glm-5.2", Array.Empty<string>());
        verdict.Should().NotBeNull();
        verdict.Should().Contain("не найдена среди доступных");
    }

    [Theory]
    [InlineData("strong", "модель с именем strong")] // нет tier: — не слот, а неизвестная модель
    [InlineData("tier:strongest", "неизвестная")]     // нет такого слота → форма 8 → нет в каталоге
    public void СлужебныеИменаБезПрефикса_НеизвестнаяМодель(string model, string _)
    {
        // Контрольная: значение, которое НАДО было записать со служебным префиксом, но без него
        // оно не становится ни слотом, ни пресетом — это «модель с таким именем», которой нет.
        // (Сами tier:/preset: до валидатора не доходят — их разбирает контроллер раньше.)
        var verdict = LocalActionRouteValidator.ClassifyModelRoute(model, Catalog);
        verdict.Should().NotBeNull();
        verdict.Should().Contain("не найдена среди доступных");
    }
}
