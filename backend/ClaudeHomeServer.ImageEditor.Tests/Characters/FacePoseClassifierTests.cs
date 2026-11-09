using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Углы головы → метки (ADR-027, §5): пороги по |yaw| 12°/45°, наклон по pitch ±15°, знак и профиль
public class FacePoseClassifierTests
{
    private static (string Angle, string Pitch) C(double yaw, double pitch = 0) =>
        FacePoseClassifier.Classify(new LocalFacePose(yaw, pitch, 0));

    [Theory]
    [InlineData(0, "front")]
    [InlineData(12, "front")]
    [InlineData(12.01, "three-quarter")]
    [InlineData(45, "three-quarter")]
    [InlineData(45.01, "profile")]
    [InlineData(-12, "front")]
    [InlineData(-12.01, "three-quarter")]
    [InlineData(-45, "three-quarter")]
    [InlineData(-45.01, "profile")]
    [InlineData(170, "profile")]
    public void Ракурс_по_модулю_yaw(double yaw, string expected) => C(yaw).Angle.Should().Be(expected);

    [Theory]
    [InlineData(0, "level")]
    [InlineData(15, "level")]
    [InlineData(-15, "level")]
    [InlineData(15.01, "up")]
    [InlineData(-15.01, "down")]
    [InlineData(40, "up")]
    [InlineData(-40, "down")]
    public void Наклон_по_pitch_положительный_это_голова_поднята(double pitch, string expected) =>
        C(5, pitch).Pitch.Should().Be(expected);

    [Theory]
    [InlineData(60, 0)]
    [InlineData(60, 30)]
    [InlineData(-60, -30)]
    [InlineData(45.01, 15.01)]
    public void В_профиле_наклон_unknown(double yaw, double pitch) => C(yaw, pitch).Pitch.Should().Be("unknown");

    [Fact]
    public void Три_четверти_наклон_различает()
    {
        C(30, 20).Should().Be(("three-quarter", "up"));
        C(-30, -20).Should().Be(("three-quarter", "down"));
    }

    [Fact]
    public void Нечисловой_угол_это_unknown_а_не_профиль()
    {
        C(double.NaN).Should().Be(("unknown", "unknown"));
        C(10, double.PositiveInfinity).Should().Be(("unknown", "unknown"));
    }
}
