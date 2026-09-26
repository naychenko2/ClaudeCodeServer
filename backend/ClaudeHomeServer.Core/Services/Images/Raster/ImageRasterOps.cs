namespace ClaudeHomeServer.Services.ImageEditor;

// Записи операций растра (ADR-018 §9, §10.1): едут в Core вместе с IImageRaster, чтобы
// растр в Images и редактор в модуле говорили на одном языке без ссылки друг на друга.
// Namespace прежний — имена в JSON и в коде вызывающих не меняются.

public enum ImageEncodeFormat { Png, Jpeg, Webp }

// Quality 40–100, у PNG игнорируется; null — качество по умолчанию формата
public record ImageEncodeSpec(ImageEncodeFormat Format, int? Quality = null);

// Прямоугольник в долях от размеров картинки (0..1), начало — левый верхний угол
public record ImageFractionRect(double X, double Y, double Width, double Height);

public enum ImageFlipAxis { Horizontal, Vertical }

// Операция без ИИ; в JSON — { "type": "crop", … }, дискриминатор первым полем объекта.
// Кодирование — не операция цепочки, а поле Encode запроса: оно применяется один раз на выходе.
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(AutoOrientOp), "autoOrient")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(CropOp), "crop")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(RotateOp), "rotate")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(FlipOp), "flip")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(ResizeOp), "resize")]
public abstract record ImageTransformOp;

public sealed record AutoOrientOp : ImageTransformOp;

public sealed record CropOp(ImageFractionRect Rect) : ImageTransformOp;

// Degrees — 90, 180 или 270 по часовой; другое — 400
public sealed record RotateOp(int Degrees) : ImageTransformOp;

public sealed record FlipOp(ImageFlipAxis Axis) : ImageTransformOp;

// Либо Width/Height в px, либо Percent; LockAspect — недостающую сторону досчитать по пропорциям
public sealed record ResizeOp(int? Width = null, int? Height = null, double? Percent = null, bool LockAspect = true)
    : ImageTransformOp;
