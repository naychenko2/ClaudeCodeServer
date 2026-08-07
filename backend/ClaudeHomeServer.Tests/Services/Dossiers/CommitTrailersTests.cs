using ClaudeHomeServer.Services.Dossiers;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.Dossiers;

// CommitTrailers (ADR-004 §1): разбор CCS-Session/CCS-Task из сообщения коммита. Неймспейс
// CCS- защищает от чужой конвенции «Task: JIRA-123»; при squash берётся последнее совпадение.
public class CommitTrailersTests
{
    [Fact]
    public void ИзвлекаетCCSSession()
    {
        var msg = "feat(x): правка\n\nCCS-Session: 8f2a1c34-abcd-1234-5678-90abcdef1234\nCo-Authored-By: X";

        CommitTrailers.ExtractSessionId(msg).Should().Be("8f2a1c34-abcd-1234-5678-90abcdef1234");
    }

    [Fact]
    public void ИзвлекаетCCSTask()
    {
        var msg = "feat(x): правка\n\nCCS-Session: abc123\nCCS-Task: cfad4026-788a-47f8-8212-ab016a5035b5";

        CommitTrailers.ExtractTaskId(msg).Should().Be("cfad4026-788a-47f8-8212-ab016a5035b5");
    }

    // При squash конкатенация несёт несколько одинаковых трейлеров; сохраняемый (последний в теле)
    // обычно ниже поглощённых — берём последнее совпадение, чтобы привязка не уехала на черновик.
    [Fact]
    public void НесколькоТрейлеров_БерётПоследнее()
    {
        var msg = "squash\n\nCCS-Session: first-session-id\nCCS-Session: final-session-id";

        CommitTrailers.ExtractSessionId(msg).Should().Be("final-session-id");
    }

    // Неймспейс (минор-правка №5): голый «Task:» — распространённая внешняя конвенция ссылки на
    // трекер; чужое значение по ней не должно даже доходить до разбора.
    [Fact]
    public void ГолыйTask_БезНеймспейса_НеМатчится()
    {
        var msg = "fix: правка\n\nTask: JIRA-123\nChat-Session: old-style-id";

        CommitTrailers.ExtractTaskId(msg).Should().BeNull();
        CommitTrailers.ExtractSessionId(msg).Should().BeNull();
    }

    [Fact]
    public void НетТрейлера_Null()
    {
        var msg = "feat(x): обычный коммит без служебных трейлеров";

        CommitTrailers.ExtractSessionId(msg).Should().BeNull();
        CommitTrailers.ExtractTaskId(msg).Should().BeNull();
    }

    [Fact]
    public void ПустоеСообщение_Null()
    {
        CommitTrailers.ExtractSessionId("").Should().BeNull();
        CommitTrailers.ExtractSessionId(null!).Should().BeNull();
    }
}
