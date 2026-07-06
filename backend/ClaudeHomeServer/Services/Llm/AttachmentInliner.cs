namespace ClaudeHomeServer.Services.Llm;

// Инлайн вложений в текст сообщения — общий для адаптеров. Текстовые файлы вставляются
// код-блоком, бинарные — пометкой. Изображения адаптеры обрабатывают сами
// (Claude шлёт image-блоками, DeepSeek не поддерживает).
public static class AttachmentInliner
{
    // Расширения, которые считаем изображениями (адаптеры решают, что с ними делать)
    public static readonly HashSet<string> ImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    // Максимум текста на один инлайн-файл — чтобы вложение не раздуло сообщение
    private const int MaxInlineBytes = 256 * 1024;

    public static (List<string> images, List<string> others) SplitImagePaths(IReadOnlyList<string>? paths)
    {
        var images = new List<string>();
        var others = new List<string>();
        if (paths != null)
            foreach (var p in paths)
                (ImageExts.Contains(Path.GetExtension(p)) ? images : others).Add(p);
        return (images, others);
    }

    public static string BuildMessageText(string rootPath, string text, IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return text;

        var sb = new System.Text.StringBuilder(text);
        foreach (var relativePath in paths)
        {
            try
            {
                var fullPath = FileService.SafeJoin(rootPath, relativePath);
                if (!File.Exists(fullPath)) continue;

                var info = new FileInfo(fullPath);
                byte[] bytes;
                using (var fs = info.OpenRead())
                {
                    var len = (int)Math.Min(info.Length, MaxInlineBytes);
                    bytes = new byte[len];
                    var read = 0;
                    while (read < len)
                    {
                        var n = fs.Read(bytes, read, len - read);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read < len) Array.Resize(ref bytes, read);
                }

                // Бинарный файл (PDF/docx/xlsx/архив и т.п.) определяем по нулевому байту.
                // Не инлайним мусор-кракозябры: даём ссылку — рабочая папка = корень проекта,
                // модель при необходимости откроет файл инструментом чтения.
                if (Array.IndexOf(bytes, (byte)0) >= 0)
                {
                    sb.Append($"\n\n---\nПрикреплён файл: {relativePath} (бинарный/документ — открой инструментом чтения файлов, если нужно его содержимое).");
                    continue;
                }

                var content = System.Text.Encoding.UTF8.GetString(bytes);
                var truncated = info.Length > MaxInlineBytes ? "\n…(файл обрезан по размеру)" : "";
                var ext = Path.GetExtension(relativePath).TrimStart('.');
                sb.Append($"\n\n---\nФайл: {relativePath}\n```{ext}\n{content}{truncated}\n```");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AttachmentInliner] Не удалось заинлайнить вложение «{relativePath}»: {ex.Message}");
            }
        }
        return sb.ToString();
    }
}
