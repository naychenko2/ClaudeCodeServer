using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Smoke-тест «форвардер сработал»: четыре пары, которые раньше регистрировались дублями
// (`AddSingleton<I, C>()` без фабрики → второй экземпляр C), теперь через фабрику
// `sp.GetRequiredService<C>()` указывают на ТОТ ЖЕ объект, что и резолв самого C.
//
// На живом проде поломка 2026-09-13/14: экземпляр TaskManager за `ITaskStatusReader`
// (его тянул `TaskStatusTriggerSource` → `PersonaAutomationService`) перезаписывал
// статические резолверы `Session.*` и иерархия чатов для задач, созданных после старта
// процесса, не резолвилась. `ReferenceEquals` тут — приёмочный критерий фикса.
//
// Все четыре проверки обязательны: задача зафиксировала только TaskManager как «сломанный
// на проде», но PersonaManager, UserHomeResolver и JwtValidatorGateway страдают от того же
// паттерна, и без явной проверки следующий регресс поймает только один из четырёх.
// JwtValidatorGateway закрывает два интерфейса (`IUserTokenValidator` + `IPreviewTokenValidator`)
// из одного адаптера — здесь три резолва и один ассерт на ReferenceEquals по базовому типу.
public class SingletonForwarderIdentityTests
{
    [Fact]
    public void TaskManager_ЧерезITaskStatusReader_ТотЖеЭкземпляр()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        var direct = sp.GetRequiredService<TaskManager>();
        var viaInterface = sp.GetRequiredService<ITaskStatusReader>();

        ReferenceEquals(direct, viaInterface).Should().BeTrue(
            "иначе второй экземпляр `TaskManager` перезаписывает статические резолверы " +
            "`Session.*` и иерархия чатов для новых задач мертва — поломка 2026-09-13/14");
    }

    [Fact]
    public void PersonaManager_ЧерезIPersonaHandleResolver_ТотЖеЭкземпляр()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        var direct = sp.GetRequiredService<PersonaManager>();
        var viaInterface = sp.GetRequiredService<IPersonaHandleResolver>();

        ReferenceEquals(direct, viaInterface).Should().BeTrue(
            "иначе второй `PersonaManager` со своим стором — персона, созданная после старта, " +
            "для резолвера handle не существует");
    }

    [Fact]
    public void UserHomeResolver_ЧерезIHomePathResolver_ТотЖеЭкземпляр()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        var direct = sp.GetRequiredService<UserHomeResolver>();
        var viaInterface = sp.GetRequiredService<IHomePathResolver>();

        ReferenceEquals(direct, viaInterface).Should().BeTrue(
            "иначе второй `UserHomeResolver` со своим кешем домашних папок пользователей");
    }

    [Fact]
    public void JwtValidatorGateway_ЧерезОбаИнтерфейса_ТотЖеЭкземпляр()
    {
        using var factory = new TestWebApplicationFactory();
        var sp = factory.Services;

        var direct = sp.GetRequiredService<JwtValidatorGateway>();
        var viaUser = sp.GetRequiredService<IUserTokenValidator>();
        var viaPreview = sp.GetRequiredService<IPreviewTokenValidator>();

        ReferenceEquals(direct, viaUser).Should().BeTrue(
            "иначе второй `JwtValidatorGateway` со своим делегатом на `JwtService` — " +
            "валидация пользовательского токена идёт по другому пути, чем прямой резолв");
        ReferenceEquals(direct, viaPreview).Should().BeTrue(
            "иначе второй `JwtValidatorGateway` со своим делегатом на `JwtService` — " +
            "валидация preview-токена идёт по другому пути, чем прямой резолв");
    }
}
