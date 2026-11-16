using System;
using System.IO;
using System.Linq;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

/// <summary>
/// Тесты IpmiSdrParser.Parse: фикстура — живой дамп `ipmitool sdr elist full` с worker
/// (H11DSi-NT, снят помощником ccs-ipmi, отчёт Ш2d1). Правила — ADR-029 «Узел — набор
/// устройств»: температуры по имени и единицам (`degrees C`), entity только для вентиляторов.
/// Недоверенный вход: на любой текст есть результат, исключение наружу не выходит никогда.
/// </summary>
public class IpmiSdrParserTests
{
    private static string LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LocalStats", "ipmi-h11dsi-sdr.txt");
        File.Exists(path).Should().BeTrue($"фикстура не скопирована в {path}");
        return File.ReadAllText(path);
    }

    // Живой дамп (54 строки): ровно два сокета по именам, Vrms — максимум VRM своего сокета,
    // пять живых вентиляторов, плата. Остальное (вольтажи, DIMM, Battery, Physical Security) — ничего.
    [Fact]
    public void Parse_Fixture_ParsesSocketsVrmsFansBoard()
    {
        var r = IpmiSdrParser.Parse(LoadFixture());
        r.Should().NotBeNull();

        // Номера сокетов — из имён CPU1/CPU2 (instance 3.1/3.2 не используются).
        r!.SocketTempsC.Should().Equal(new int?[] { 49, 35 });
        // Сокет 1: max(47,45,46,47); сокет 2: max(50,39,49,38) — id в выводе идут не по порядку.
        r.Vrms.Should().Equal(new int?[] { 47, 50 });
        // Датчика с «VRM» вне шаблонов в дампе нет.
        r.BoardVrm.Should().BeNull();
        // FAN1/FAN2/FANA — `ns` — отсекаются; порядок — по строкам вывода.
        r.Fans.Should().HaveCount(5);
        r.Fans.Select(f => f.Label).Should().Equal("FAN3", "FAN4", "FAN5", "FAN6", "FANB");
        r.Fans.Select(f => f.Rpm).Should().Equal(600, 900, 800, 900, 900);
        r.BoardTempC.Should().Be(27);
        r.PeripheralTempC.Should().Be(37);
    }

    // P1_VDDCR висит на entity 3, как температуры сокетов, но это вольты: ни сокета, ни VRM.
    // Мутация «сокет по entity 3 вместо имени» ломает этот тест и тест на фикстуре (4 «сокета»).
    [Fact]
    public void Parse_VoltageOnEntity3_AddsNoSocketNoVrm()
    {
        var r = IpmiSdrParser.Parse("P1_VDDCR         | 34h | ok  |  3.3 | 1.08 Volts");
        r.Should().NotBeNull();
        r!.SocketTempsC.Should().BeEmpty();
        r.Vrms.Should().BeEmpty();
        r.BoardVrm.Should().BeNull();
        r.Fans.Should().BeEmpty();
    }

    // VRM с именем вне двух шаблонов — на плату (максимум), сокету не приписывается.
    // Признак — шаблон имени (начинается с VRM, заканчивается «Temp»), а не подстрока: имя должно
    // именно им быть. Мутация «по подстроке Contains("VRM")» краснит тест ниже.
    [Fact]
    public void Parse_VrmOutsidePatterns_BoardVrm_NotSocket()
    {
        var r = IpmiSdrParser.Parse("VRMX9 Temp     | 90h | ok  |  8.9 | 70 degrees C");
        r.Should().NotBeNull();
        r!.BoardVrm.Should().Be(70);
        r.SocketTempsC.Should().BeEmpty();
        r.Vrms.Should().BeEmpty();
    }

    // «VRM» ВНУТРИ имени — не VRM-датчик: «NVRM…», «xvrm», «CPU VRMX» — не начинаются с VRM,
    // на плату не идут (Р-Ш2д3). Мутация «name.Contains("VRM")» краснит этот тест на всех трёх
    // случаях (BoardVrm стал бы 70).
    [Theory]
    [InlineData("NVRM1 Temp     | 91h | ok  |  8.10 | 70 degrees C")]
    [InlineData("xvrm Temp      | 92h | ok  |  8.11 | 70 degrees C")]
    [InlineData("CPU VRMX Temp  | 93h | ok  |  8.12 | 70 degrees C")]
    public void Parse_VrmAsSubstring_NotBoardVrm(string line)
    {
        var r = IpmiSdrParser.Parse(line);
        r.Should().NotBeNull();
        r!.BoardVrm.Should().BeNull();
        r.SocketTempsC.Should().BeEmpty();
        r.Vrms.Should().BeEmpty();
    }

    // Все четыре VRM сокета `ns` — Vrms[0] = null, но сокет, опознанный по имени, остаётся
    // в модели (длина массива до максимального распознанного индекса).
    [Fact]
    public void Parse_SocketVrmsAllNoReading_VrmNull()
    {
        var sdr = string.Join('\n',
            "VRMCpu1 Temp     | 10h | ns  |  8.1 | No Reading",
            "VRMSoc1 Temp     | 12h | ns  |  8.3 | No Reading",
            "VRMP1ABCD Temp   | 14h | ns  |  8.5 | No Reading",
            "VRMP1EFGH Temp   | 16h | ns  |  8.6 | No Reading");
        var r = IpmiSdrParser.Parse(sdr);
        r.Should().NotBeNull();
        r!.Vrms.Should().HaveCount(1);
        r.Vrms[0].Should().BeNull();
        r.BoardVrm.Should().BeNull();
    }

    // В Fans — только entity 29 со статусом ok и RPM > 0: ns/nc/uc и 0 RPM отсекаются.
    // Мутация «вентиляторы любого статуса» (убрать проверку статуса) делает тест красным.
    [Fact]
    public void Parse_FansOnlyOkPositiveRpm()
    {
        var sdr = string.Join('\n',
            "FAN1             | 41h | ns  | 29.1 | No Reading",
            "FAN2             | 42h | nc  | 29.2 | 800 RPM",
            "FAN3             | 43h | ok  | 29.3 | 0 RPM",
            "FAN4             | 44h | uc  | 29.4 | 900 RPM",
            "FAN5             | 45h | ok  | 29.5 | 700 RPM");
        var r = IpmiSdrParser.Parse(sdr);
        r.Should().NotBeNull();
        r!.Fans.Select(f => f.Label).Should().Equal("FAN5");
        r.Fans.Single().Rpm.Should().Be(700);
    }

    // Строка с пустым значением после последнего `|` (`Chassis Intru` в живом дампе) — без исключения.
    [Fact]
    public void Parse_EmptyLastField_NoThrow()
    {
        var act = () => IpmiSdrParser.Parse("Chassis Intru    | AAh | ok  | 23.1 | ");
        act.Should().NotThrow();
        var r = act();
        r!.SocketTempsC.Should().BeEmpty();
        r.Fans.Should().BeEmpty();
        r.BoardTempC.Should().BeNull();
    }

    // Плата — по имени, не по entity: в entity 7 на H11DSi 9 из 12 записей — вольтажи.
    [Fact]
    public void Parse_BoardTempsByName_IgnoreEntity()
    {
        var sdr = string.Join('\n',
            "System Temp      | 0Bh | ok  |  7.1 | 27 degrees C",
            "Peripheral Temp  | 0Ch | ok  |  7.2 | 37 degrees C",
            "12V              | 30h | ok  |  7.17 | 12.04 Volts");
        var r = IpmiSdrParser.Parse(sdr);
        r!.BoardTempC.Should().Be(27);
        r.PeripheralTempC.Should().Be(37);
    }

    [Fact]
    public void Parse_NullOrBlank_ReturnsNull()
    {
        IpmiSdrParser.Parse(null).Should().BeNull();
        IpmiSdrParser.Parse("").Should().BeNull();
        IpmiSdrParser.Parse("   \n\n\t ").Should().BeNull();
    }

    // 200 случайных строк (фиксированный seed): ни одна не должна бросить.
    [Fact]
    public void Parse_RandomGarbage_NoThrow()
    {
        var rnd = new Random(20261116);
        const string alphabet = "abcXYZ019 |.,-\t\rdegreesCVoltsRPMNoReading:TempCPUVRMFAN";
        var lines = Enumerable.Range(0, 200).Select(_ =>
        {
            var len = rnd.Next(0, 80);
            return string.Concat(Enumerable.Range(0, len).Select(_ => alphabet[rnd.Next(alphabet.Length)]));
        });
        var sdr = string.Join('\n', lines);
        var act = () => IpmiSdrParser.Parse(sdr);
        act.Should().NotThrow();
    }

    // Недоформированная строка (2 поля) и строка с лишними полями (9) — пропускаются, без исключения.
    [Fact]
    public void Parse_TwoAndNineFieldLines_SkippedNoThrow()
    {
        var two = IpmiSdrParser.Parse("CPU1 Temp | 49 degrees C");
        two!.SocketTempsC.Should().BeEmpty();

        var nine = IpmiSdrParser.Parse("junk | a | b | c | d | e | f | g | h");
        nine!.SocketTempsC.Should().BeEmpty();
        nine.Vrms.Should().BeEmpty();
        nine.Fans.Should().BeEmpty();
        nine.BoardVrm.Should().BeNull();
    }

    // Номер сокета вне 1..8 — строка пропускается (длины массивов ограничены).
    [Theory]
    [InlineData("CPU99 Temp     | 01h | ok  |  3.1 | 60 degrees C")]
    [InlineData("CPU0 Temp      | 01h | ok  |  3.1 | 60 degrees C")]
    public void Parse_SocketNumberOutOfRange_LineSkipped(string line)
    {
        var r = IpmiSdrParser.Parse(line);
        r!.SocketTempsC.Should().BeEmpty();
        r.Vrms.Should().BeEmpty();
    }

    // Температура вне −50…200 → null в поле; сокет по имени остаётся распознанным.
    [Theory]
    [InlineData("CPU1 Temp      | 01h | ok  |  3.1 | 999 degrees C")]
    [InlineData("CPU1 Temp      | 01h | ok  |  3.1 | -51 degrees C")]
    public void Parse_TemperatureOutOfRange_NullInField(string line)
    {
        var r = IpmiSdrParser.Parse(line);
        r!.SocketTempsC.Should().Equal(new int?[] { null });
    }

    // Потолок строк закреплён с обеих сторон: 512-я строка разбирается, 513-я — нет. Признак
    // различим по значению: строки 1..511 дают сокету 1 значение 100, 512-я — 120, последующие — 150
    // (разбор берёт максимум). Результат 120 — разобраны ровно первые 512. 100 — потолок НИЖЕ 512
    // (512-я отброшена); 150 — потолок не работает (разобрана 513-я). Мутация MaxLines 512→511
    // краснит этот тест на всех трёх случаях.
    [Theory]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(10_000)]
    public void Parse_TooManyLines_OnlyFirst512Parsed(int total)
    {
        var sdr = string.Join('\n', Enumerable.Range(1, total)
            .Select(n => $"CPU1 Temp      | 01h | ok  |  3.1 | {(n < 512 ? 100 : n == 512 ? 120 : 150)} degrees C"));
        var r = IpmiSdrParser.Parse(sdr);
        r!.SocketTempsC.Should().Equal(new int?[] { 120 });
    }
}
