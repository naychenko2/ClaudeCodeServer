using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Preset-блок ответа Describe места каталога. Битую ссылку (preset:{id}, где id удалён из общих)
// через HTTP не смоделировать: валидация LocalActionsAdminController.Set отсекает несуществующий
// id ещё до записи в стор. Поэтому DescribePreset вынесен в internal static и тестируется напрямую —
// здесь покрываются все ветки, включая недостижимую через эндпоинт «битую ссылку».
public class LocalActionsAdminControllerDescribePresetTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static IReadOnlyList<ModelRoutePreset> Globals(params ModelRoutePreset[] presets) => presets;

    private static JsonElement Render(string? stored, IReadOnlyList<ModelRoutePreset> globals)
    {
        var descriptor = LocalActionsAdminController.DescribePreset(stored, globals);
        descriptor.Should().NotBeNull("preset-блок ожидается для preset:{id}");
        return JsonSerializer.SerializeToElement(descriptor, Json);
    }

    [Fact]
    public void ВалиднаяСсылка_ОтдаётIdИмяИШаги()
    {
        var preset = new ModelRoutePreset { Id = "p1", Name = "Цепочка", Steps = ["tier:strong", "glm-5.2"] };

        var el = Render("preset:p1", Globals(preset));

        el.GetProperty("id").GetString().Should().Be("p1");
        el.GetProperty("name").GetString().Should().Be("Цепочка");
        el.GetProperty("steps").EnumerateArray().Select(s => s.GetString())
            .Should().Equal("tier:strong", "glm-5.2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tier:strong")]
    [InlineData("tier:medium")]
    [InlineData("local")]
    [InlineData("claude")]
    [InlineData("default")]
    [InlineData("glm-5.2")]
    public void НеСсылкаПресета_ОтдаётNull(string? stored)
    {
        // Обычный маршрут места (слот, модель, локаль, легаси) — preset отсутствует
        LocalActionsAdminController.DescribePreset(stored, Globals())
            .Should().BeNull();
    }

    [Fact]
    public void ПустойIdВСсылке_ОтдаётNull()
    {
        // "preset:" без id — невалидный маршрут; в сторе не бывает, но обрабатываем как «не ссылка»,
        // а не как битую: без id сопоставлять не с чем
        LocalActionsAdminController.DescribePreset("preset:", Globals(new ModelRoutePreset { Id = "x" }))
            .Should().BeNull();
    }

    [Fact]
    public void БитаяСсылка_ОтдаётPresetСПустымИменем()
    {
        // Рассинхрон: пресет удалили после назначения, оверрайд «preset:удалённый» остался.
        // preset != null (это всё ещё preset-route), но name=null и шагов нет — UI покажет,
        // что ссылка протухла, а не смолчит будто выбран слот
        var el = Render("preset:удалённый", Globals(new ModelRoutePreset { Id = "другой" }));

        el.GetProperty("id").GetString().Should().Be("удалённый");
        el.GetProperty("name").ValueKind.Should().Be(JsonValueKind.Null);
        el.GetProperty("steps").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void IdНезависимОтРегистраПриПоискеПресета()
    {
        var preset = new ModelRoutePreset { Id = "AbC-123", Name = "Регистр", Steps = ["tier:weak"] };

        // Сравнение id регистронезависимо (как везде вpreset-логике) — найдётся
        var el = Render("preset:abc-123", Globals(preset));

        el.GetProperty("id").GetString().Should().Be("AbC-123");
        el.GetProperty("name").GetString().Should().Be("Регистр");
    }
}
