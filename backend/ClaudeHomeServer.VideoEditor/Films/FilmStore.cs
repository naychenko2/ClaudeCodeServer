using System.Collections.Concurrent;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Файл фильма на диске проекта: чтение с проверкой и атомарная запись под ревизией (ADR-022 §2). Запись —
// временный файл рядом и rename; чужая правка между чтением и записью (ревизия — хеш содержимого файла) —
// Conflict с актуальным содержимым. Это ЕДИНСТВЕННОЕ место, где модуль переписывает существующий файл проекта.
// Пути сюда приходят уже проверенными ProjectLinkGuard.ResolveInside — полные хостовые.
public sealed class FilmStore
{
    // Замок на файл: два патча и сборка в одном процессе не перемешают чтение с записью. Между процессами
    // защищает ревизия, проверяемая ещё раз перед самым rename
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);

    public enum ReadStatus { Ok, NotFound, Invalid, Unsupported }

    public sealed record Read(ReadStatus Status, FilmDocument? Document, string Revision, string? Error)
    {
        public bool Editable => Status == ReadStatus.Ok;
    }

    public enum WriteStatus { Ok, Conflict, NotFound, NameTaken, Invalid }

    // Current — содержимое файла, как оно есть сейчас (у Conflict — чужая правка, у Ok — записанное)
    public sealed record Write(WriteStatus Status, Read Current, string? Error = null);

    public Read ReadFile(string fullPath)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(fullPath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Read(ReadStatus.NotFound, null, "", null);
        }
        var parsed = FilmFormat.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        var revision = FilmFormat.RevisionOf(bytes);
        return parsed.Status switch
        {
            FilmFormat.Status.Ok => new Read(ReadStatus.Ok, parsed.Document, revision, null),
            FilmFormat.Status.Unsupported => new Read(ReadStatus.Unsupported, parsed.Document, revision, parsed.Error),
            _ => new Read(ReadStatus.Invalid, parsed.Document, revision, parsed.Error),
        };
    }

    // Запись под ревизией: expectedRevision обязателен для существующего файла. create = true — файл должен
    // отсутствовать (CreateNew): новый фильм не затирает чужой
    public Write WriteFile(string fullPath, FilmDocument document, string? expectedRevision, bool create)
    {
        if (FilmFormat.Validate(document) is { } invalid)
            return new Write(WriteStatus.Invalid, new Read(ReadStatus.Invalid, document, "", invalid), invalid);
        lock (Locks.GetOrAdd(fullPath, _ => new object()))
        {
            var current = ReadFile(fullPath);
            if (create)
            {
                if (current.Status != ReadStatus.NotFound) return new Write(WriteStatus.NameTaken, current);
            }
            else
            {
                if (current.Status == ReadStatus.NotFound) return new Write(WriteStatus.NotFound, current);
                if (current.Status == ReadStatus.Unsupported) return new Write(WriteStatus.Invalid, current, current.Error);
                if (expectedRevision is null || current.Revision != expectedRevision) return new Write(WriteStatus.Conflict, current);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var tmp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var bytes = System.Text.Encoding.UTF8.GetBytes(FilmFormat.Serialize(document));
            try
            {
                File.WriteAllBytes(tmp, bytes);
                // Ещё раз перед самым rename: правка из другого процесса за время записи temp не затирается
                if (!create && File.Exists(fullPath) && FilmFormat.RevisionOf(File.ReadAllBytes(fullPath)) != current.Revision)
                    return new Write(WriteStatus.Conflict, ReadFile(fullPath));
                // Создание — Move без перезаписи (атомарный «создать, если нет», как у сборки): занятое другим процессом
                // имя в окне после проверки под замком не затирается. Прямой FileMode.CreateNew писал бы в целевой
                // файл на месте, и читатель увидел бы недописанный .film — потому temp + Move
                if (create)
                {
                    try { File.Move(tmp, fullPath, overwrite: false); }
                    catch (IOException) when (File.Exists(fullPath)) { return new Write(WriteStatus.NameTaken, ReadFile(fullPath)); }
                }
                else File.Move(tmp, fullPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
            return new Write(WriteStatus.Ok, new Read(ReadStatus.Ok, document, FilmFormat.RevisionOf(bytes), null));
        }
    }

    // Запись сервера без ревизии человека: сборка дописывает свою сборку, подписка — музыку. Читает свежее
    // содержимое под замком, применяет правку и пишет под ЕГО ревизией — человеческая правка не затирается,
    // а ложится поверх (правка сервера — дописывание факта)
    public Write Update(string fullPath, Func<FilmDocument, FilmDocument?> change)
    {
        lock (Locks.GetOrAdd(fullPath, _ => new object()))
        {
            var current = ReadFile(fullPath);
            if (current.Status != ReadStatus.Ok) return new Write(
                current.Status == ReadStatus.NotFound ? WriteStatus.NotFound : WriteStatus.Invalid, current, current.Error);
            var next = change(current.Document!);
            if (next is null) return new Write(WriteStatus.Ok, current);
            return WriteFile(fullPath, next, current.Revision, create: false);
        }
    }
}
