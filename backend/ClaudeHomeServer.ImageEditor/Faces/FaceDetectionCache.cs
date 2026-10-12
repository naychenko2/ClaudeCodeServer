using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor.Faces;

// Кэш детекции лиц (ADR-027, §4, этап 2): повторный запрос на те же байты картинки версии не
// дублирует дорогой прогон ComfyUI. LRU в памяти, ключ (ownerId, sha256 байт), значение —
// успешный результат (без Error и без Busy). Single-flight: два параллельных запроса на один
// ключ запускают один детектор, второй ждёт ту же задачу.
public sealed class FaceDetectionCache
{
    // Номер версии формата результата: изменился — меняется facesKey, старые записи не годятся
    public const string FormatVersion = "v1";
    private const int DefaultCapacity = 256;

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<(string Owner, string Hash), Entry> _entries = new();
    // LRU: порядок обращения, последнее — в конце
    private readonly LinkedList<(string Owner, string Hash)> _order = new();
    // Номер поколения записи: сверка, что завершение детекции относится к ТЕКУЩЕЙ записи, а не
    // к вытесненной (и заново занятой тем же ключом)
    private int _sequence;

    private sealed class Entry
    {
        public int Seq;
        // Готовый результат; null, пока детекция в процессе
        public LocalFaceDetectResult? Result;
        // Висячая задача детекции: все, кто пришёл на этот ключ, ждут её
        public Task<LocalFaceDetectResult>? Task;
    }

    public FaceDetectionCache(int? capacity = null)
    {
        _capacity = capacity is { } c and > 0 ? c : DefaultCapacity;
    }

    // facesKey — детерминирован по байтам: префикс 16 hex sha256 + номер версии формата.
    // Изменение байт картинки меняет ключ; формат версии — константа, по которой клиент понимает
    // «список лиц этой картинки не изменился»
    public static string FacesKey(byte[] image) => $"{Sha256Prefix16(image)}:{FormatVersion}";

    // Прогон детектора: промах в кэше → detect; успешный результат (без Error и без Busy) кладётся
    // в кэш. Два параллельных запроса на один ключ — один детектор, второй ждёт ту же задачу
    public async Task<LocalFaceDetectResult> GetOrDetectAsync(string ownerId, byte[] image,
        Func<Task<LocalFaceDetectResult>> detect)
    {
        var key = (ownerId, Sha256Prefix16(image));
        Task<LocalFaceDetectResult> task;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                if (e.Result is { } hit)
                    return hit;
                task = e.Task!;
            }
            else
            {
                var entry = new Entry { Seq = ++_sequence };
                entry.Task = DetectAndStoreAsync(key, entry.Seq, detect);
                _entries[key] = entry;
                _order.AddLast(key);
                EvictIfNeeded();
                task = entry.Task;
            }
        }
        return await task;
    }

    // Завершение детекции. Вызывается через ContinueWith (не inline): детектор может вернуть
    // готовую задачу, и inline-вызов встал бы в тот же lock, что и постановка записи, — запись
    // удалилась бы до того, как была добавлена. Успех (без Error и без Busy) кладётся в кэш;
    // неуспех (Error/Busy, сбой, отмена) запись удаляет — повторный запрос запускает детектор
    // снова (иначе завершённая задача single-flight мемоизировала бы сбой до вытеснения LRU)
    private Task<LocalFaceDetectResult> DetectAndStoreAsync((string Owner, string Hash) key, int seq,
        Func<Task<LocalFaceDetectResult>> detect) =>
        detect().ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled)
            {
                // Сбой или отмена: запись удаляется, а исключение пробрасывается исходным — отмена
                // даёт TaskCanceledException, сбой — первичное, не AggregateException (GetAwaiter)
                RemoveEntry(key, seq);
                return t.GetAwaiter().GetResult();
            }
            FinishDetection(key, seq, t.Result);
            return t.Result;
        }, TaskScheduler.Default);

    private void FinishDetection((string Owner, string Hash) key, int seq, LocalFaceDetectResult result)
    {
        lock (_gate)
        {
            // Запись могла стать чужой: LRU вытеснила, а тот же ключ заново заняли (новое поколение).
            // Тогда результат уже не наш — не трогаем. Сверка по номеру поколения, не по ключу
            if (!_entries.TryGetValue(key, out var e) || e.Seq != seq) return;
            if (result.Error is null && !result.Busy)
            {
                e.Result = result;
            }
            else
            {
                _entries.Remove(key);
                var node = _order.Find(key);
                if (node is not null) _order.Remove(node);
            }
        }
    }

    // Удаление записи по ключу и поколению: только если запись ещё принадлежит этому запуску
    // (сверка по seq, как в FinishDetection). Вызывается при сбое/отмене детекции, чтобы повторный
    // запрос на те же байты запустил детектор заново
    private void RemoveEntry((string Owner, string Hash) key, int seq)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e) || e.Seq != seq) return;
            _entries.Remove(key);
            var node = _order.Find(key);
            if (node is not null) _order.Remove(node);
        }
    }

    // LRU: вытеснить самые старые, пока не уложимся в ёмкость
    private void EvictIfNeeded()
    {
        while (_entries.Count > _capacity && _order.First is { } oldest)
        {
            _entries.Remove(oldest.Value);
            _order.Remove(oldest);
        }
    }

    // Префикс 16 hex sha256: достаточно, чтобы разные байты давали разные ключи (64 бита)
    private static string Sha256Prefix16(byte[] image)
    {
        var hash = SHA256.HashData(image);
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
