using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

public static partial class ComfyWorkflows
{
    public const int MaxFacePoseImages = 20;

    // Граф ракурса: один узел CcsFacePose, на вход — имена уже загруженных в input ComfyUI фото
    // по одному на строку (вход references, как у CcsFaceSimilarity). Ответ — текст ccs_face_pose={файл: углы}
    public static JsonObject FacePose(IReadOnlyList<string> images) => new()
    {
        ["pose"] = Node("CcsFacePose", new JsonObject { ["references"] = string.Join("\n", images) }),
    };
}
