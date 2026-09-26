namespace ClaudeHomeServer.Services.Composition;

// Шов чтения настройки одного места генерации картинок (ADR-018 §10.1). Реализует стор
// настроек в Images; редактор в модуле берёт отсюда поставщика и модель своего места,
// не видя типов Images. Выключенная Images — шва нет, редактор предвыбирает «Авто».
public interface IImagePlaceSettings
{
    // Эффективный режим места: auto или ключ поставщика, в нижнем регистре
    string ProviderFor(string place);

    // Модель поставщика в этом месте; null — дефолт драйвера
    string? ModelFor(string place, string providerKey);
}

// Ключи мест, которые нужны за пределами Images
public static class ImagePlaceKeys
{
    public const string ImageEditor = "image-editor";
}
