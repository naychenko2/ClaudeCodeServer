using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.ProjectIcons;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Место применения «Значок проекта» в каталоге — текстовое, с выбором модели (ADR-009 §9)
public class ProjectIconCatalogTests
{
    [Fact]
    public void МестоЗначокПроекта_ЕстьВКаталоге()
    {
        var action = LocalActionCatalog.Find("project-icon");

        Assert.NotNull(action);
        Assert.Equal("Значок проекта", action.Title);
        Assert.Equal("Проекты", action.Group);
        Assert.Equal(CheapProfile.Large, action.Profile);
        Assert.False(action.DefaultLocal);
        Assert.Equal(ModelTier.Medium, LocalActionCatalog.EffectiveDefaultTier(action!));
        Assert.True(LocalActionCatalog.IsKnown("project-icon"));
    }
}


// Разбор и валидация ответа модели по контракту ADR-009: имя только из белого списка,
// пути — алфавит/форма чисел/синтаксис/габарит, взаимоисключительность видов и сборка
// SVG только на сервере. Критерий задачи: подставной ответ с сырой разметкой или именем
// вне белого списка = пустой результат, а не значок.
public class ProjectIconGlyphServiceTests
{
    // Нарисованные пути — в габарите контракта [-4, 28]: пример из ADR-009 §2.2 с
    // координатами -5/-6 его же лимиту §3.4 не удовлетворяет, здесь данные годные
    private const string ValidPathsJson =
        """{"glyphs":[{"name":"piggy-bank"},{"name":"chart-line"},{"paths":["M3 21h18","M6 21V9l6-4 6 4v12","M10 21v-4h4v4"]},{"paths":["M4 18l5-4 4 4 7-4","M16 8h4v4"]}]}""";

    [Fact]
    public void ГодныйОтвет_ДаДоЧетырёхКандидатовВперемешку()
    {
        var result = ProjectIconGlyphService.Parse(ValidPathsJson);

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
        // Виды вперемешку: два имени + два нарисованных, порядок модели сохранён
        Assert.Equal(["piggy-bank", "chart-line"],
            result.Candidates.Take(2).Select(c => c.Name));
        Assert.All(result.Candidates.Skip(2), c => Assert.False(c.IsNamed));
        Assert.All(result.Candidates.Skip(2), c => Assert.NotNull(c.Paths));
    }

    [Fact]
    public void ОтветВМаркдаунЗаборе_Разбирается()
    {
        var result = ProjectIconGlyphService.Parse("```json\n" + ValidPathsJson + "\n```");

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
    }

