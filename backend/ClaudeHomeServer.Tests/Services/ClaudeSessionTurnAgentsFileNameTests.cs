using System.Collections.Generic;
using System.IO;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Поведенческий тест гонки за файл --agents (дефект ed581448, доработка по ревью Глеба).
//
// Суть дефекта: файл --agents пишется в BuildArgs, а при несовпадении сигнатуры запуска
// убивается прежний прогон этой же сессии, и ЕГО финализация (File.Delete) удаляла тот же
// файл, на который стартует новый процесс → exit=1 → Unreachable → фолбэк.
//
// Тест идёт через PrepareTurnAgents — ту самую проводку, которую зовёт BuildArgs (пишет файл,
// дописывает --agents в args, отдаёт вклад в сигнатуру), — и через BuildLaunchSignature на
// получившихся args. Мутация «общий путь в проводке + --agents обратно в сигнатуре» обязана
// здесь краснеть: обе сборки получат один путь, и удаление файла первой убьёт файл второй.
public class ClaudeSessionTurnAgentsFileNameTests : System.IDisposable
{
    private const string AgentsJson = """{"agents":[{"name":"denis","prompt":"p1"}]}""";
    private const string OtherAgentsJson = """{"agents":[{"name":"kira","prompt":"p2"}]}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ClaudeSessionTurnAgentsFileNameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // Одна сборка args хода: общий хвост, как у BuildArgs, плюс проводка --agents.
    private (string Path, List<string> Args, string Signature) Build(string json)
    {
        var args = new List<string> { "--print", "--output-format", "stream-json", "--model", "opus" };
        var (path, fingerprint) = ClaudeSession.PrepareTurnAgents(json, _dir, p => "/rt" + p, args);
        var signature = ClaudeSession.BuildLaunchSignature(
            args, "tasks,notes", fingerprint, new Dictionary<string, string>(), null);
        return (path, args, signature);
    }

    private static string AgentsArg(List<string> args)
    {
        var i = args.IndexOf("--agents");
        i.Should().BeGreaterThanOrEqualTo(0, "проводка обязана положить --agents в args");
        return args[i + 1];
    }

    [Fact]
    public void ДвеСборкиСОднимКонтентом_РазныеЗначенияAgentsВArgs()
    {
        var a = Build(AgentsJson);
        var b = Build(AgentsJson);

        AgentsArg(a.Args).Should().NotBe(AgentsArg(b.Args),
            "путь --agents уникален на каждую сборку, как у --mcp-config");
        AgentsArg(a.Args).Should().Be("/rt" + a.Path, "в args едет путь среды того же файла");
        File.ReadAllText(a.Path).Should().Be(AgentsJson);
    }

    [Fact]
    public void Сигнатура_СовпадаетПриТомЖеСоставе_РазличаетсяПриДругом()
    {
        var a = Build(AgentsJson);
        var b = Build(AgentsJson);
        var other = Build(OtherAgentsJson);

        b.Signature.Should().Be(a.Signature,
            "тот же состав сабагентов → ход отдаётся живому процессу, путь в сигнатуре не мерцает");
        other.Signature.Should().NotBe(a.Signature,
            "смена состава сабагентов → новая сигнатура → новый процесс");
    }

    [Fact]
    public void УдалениеФайлаПервойСборки_НеУдаляетФайлВторой()
    {
        var first = Build(AgentsJson);
        var second = Build(AgentsJson);

        // финализация убитого прогона первого хода удаляет свой файл
        File.Delete(first.Path);

        File.Exists(second.Path).Should().BeTrue(
            "финализация хода 1 не должна тронуть файл хода 2, на который стартует новый процесс");
        File.ReadAllText(second.Path).Should().Be(AgentsJson);
    }

    [Fact]
    public void ИмяФайлаНесётФорматGuid()
    {
        var name = Path.GetFileName(Build(AgentsJson).Path);
        name.Should().MatchRegex("^claude-agents-[0-9a-f]{32}\\.json$");
        Directory.GetFiles(_dir).Should().ContainSingle();
    }
}
