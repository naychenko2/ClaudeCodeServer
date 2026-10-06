using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож СОСТАВА участников синка знаний в боевом DI. Тесты самих сервисов проверяют, что
// сервис реализует IKnowledgeSyncParticipant, но не то, что форвардер зарегистрирован:
// без него UserKnowledgeCascade оставляет данные сферы после удаления владельца, а
// реконсайлер не чинит упавшие документы её полки.
public class KnowledgeSyncParticipantsRegistrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public KnowledgeSyncParticipantsRegistrationTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void Participants_ВключаютПолкуСферы_ТеЖеИнстансы()
    {
        var participants = _factory.Services.GetServices<IKnowledgeSyncParticipant>().ToList();

        participants.Should().Contain(p => ReferenceEquals(p, _factory.Services.GetRequiredService<SphereMemoryService>()),
            "полка памяти сферы обязана участвовать в каскаде удаления пользователя и реконсайлере");
        participants.Should().Contain(p => ReferenceEquals(p, _factory.Services.GetRequiredService<PersonaMemoryService>()));
        participants.Should().Contain(p => ReferenceEquals(p, _factory.Services.GetRequiredService<TeamMemoryService>()));
        participants.Should().Contain(p => ReferenceEquals(p, _factory.Services.GetRequiredService<DossierStore>()));
    }
}
