using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты реестра корней транскриптов — вынесены из WorkflowAgentParserTests в волне
// 4C шаг 4: реестр уехал в `Services.TranscriptRoots` (спинка), парсер остался в
// `Services.Llm.WorkflowAgentParser`. Защита от обхода каталога для REST
// (`WorkflowController:58`) — поведение `IsPathAllowed` менять нельзя.
public class TranscriptRootsTests : IDisposable
{
    private readonly string _dir;

    public TranscriptRootsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wf_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void IsPathAllowed_ПутьВнутриAllowedRoot_True()
    {
        var inside = Path.Combine(TranscriptRoots.DefaultRoot, "proj", "wf_1");
        TranscriptRoots.IsPathAllowed(inside).Should().BeTrue();
    }

    [Fact]
    public void IsPathAllowed_ПутьВнеAllowedRoot_False()
    {
        TranscriptRoots.IsPathAllowed(_dir).Should().BeFalse();
        TranscriptRoots.IsPathAllowed(Path.GetTempPath()).Should().BeFalse();
    }

    [Fact]
    public void IsPathAllowed_ПрофильПодProfilesRoot_РазрешёнТолькоProjects()
    {
        // profilesRoot передаём параметром в перегрузку, а не через статическое поле:
        // параллельный старт WebApplicationFactory в интеграционных тестах перезаписывает
        // TranscriptRoots.ProfilesRoot (Program.cs), и гонка делает проверку flaky.
        // Транскрипты любого профиля (в т.ч. подписки sub-*, созданной после старта)
        var wf = Path.Combine(_dir, "sub-my-second", "projects", "-p-x-", "sid", "subagents", "workflows", "wf_1");
        TranscriptRoots.IsPathAllowed(wf, _dir).Should().BeTrue();
        // Но не остальное содержимое профиля (креденшалы и т.п.)
        TranscriptRoots.IsPathAllowed(Path.Combine(_dir, "sub-my-second", ".credentials.json"), _dir)
            .Should().BeFalse();
        // И не сам корень с одним сегментом
        TranscriptRoots.IsPathAllowed(Path.Combine(_dir, "projects"), _dir).Should().BeFalse();
    }

    [Fact]
    public void IsPathAllowed_ProfilesRoot_TraversalНеПроходит()
    {
        var profilesRoot = Path.Combine(_dir, "profiles");
        var sneaky = Path.Combine(_dir, "profiles", "key", "..", "..", "secret", "projects", "x");
        TranscriptRoots.IsPathAllowed(sneaky, profilesRoot).Should().BeFalse();
    }
}
