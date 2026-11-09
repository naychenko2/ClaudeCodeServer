namespace ClaudeHomeServer.Services.ImageEditor;

// Углы головы → метки ракурса и наклона (ADR-027, §5). Пороги — стартовые, их правит замер шага 21.
// Ракурс — только по |yaw|; наклон — по pitch, знак установлен пробой: pitch > 0 — голова поднята.
// В профиль модель наклон не различает (2 из 11), поэтому там он unknown
public static class FacePoseClassifier
{
    public const double FrontMaxYaw = 12;
    public const double ThreeQuarterMaxYaw = 45;
    public const double LevelMaxPitch = 15;

    public static (string Angle, string Pitch) Classify(LocalFacePose pose)
    {
        const string unknown = PhotoLabelValues.Unknown;
        if (!double.IsFinite(pose.Yaw) || !double.IsFinite(pose.Pitch)) return (unknown, unknown);
        var yaw = Math.Abs(pose.Yaw);
        var angle = yaw <= FrontMaxYaw ? "front" : yaw <= ThreeQuarterMaxYaw ? "three-quarter" : "profile";
        if (angle == "profile") return (angle, unknown);
        var pitch = Math.Abs(pose.Pitch) <= LevelMaxPitch ? "level" : pose.Pitch > 0 ? "up" : "down";
        return (angle, pitch);
    }
}
