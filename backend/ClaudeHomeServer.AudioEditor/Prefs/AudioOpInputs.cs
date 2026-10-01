using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.AudioEditor.Prefs;

// Входы операции в настройках нити и префах (Inputs) — то, что панель подставляет в форму запуска:
// язык, образец, кусок, куски склейки и стыки, голос из библиотеки, реплики диалога. В params модели
// не уходят никогда и запуском не читаются: входы задачи едут в самом запросе запуска. Белый список
// общий на все операции, у каждого ключа свой тип. Пути — только от корня проекта и только через
// ProjectLinkGuard; у личной области путей проекта нет вовсе
public static class AudioOpInputs
{
    public const string Language = "language";
    public const string ReferencePath = "referencePath";
    public const string StartSec = "startSec";
    public const string EndSec = "endSec";
    public const string Voice = "voice";
    public const string Pieces = "pieces";
    public const string Joint = "joint";
    public const string Joints = "joints";
    public const string Dialogue = "dialogue";

    public static readonly IReadOnlyList<string> Keys =
        [Language, ReferencePath, StartSec, EndSec, Voice, Pieces, Joint, Joints, Dialogue];

    private const int MaxJsonLength = 64 * 1024;
    private const int MaxPathLength = 1024;
    private const int MaxPieces = 50;
    private const int MaxLines = 200;
    private const int MaxLineText = 5000;
    private const double MaxSeconds = 24 * 3600;
    private const double MaxJointSeconds = 60;
    private static readonly string[] JointKinds = ["butt", "pause", "crossfade"];

    // null — входы годятся; иначе текст отказа для 400
    public static string? Validate(JsonObject? inputs, AudioEditScope scope)
    {
        if (inputs is null) return null;
        if (inputs.ToJsonString().Length > MaxJsonLength) return "inputs: слишком большой объём";
        foreach (var (key, value) in inputs)
        {
            var error = key switch
            {
                Language => Str(value, 16) is { } lang && lang.All(c => char.IsAsciiLetter(c) || c == '-')
                    ? null : "язык — код вида ru или en-US",
                ReferencePath => ProjectPath(value, scope),
                StartSec or EndSec => Seconds(value, MaxSeconds) ? null : "секунды — число от 0",
                Voice => AudioVoiceRefs.SlugOf(Str(value, 200)) is { } slug && VoiceStore.IsValidSlug(slug)
                    ? null : "голос — значение voice:<slug> из библиотеки",
                Pieces => List(value, MaxPieces, piece => Piece(piece, scope)),
                Joint => JointOf(value),
                Joints => List(value, MaxPieces - 1, joint => joint is null ? null : JointOf(joint)),
                Dialogue => List(value, MaxLines, Line),
                _ => "неизвестный вход операции. Допустимые: " + string.Join(", ", Keys),
            };
            if (error is not null) return $"inputs.{key}: {error}";
        }
        return null;
    }

    private static string? Piece(JsonNode? node, AudioEditScope scope)
    {
        if (node is not JsonObject piece) return "кусок — объект";
        foreach (var (key, value) in piece)
        {
            var error = key switch
            {
                "threadId" or "versionId" => Str(value, 64) is null ? "id — строка" : null,
                "projectFile" => ProjectPath(value, scope),
                _ => $"у куска нет поля «{key}» (threadId, versionId, projectFile)",
            };
            if (error is not null) return error;
        }
        return piece.ContainsKey("threadId") == piece.ContainsKey("projectFile")
            ? "у куска ровно одно из threadId и projectFile" : null;
    }

    private static string? JointOf(JsonNode? node)
    {
        if (node is not JsonObject joint) return "стык — объект { kind, seconds }";
        foreach (var (key, value) in joint)
        {
            var error = key switch
            {
                "kind" => Str(value, 16) is { } kind && JointKinds.Contains(kind) ? null : "стык — butt, pause или crossfade",
                "seconds" => Seconds(value, MaxJointSeconds) ? null : $"длина стыка — от 0 до {MaxJointSeconds} с",
                _ => $"у стыка нет поля «{key}» (kind, seconds)",
            };
            if (error is not null) return error;
        }
        return joint.ContainsKey("kind") ? null : "у стыка нет kind";
    }

    private static string? Line(JsonNode? node)
    {
        if (node is not JsonObject line) return "реплика — объект { text, voice }";
        foreach (var (key, value) in line)
        {
            var error = key switch
            {
                "text" => Str(value, MaxLineText) is null ? $"текст реплики — строка до {MaxLineText} символов" : null,
                "voice" => Str(value, 200) is null ? "голос реплики — строка" : null,
                _ => $"у реплики нет поля «{key}» (text, voice)",
            };
            if (error is not null) return error;
        }
        return null;
    }

    private static string? ProjectPath(JsonNode? value, AudioEditScope scope)
    {
        if (Str(value, MaxPathLength) is not { } path || path.Trim().Length == 0) return "путь — непустая строка";
        if (scope.IsPersonal || scope.Project is not { } project) return "у личного чата путей проекта нет";
        return ProjectLinkGuard.ResolveInside(project.RootPath, path.Trim()) is null ? "путь вне проекта" : null;
    }

    private static string? List(JsonNode? value, int max, Func<JsonNode?, string?> item)
    {
        if (value is not JsonArray array) return "ожидается массив";
        if (array.Count > max) return $"не больше {max} элементов";
        for (var i = 0; i < array.Count; i++)
            if (item(array[i]) is { } error) return $"[{i}] {error}";
        return null;
    }

    private static string? Str(JsonNode? value, int maxLength) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { } s && s.Length <= maxLength
            ? s : null;

    private static bool Seconds(JsonNode? value, double max) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && d >= 0 && d <= max;
}
