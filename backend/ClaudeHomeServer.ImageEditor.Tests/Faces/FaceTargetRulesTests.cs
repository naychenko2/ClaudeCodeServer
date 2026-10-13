using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Faces;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Faces;

// Чистые правила запроса подстановки (ADR-028, этап 3): лимит лиц, повторы номеров
// и персонажей. Тесты без зависимостей: FaceTargetRules.Validate — статика.
public class FaceTargetRulesTests
{
    private static FaceTarget Target(params (int Face, string Slug)[] assignments) =>
        new("v1", "key", assignments.Select(a => new FaceAssignment(a.Face, a.Slug)).ToList());

    // ── Корректные ──────────────────────────────────────────────────────────────

    [Fact]
    public void Одно_лицо_один_персонаж_проходит()
    {
        var result = FaceTargetRules.Validate(Target((1, "maria")));

        result.Ok.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Два_лица_два_персонажа_проходит()
    {
        var result = FaceTargetRules.Validate(
            Target((1, "maria"), (2, "ivan")));

        result.Ok.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
    }

    // ── Отказы ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Три_лица_отказ()
    {
        var result = FaceTargetRules.Validate(
            Target((1, "maria"), (2, "ivan"), (3, "olga")));

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Be("Не больше 2 лиц за раз");
    }

    [Fact]
    public void Лицо_дважды_отказ()
    {
        var result = FaceTargetRules.Validate(
            Target((1, "maria"), (1, "ivan")));

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Be("Лицо 1 указано дважды");
    }

    [Fact]
    public void Персонаж_на_двух_лицах_отказ()
    {
        var result = FaceTargetRules.Validate(
            Target((1, "maria"), (2, "maria")));

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Be("Каждому лицу — свой персонаж: «maria» уже стоит на лице 1");
    }

    [Fact]
    public void Пустой_слаг_отказ()
    {
        var result = FaceTargetRules.Validate(
            Target((1, "")));

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Be("Укажите персонажа");
    }

    [Fact]
    public void Пустой_список_отказ()
    {
        var result = FaceTargetRules.Validate(
            Target());

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Be("Не больше 2 лиц за раз");
    }

    [Fact]
    public void Номер_лица_ноль_отказ()
    {
        var result = FaceTargetRules.Validate(
            Target((0, "maria")));

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Be("Номер лица должен быть не меньше 1");
    }

    [Fact]
    public void Номер_лица_отрицательный_отказ()
    {
        var result = FaceTargetRules.Validate(
            Target((-1, "maria")));

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be(ImageEditErrorCodes.InvalidRequest);
        result.Error.Should().Be("Номер лица должен быть не меньше 1");
    }
}