    [Fact]
    public void ИмяВнеБелогоСписка_ПустойРезультат()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"name":"super-kitty-icon"},{"name":"chart-line"}]}""");

        // Негодный кандидат отбрасывается, годный остаётся
        Assert.True(result.Ok);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("chart-line", candidate.Name);
    }

    [Fact]
    public void ТолькоИмяВнеБелогоСписка_ОтказПустымРезультатом()
    {
        var result = ProjectIconGlyphService.Parse("""{"glyphs":[{"name":"not-a-lucide-name"}]}""");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
        Assert.Equal("rejected", result.FailReason);
    }

    [Fact]
    public void СыраяРазметкаВОтвете_ОтказБезЗначка()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["<svg onload=alert(1)><path d='M0 0'/></svg>"]}]}""");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("""{"glyphs":[{"name":"house","paths":["M3 21h18"]}]}""")]      // оба поля сразу
    [InlineData("""{"glyphs":[{}]}""")]                                          // ни одного
    [InlineData("""{"glyphs":[{"paths":[]}]}""")]                                // пустой список путей
    [InlineData("""{"glyphs":[{"paths":["M3 21h18","M4 18l5-6","M6 6l5 5","M2 2h3","M7 7h3"]}]}""")] // больше 4 путей
    [InlineData("""{"glyphs":[{"paths":["M0 0e5 5"]}]}""")]                      // экспонента
    [InlineData("""{"glyphs":[{"paths":["M100 100h1"]}]}""")]                    // габарит: 100 вне [-4, 28]
    [InlineData("""{"glyphs":[{"paths":["L0 0h1"]}]}""")]                        // первая команда не M
    [InlineData("""{"glyphs":[{"paths":["M0 0h"]}]}""")]                         // арность H не соблюдена
    [InlineData("""{"glyphs":[{"paths":["M0 0h1.234"]}]}""")]                    // три знака после точки
    public void НегодныеКандидаты_Отбрасываются(string raw)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ПутьДлиннееЛимита_КандидатОтброшен()
    {
        // Алфавит валиден (M/l и цифры), но строка длиннее 256 символов
        var longPath = "M0 0" + new string('l', 260);
        var result = ProjectIconGlyphService.Parse(
            "{\"glyphs\":[{\"paths\":[\"" + longPath + "\"]}]}");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void НеОтданНиОдинГодный_ПорогОдин()
    {
        // Один годный из смеси с мусором — уже успех (ADR-009 §3: порог годности один)
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["<script>"]},{"name":"wallet"},{"name":"nope"}]}""");

        Assert.True(result.Ok);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("wallet", candidate.Name);
    }

    [Fact]
    public void ХвостСверхЧетырёх_Обрезается()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"name":"wallet"},{"name":"house"},{"name":"rocket"},{"name":"star"},{"name":"zap"},{"name":"bot"}]}""");

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("мусор без json")]
    [InlineData("""{"nope": 1}""")]
    [InlineData("""[{"name":"wallet"}]""")]   // массив вместо объекта
    public void НеJson_ОтказBadJson(string? raw)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Equal("bad-json", result.FailReason);
    }

    [Fact]
    public void ValidateGlyph_ПовторнаяВалидацияТойЖеТочкойВхода()
    {
        // icon/select присылает значок телом — валидация та же, что для модели (ADR-009 §8)
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("wallet", null));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph("нет-такого", null));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph("wallet", ["M3 21h18"]));   // оба вида
        Assert.Null(ProjectIconGlyphService.ValidateGlyph(null, null));
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph(null, new[] { "M3 21h18", "M4 4h6v6" }));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph(null, new[] { "M3 21h18", "<b>" }));
    }

    [Fact]
    public void GlyphSvg_СобираетсяБезРазметкиОтМодели()
    {
        var svg = GlyphSvg.Build(["M3 21h18", "M6 21V9l6-5 6 5v12"]);

        // Шаблонные атрибуты совпадают с ICON_PROPS фронта: штрих, толщина 2, currentColor.
        // Порядок атрибутов у XmlWriter не гарантирован (xmlns может уехать не первым) —
        // проверяем состав, а не последовательность
        Assert.StartsWith("<svg ", svg);
        Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.Contains("viewBox=\"0 0 24 24\"", svg);
        Assert.Contains("fill=\"none\"", svg);
        Assert.Contains("stroke=\"currentColor\"", svg);
        Assert.Contains("stroke-width=\"2\"", svg);
        Assert.Contains("stroke-linecap=\"round\"", svg);
        Assert.Contains("stroke-linejoin=\"round\"", svg);
        Assert.Equal(2, svg.Split("<path").Length - 1);
        Assert.DoesNotContain("<text", svg);
    }

    [Fact]
    public void LucideGlyphs_БелыйСписокЦеликомНижнегоРегистра()
    {
        Assert.All(LucideGlyphs.All, name =>
        {
            Assert.Matches("^[a-z][a-z0-9-]{1,39}$", name);
        });
        // Стартовый набор ADR-009 §5 на месте
        Assert.Contains("piggy-bank", LucideGlyphs.All);
        Assert.Contains("chart-line", LucideGlyphs.All);
        Assert.Equal(LucideGlyphs.Names.Count, LucideGlyphs.All.Count);
    }
}

// Номера ProjectIconKind закреплены ЯВНО (ADR-009 §6): projects.json хранит enum числом,
// и перенумерация после удаления Image молча превращала бы старые записи в «значковые»
// с пустым значком — исключения не было бы, только тихая порча смысла.
public class ProjectIconKindNumberingTests
{
    [Fact]
    public void НомераЗначений_ЗакрепленыЯвно_ЕдиницаВыведенаИзОбращения()
    {
        Assert.Equal(0, (int)ProjectIconKind.Initials);
        Assert.Equal(2, (int)ProjectIconKind.Glyph);
    }

    [Fact]
    public void СтараяЗаписьСРастровойИконкой_ЧитаетсяБезИсключенияИНеСтановитсяЗначком()
    {
        // Kind=1 — бывший Image; ImageFile/OriginalFile/Crop полей у модели больше нет,
        // лишние поля десериализатор игнорирует. Стор ProjectManager читает ровно с этими
        // опциями (PropertyNameCaseInsensitive, без JsonStringEnumConverter).
        var icon = JsonSerializer.Deserialize<ProjectIcon>(
            """{"Kind":1,"Color":"blue","ImageFile":"icon-abc.png","OriginalFile":"original-abc.png","Crop":{"X":1,"Y":2,"Size":3}}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(icon);
        Assert.NotEqual(ProjectIconKind.Glyph, icon!.Kind);   // не «значковый с пустым значком»
        Assert.Null(icon.Glyph);
        Assert.Equal("blue", icon.Color);
    }
}
