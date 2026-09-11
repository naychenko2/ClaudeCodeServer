using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Роутинг профильных исполнителей: DevOps-роль обязана резолвиться в DevopsExecutor,
// а не проваливаться в универсальный Executor (см. комментарий RoleFallback про
// порядок «профильные ДО общих»).
public class OmcPersonaRoutingTests
{
    [Theory]
    [InlineData("DevOps-инженер")]
    [InlineData("девопс")]
    [InlineData("SRE / инженер по надежности")]
    [InlineData("инфраструктурный разработчик")]
    public void EffectiveSpecialty_DevOpsРоли_НеПроваливаютсяВExecutor(string role)
    {
        var persona = new Persona { Role = role };

        OmcPersonaRouting.EffectiveSpecialty(persona)
            .Should().Be(PersonaSpecialty.DevopsExecutor,
                "профильные ключи стоят в RoleFallback раньше общих «разработчик»/«исполнитель»");
    }

    [Fact]
    public void AgentTypesFor_DevopsExecutor_НаборКакУПрофильныхИсполнителей()
    {
        OmcPersonaRouting.AgentTypesFor(PersonaSpecialty.DevopsExecutor, executorCapable: true)
            .Should().Equal("executor", "debugger", "git-master");
    }
}
