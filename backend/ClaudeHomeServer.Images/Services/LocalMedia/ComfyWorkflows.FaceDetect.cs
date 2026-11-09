using System.Text.Json.Nodes;

namespace ClaudeHomeServer.Services.Images.LocalMedia;

public static partial class ComfyWorkflows
{
    // Граф детекции лиц: один узел CcsFaceDetect, на вход — имя уже загруженного в input
    // ComfyUI фото (вход references, как у CcsFacePose). Ответ — текст ccs_face_detect={...}
    public static JsonObject FaceDetect(string image) => new()
    {
        ["detect"] = Node("CcsFaceDetect", new JsonObject { ["references"] = image }),
    };
}
