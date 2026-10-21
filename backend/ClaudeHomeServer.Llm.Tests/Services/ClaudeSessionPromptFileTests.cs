using System.Text;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Системный промпт хода файлом (--append-system-prompt-file) вместо аргумента командной
// строки: лимит 32 767 символов перестаёт зависеть от размера промпта (инцидент 21.10).
public class ClaudeSessionPromptFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ClaudeSessionPromptFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly string[] TinyArgs = ["--print", "--input-format", "stream-json"];

    private static int FakeLocalLength(IReadOnlyList<string> finalArgs)
    {
        var total = "claude".Length;
        foreach (var a in finalArgs) total += TurnPromptAssembler.ArgCost(a);
        return total;
    }

    private static PromptSectionDto Sec(string key, int size) => new(key, key, new string('a', size), "system");

    [Fact]
    public void Проводка_ПишетФайлСPromptИДобавляетФлагФайла()
    {
        var args = new List<string> { "--print" };
        const string prompt = "Правила\n\nСлой «персоны» — юникод";

        var path = ClaudeSession.PrepareTurnPromptFile(prompt, _dir, p => "/rt" + p, args);

        args.Should().NotContain("--append-system-prompt");
        var i = args.IndexOf("--append-system-prompt-file");
        i.Should().BeGreaterThanOrEqualTo(0);
        args[i + 1].Should().Be("/rt" + path, "в args едет путь среды того же файла");
        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 без BOM");
        Encoding.UTF8.GetString(bytes).Should().Be(prompt);
        Path.GetFileName(path).Should().MatchRegex("^claude-prompt-[0-9a-f]{32}\\.md$");
    }

    [Fact]
    public void Сигнатура_НеЗависитОтПутиИТекстаПромпта()
    {
        string Sig(string prompt)
        {
            var args = new List<string> { "--print", "--model", "opus" };
            ClaudeSession.PrepareTurnPromptFile(prompt, _dir, p => p, args);
            return ClaudeSession.BuildLaunchSignature(
                args, "tasks", "", new Dictionary<string, string>(), null);
        }

        Sig("один").Should().Be(Sig("один"), "путь Guid не должен мерцать в сигнатуре");
        Sig("один").Should().Be(Sig("другой"),
            "нестабильные секции промпта меняются каждый ход и не должны перезапускать процесс");
    }

    [Fact]
    public void Бюджет_Промпт60000_ВФайловомРежимеНеОтказывает()
    {
        var sections = new[] { Sec("big", 60_000) };

        var file = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, FakeLocalLength,
            promptFileLimit: TurnPromptAssembler.PromptFileLimit);
        var inline = TurnPromptAssembler.ApplyBudget(sections, null, TinyArgs, FakeLocalLength);

        file.Overflowed.Should().BeFalse();
        file.TruncatedSections.Should().BeEmpty();
        file.CombinedPrompt.Length.Should().Be(60_000);
        inline.Overflowed.Should().BeTrue("прежний путь аргументом отказывает как раньше");
    }

    [Fact]
    public void Бюджет_ПревышениеПотолкаФайла_РежетПоПорядкуИПотомОтказывает()
    {
        var limit = TurnPromptAssembler.PromptFileLimit;

        // Срезка помогает: нестабильный code-graph выкидывается первым, остаток влезает
        var fits = TurnPromptAssembler.ApplyBudget(
            [Sec("base", 100_000), Sec("code-graph", 150_000)], null, TinyArgs, FakeLocalLength,
            promptFileLimit: limit);
        fits.Overflowed.Should().BeFalse();
        fits.Sections.Select(s => s.Key).Should().NotContain("code-graph");
        fits.TruncatedSections.Should().ContainSingle(t => t.Key == "(truncated:code-graph)");

        // Срезка не помогает: стабильная часть сама больше потолка → отказ
        var over = TurnPromptAssembler.ApplyBudget(
            [Sec("base", limit + 1)], null, TinyArgs, FakeLocalLength, promptFileLimit: limit);
        over.Overflowed.Should().BeTrue();
        over.PromptChars.Should().Be(limit + 1);

        // Ровно на границе — ещё можно
        TurnPromptAssembler.ApplyBudget(
            [Sec("base", limit)], null, TinyArgs, FakeLocalLength, promptFileLimit: limit)
            .Overflowed.Should().BeFalse();
    }

    [Fact]
    public void Бюджет_ПустойНабор_ВФайловомРежимеНеОтказывает()
    {
        var r = TurnPromptAssembler.ApplyBudget([], null, TinyArgs, FakeLocalLength,
            promptFileLimit: TurnPromptAssembler.PromptFileLimit);

        r.Overflowed.Should().BeFalse();
        r.CombinedPrompt.Should().BeEmpty();
    }
}
