using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Ложное «не хватило памяти» при остановке сервера (задача a5d2cb1f): DisposeAsync убивает
// дерево CLI (SIGKILL, exit=137), а обработчик смерти, не отличая своё убийство от oom-kill,
// отдавал TurnFailureText.ProcessKilled («не хватило памяти»). Фикс: флаг _disposing (ставится
// в начале DisposeAsync) передаётся в ProcessDeathMessage, который при нём возвращает отдельный
// текст с маркером [Process:stopped-by-server] (не [Process:killed]) — классификатор даёт None
// (не Unreachable), фолбэк модель не подменяет, ход завершается честной ошибкой. Настоящий
// oom-kill (exit=137 без dispose) — как раньше: ProcessKilled + маркер [Process:killed].
public class ClaudeSessionDisposeDeathMessageTests
{
    // _disposing — приватное поле; читаем через reflection (тот же приём, что в
    // ClaudeSessionDisposeRaceTests для _turnLock/_stdinLock). Мутация «убрать
    // _disposing = true из DisposeAsync» обязана покрасить DisposeAsync_ВыставляетФлаг.
    private static readonly FieldInfo DisposingField =
        typeof(ClaudeSession).GetField("_disposing", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // ===== Ветки ProcessDeathMessage (чистая функция — детерминированно, без жизненного цикла) =====

    // Свой Kill (DisposeAsync: остановка сервера / реанимация чата) — НЕ oom-kill: текст не
    // «не хватило памяти», маркер [Process:stopped-by-server] (не [Process:killed]): классификатор
    // даёт None (не Unreachable), фолбэк модель не подменяет, ход завершается error.
    [Fact]
    public void ProcessDeathMessage_СвойKill_ТекстОстановкиСервером_МаркерStoppedByServer()
    {
        var msg = ClaudeSession.ProcessDeathMessage(pendingControlResponse: false, exitCode: 137, disposing: true);

        msg.Text.Should().Be(TurnFailureText.ProcessStoppedByServer,
            "свой Kill не должен выдавать «не хватило памяти» — причина: сервер остановил процесс");
        msg.Text.Should().NotBe(TurnFailureText.ProcessKilled,
            "ложное «не хватило памяти» — ровно то, что чинит задача");
        msg.Details.Should().NotBeNull("маркер [Process:stopped-by-server] — первый токен Details");
        msg.Details!.Should().StartWith(TurnErrorClassifier.ProcessStoppedByServerMarker,
            "маркер первым токеном Details: классификатор даёт None, не Unreachable");
        msg.Details.Should().NotContain(TurnErrorClassifier.ProcessKilledMarker,
            "не [Process:killed]: это не oom-kill, а свой Kill сервера");
    }

    // Настоящий oom-kill (exit=137, dispose не было): как раньше — ProcessKilled + маркер,
    // чтобы классификатор не ушёл в Unreachable и не крутил фолбэк по живым провайдерам.
    [Fact]
    public void ProcessDeathMessage_НастоящийOomKill_ТекстНехваткиПамяти_СМаркером()
    {
        var msg = ClaudeSession.ProcessDeathMessage(pendingControlResponse: false, exitCode: 137, disposing: false);

        msg.Text.Should().Be(TurnFailureText.ProcessKilled);
        msg.Details.Should().NotBeNull();
        msg.Details!.Should().Contain(TurnErrorClassifier.ProcessKilledMarker,
            "маркер нужен: без него oom-kill ушёл бы в Unreachable и фолбэк жёг пары впустую");
    }

    // Регрессия: pending control_response (ожидание разрешения/вопроса) имеет приоритет над
    // кодом выхода — текст про ожидание разрешения, а не про смерть от сигнала.
    [Fact]
    public void ProcessDeathMessage_PendingControl_ПриоритетНадКодомВыхода()
    {
        var msg = ClaudeSession.ProcessDeathMessage(pendingControlResponse: true, exitCode: 137, disposing: false);

        msg.Text.Should().Be("Процесс модели завершился во время ожидания разрешения — ход прерван");
        msg.Text.Should().NotBe(TurnFailureText.ProcessKilled);
    }

    // ===== Файл аргумента CLI не найден (инцидент 2026-10-16): производящая сторона =====
    // ProcessDeathMessage — чистая функция: stderr «file not found» → маркер [Local:file-missing]
    // в Details (классификатор → LocalFileMissing). Мутация «стереть stderrTail.Contains("file
    // not found")» (условие становится «stderrTail is not null») обязана покрасить тест
    // ..._StderrБезFileNotFound_БезМаркера: любой ненулевой stderr ложно ставил бы маркер.

    // «file not found» в stderr → маркер [Local:file-missing] первым токеном Details.
    [Fact]
    public void ProcessDeathMessage_FileNotFoundВStderr_МаркерLocalFileMissing()
    {
        var msg = ClaudeSession.ProcessDeathMessage(
            pendingControlResponse: false, exitCode: 1, disposing: false,
            stderrTail: "Error: --agents file not found: /tmp/claude-agents-abc123.json");

        msg.Details.Should().NotBeNull("маркер [Local:file-missing] — первый токен Details");
        msg.Details!.Should().StartWith(TurnErrorClassifier.LocalFileMissingMarker,
            "«file not found» в stderr — локальная гонка per-run файла, маркер ставится");
        msg.Details.Should().Contain("file not found", "сырой stderr после маркера не теряется");
    }

    // null в stderr → маркера нет (процесс умер без stderr — обычный обрыв, Unreachable).
    [Fact]
    public void ProcessDeathMessage_NullStderr_БезМаркера()
    {
        var msg = ClaudeSession.ProcessDeathMessage(
            pendingControlResponse: false, exitCode: 1, disposing: false, stderrTail: null);

        msg.Details.Should().BeNull(
            "null в stderr — маркера нет: это не «file not found», а обычный обрыв");
    }

    // Мутация «стереть .Contains("file not found")»: условие становится «stderrTail is not null»,
    // и ЛЮБОЙ ненулевой stderr ложно ставил бы маркер. Этот тест её ловит: stderr без
    // «file not found» → маркера нет.
    [Fact]
    public void ProcessDeathMessage_StderrБезFileNotFound_БезМаркера()
    {
        var msg = ClaudeSession.ProcessDeathMessage(
            pendingControlResponse: false, exitCode: 1, disposing: false,
            stderrTail: "some other startup error, no file involved");

        msg.Details.Should().BeNull(
            "«file not found» в stderr отсутствует — маркер LocalFileMissing не ставится");
    }

    // Ход уже получил события (turnGotEvent=true) — смерть посреди хода, а не на старте.
    // Посторонний «file not found» в stderr не делает это локальной гонкой: текст
    // «Ход не стартовал» (TurnFailureText.LocalFileMissing) на уже начавшемся ходе лгал бы.
    // Маркер ставится ТОЛЬКО при !turnGotEvent — тест ловит мутацию «убрать !turnGotEvent».
    [Fact]
    public void ProcessDeathMessage_ХодПолучилСобытия_ФайлNotFoundВStderr_БезМаркера()
    {
        var msg = ClaudeSession.ProcessDeathMessage(
            pendingControlResponse: false, exitCode: 1, disposing: false,
            stderrTail: "Error: --agents file not found: /tmp/claude-agents-abc123.json",
            turnGotEvent: true);

        msg.Details.Should().BeNull(
            "ход уже стартовал (turnGotEvent=true) — «file not found» в stderr посторонний, маркера нет");
    }

    // ===== DisposeAsync ставит флаг (ловит мутацию «убрать _disposing = true») =====

    [Fact]
    public async Task DisposeAsync_ВыставляетФлагСвоегоУбийства()
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null);
        var session = new ClaudeSession(new Session(), context);

        ((bool)DisposingField.GetValue(session)!).Should().BeFalse("флаг ставится только в DisposeAsync, не в конструкторе");
        await session.DisposeAsync();
        ((bool)DisposingField.GetValue(session)!).Should().BeTrue(
            "DisposeAsync обязан выставить _disposing ДО Kill дерева — иначе свой Kill неотличим от oom-kill");
    }
}
