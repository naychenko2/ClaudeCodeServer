namespace ClaudeHomeServer.Services.ImageEditor;

// Стилевые LoRA Krea 2 Turbo (репак Comfy-Org/Krea-2, папка loras) и «стиль по образцу» ostris.
// Триггеры — из заметки шаблона ComfyUI image_krea2_turbo_t2i_int8.json; шаблон ставит триггер
// в промпт через StringConcatenate (промпт + «, » + триггер) и грузит LoRA узлом LoraLoaderModelOnly
public sealed record Krea2Style(string Id, string Label, string LoraFile, string Trigger);

public static class Krea2Styles
{
    // Сила LoRA стиля в шаблоне (заметка шаблона советует 1,0, но узел шаблона стоит на 0,8)
    public const double Strength = 0.8;

    // «Стиль по образцу»: LoRA ostris, сила 1 — как в шаблоне image_krea2_turbo_int8_image_style_reference.json
    public const string StyleReferenceLora = "krea2_style_reference.safetensors";
    public const double StyleReferenceStrength = 1.0;

    public static readonly IReadOnlyList<Krea2Style> All =
    [
        new("darkbrush", "Тёмная кисть", "krea2_darkbrush.safetensors", "monochrome ink wash style"),
        new("dotmatrix", "Пунктир", "krea2_dotmatrix.safetensors", "monochrome stippling style"),
        new("kidsdrawing", "Детский рисунок", "krea2_kidsdrawing.safetensors", "naive expressive sketch style"),
        new("neondrip", "Неоновые потёки", "krea2_neondrip.safetensors", "textured abstract style"),
        new("rainywindow", "Дождь на стекле", "krea2_rainywindow.safetensors", "rainy window style"),
        new("retroanime", "Ретро-аниме", "krea2_retroanime.safetensors", "purple retro anime style"),
        new("softwatercolor", "Акварель", "krea2_softwatercolor.safetensors", "art deco watercolor style"),
        new("sunsetblur", "Закатное размытие", "krea2_sunsetblur.safetensors", "ethereal motion blur style"),
        new("vintagetarot", "Винтажное таро", "krea2_vintagetarot.safetensors", "vintage tarot style"),
    ];

    // Неизвестный или пустой id — null: запуск идёт без стиля
    public static Krea2Style? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(s => s.Id == id.Trim());
}
