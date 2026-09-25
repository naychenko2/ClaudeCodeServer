using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.ImageEditor.Spending;

// Точка записи и чтения трат редактора картинок (ADR-016, раздел 4). Самостоятельный
// сервис Core, не привязан к существующему ISpendCollector/SpendStore — интеграция
// с общей аналитикой расхода делается отдельной задачей на слое контрактов.
public interface IImageEditSpendStore
{
    void Record(ImageEditSpendRecord record);

    // Без прав администратора — только свои записи; isAdmin=true — все записи всех
    // владельцев (панель «Модели и расход» показывает админу разбивку по пользователям).
    IReadOnlyList<ImageEditSpendRecord> Query(string ownerId, bool isAdmin);
}

// Файловое хранилище: append-only JSONL в data/, как остальные сторы проекта
// («новой БД не нужно» — team memory, техразрез 2026-07-24). Каталог передаётся явно
// конструктором — в DI сервис не регистрируется этой задачей.
public sealed class ImageEditSpendStore : IImageEditSpendStore
{
    private readonly string _path;
    private readonly object _ioLock = new();
    private readonly List<ImageEditSpendRecord> _records = [];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public ImageEditSpendStore(string dir)
    {
        _path = Path.Combine(dir, "image-editor-spend.jsonl");
        Load();
    }

    public void Record(ImageEditSpendRecord record)
    {
        lock (_ioLock)
        {
            _records.Add(record);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine);
        }
    }

    public IReadOnlyList<ImageEditSpendRecord> Query(string ownerId, bool isAdmin)
    {
        lock (_ioLock)
            return isAdmin
                ? [.. _records]
                : [.. _records.Where(r => r.OwnerId == ownerId)];
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        lock (_ioLock)
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<ImageEditSpendRecord>(line, JsonOpts) is { } r)
                        _records.Add(r);
                }
                catch (JsonException) { /* битая строка (оборванный append) — пропускаем */ }
            }
        }
    }
}
