using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.ChatContext;

namespace ClaudeHomeServer.Services.AudioEditor.Threads;

// Нити звука чата (ADR-021 §2): что лежит в data/audio-threads/{ownerId}/{sessionId}.json.
// Focus — нить «в работе» этого чата или null; Revision растёт с каждой записью, запись со
// старой ревизией — конфликт. Добавлять поля можно только аддитивно: файл живёт в бэкапе.
// Events — журнал «с прошлого сообщения» для блока хвоста хода, TurnCursor — до какого момента
// журнал уже показан ходу (сдвиг курсора ревизию не поднимает: сборка хода — не правка).
public sealed record AudioThreadsState(string? Focus, long Revision, IReadOnlyList<AudioThread> Threads)
{
    public static AudioThreadsState Empty { get; } = new(null, 0, []);

    public IReadOnlyList<AudioThreadEvent> Events { get; init; } = [];
    public DateTime? TurnCursor { get; init; }
}

// Нить — один звуковой файл в ленте чата. File — путь от корня проекта через «/», null у черновика
// «Новый звук»: у него есть только DraftFolder ("" — корень). Lineage — прежние пути файла, по
// которым нить шла за сохранениями.
//
// Versions — по порядку появления; у нити по файлу первой идёт исходник (AudioThreadVersion.OriginId),
// у черновика исходника нет. CurrentVersionId — версия «в работе», от неё идёт следующая операция.
// В отличие от картинок, правка без ИИ — тоже новая версия, а не шаг: обрезка и сведение меняют
// длину и состав файлов, и сравнение A/B должно видеть их как версии.
//
// Settings — последние настройки ЭТОЙ нити (решение 2026-10-01: настройки на каждую нить); при
// запуске они идут первыми, перед префами режима и умолчанием каталога.
public sealed record AudioThread(
    string Id,
    string? File,
    IReadOnlyList<string> Lineage,
    string? DraftFolder,
    DateTime CreatedAt)
{
    public IReadOnlyList<AudioThreadVersion> Versions { get; init; } = [];
    public string? CurrentVersionId { get; init; }
    public IReadOnlyList<AudioThreadLaunch> Launches { get; init; } = [];
    public AudioThreadSettings? Settings { get; init; }
    // Файлы, которые человек сохранил из нити (для «Зафиксировать только этот чат», ADR-023 §3.3)
    public IReadOnlyList<ThreadSavedFile> SavedFiles { get; init; } = [];
    // Имя, предложенное для файла черновика при сохранении (склейка: «podcast-full.mp3»); null — нет
    public string? Name { get; init; }

    [JsonIgnore]
    public AudioThreadVersion? CurrentVersion => Version(CurrentVersionId);

    // Есть что терять: хотя бы одна версия кроме исходника
    [JsonIgnore]
    public bool HasVersions => Versions.Any(v => !v.IsOrigin);

    [JsonIgnore]
    public bool HasRunningLaunch => Launches.Any(l => l.Status == AudioThreadLaunchStatus.Running);

    public AudioThreadVersion? Version(string? id) => id is null ? null : Versions.FirstOrDefault(v => v.Id == id);

    // «Версия 3» / «исходник»
    public static string Label(AudioThreadVersion version) =>
        version.IsOrigin ? "исходник" : $"версия {version.Number}";
}

// Версия звука — набор файлов с ролями (AudioFileRoles). Исходник: Id = OriginId, Number = 0, без
// JobId, единственный файл main — путь от корня проекта. Версия от запуска: JobId + Variant, файлы —
// пути в рабочей папке задачи; BaseVersionId — версия, от которой запускали (null — с нуля).
// License — лицензия модели на момент запуска («CC BY-NC», «GPL-3.0», «watermark», null — не указана):
// значок рисуется из версии, а не из текущего каталога — каталог может поменяться.
public sealed record AudioThreadVersion(
    string Id,
    int Number,
    string? JobId,
    int? Variant,
    string? BaseVersionId,
    IReadOnlyList<AudioVersionFile> Files,
    string? License,
    DateTime CreatedAt)
{
    public const string OriginId = "origin";

    [JsonIgnore]
    public bool IsOrigin => Id == OriginId;

    public AudioVersionFile? File(string role) => Files.FirstOrDefault(f => f.Role == role);

    public static AudioThreadVersion Origin(string file, DateTime at) =>
        new(OriginId, 0, null, null, null, [new AudioVersionFile(AudioFileRoles.Main, file)], null, at);
}

// Файл версии: Role — AudioFileRoles.*, Path — см. AudioThreadVersion
public sealed record AudioVersionFile(string Role, string Path);

// Роли файлов версии (ADR-021 §2). Стем — «stem:<имя>» (stem:vocals, stem:drums)
public static class AudioFileRoles
{
    public const string Main = "main";
    public const string Score = "score";
    public const string Subtitles = "subtitles";
    public const string Lyrics = "lyrics";
    public const string Text = "text";
    public const string Midi = "midi";
    public const string Model = "model";
    public const string Index = "index";
    public const string StemPrefix = "stem:";

    private static readonly HashSet<string> Fixed = [Main, Score, Subtitles, Lyrics, Text, Midi, Model, Index];

    public static string Stem(string name) => StemPrefix + name;

    // Имя стема — латиница, цифры, «-» и «_»: оно уходит в имя файла папки стемов
    public static bool IsValid(string? role)
    {
        if (role is null) return false;
        if (Fixed.Contains(role)) return true;
        if (!role.StartsWith(StemPrefix, StringComparison.Ordinal)) return false;
        var name = role[StemPrefix.Length..];
        return name.Length is > 0 and <= 40
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }
}

// Запуск в нить: якорь в ленте — по JobId. BaseVersionId — откуда запустили; Status —
// AudioThreadLaunchStatus.*; Initiator — human | agent; Prompt — для блока хвоста хода;
// License — лицензия модели, зафиксированная в момент запуска, её наследуют версии запуска
public sealed record AudioThreadLaunch(
    string JobId,
    string? BaseVersionId,
    DateTime At,
    string Status,
    string Initiator,
    string? Prompt,
    string? License);

public static class AudioThreadLaunchStatus
{
    public const string Running = "running";
    // Результаты стали версиями
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    // Задачу оборвал перезапуск сервера: реестр задач живёт в памяти, результата не будет
    public const string Interrupted = "interrupted";
}

// Последние настройки нити: Mode — AudioModes.*, Operation — операция режима, Provider и Model —
// выбор поставщика, Fields — параметры модели (ключи её схемы, идут в params), Count — число
// вариантов. null у поля — нить его не задаёт, при запуске оно берётся из префов режима
// (AudioPrefsResolver). Inputs — входы операции (язык, образец, кусок, куски склейки, голос из
// библиотеки, реплики; белый список AudioOpInputs): их только подставляет панель, в params и запуск
// они не попадают — входы задачи едут в запросе запуска. У старых записей Inputs нет — null
public sealed record AudioThreadSettings(string Mode, string? Operation, string? Provider, string? Model, JsonObject? Fields,
    int? Count = null)
{
    public JsonObject? Inputs { get; init; }
}

public static class AudioModes
{
    public const string Voice = "voice";
    public const string Music = "music";
    public const string Process = "process";

    public static bool IsValid(string? mode) => mode is Voice or Music or Process;
}

// Запись журнала нитей: Kind — AudioThreadEventKinds.*, Text — строка для блока хвоста хода
public sealed record AudioThreadEvent(DateTime At, string Kind, string Text, string? ThreadId = null, string? JobId = null);

public static class AudioThreadEventKinds
{
    public const string Launched = "launched";
    // Результаты запуска стали версиями (или запуск кончился без них)
    public const string Versions = "versions";
    // Правка без ИИ завела версию
    public const string Edited = "edited";
    public const string Saved = "saved";
    public const string Interrupted = "interrupted";
}

public enum AudioThreadWriteStatus
{
    Ok,
    // Ревизия устарела — State несёт актуальное состояние
    Conflict,
    // Нити с таким id в этом чате нет (чужая неотличима от несуществующей)
    ThreadNotFound,
    // Версии нет в этой нити
    VersionNotFound,
    // Запись не подходит: удалить нить с версиями или идущим запуском, неизвестная роль файла
    // или режим настроек
    Invalid,
}

// Thread — нить после записи; NewVersions — версии, которые завела запись
public sealed record AudioThreadWrite(AudioThreadWriteStatus Status, AudioThreadsState State)
{
    public AudioThread? Thread { get; init; }
    // Создание не завело новую нить, а взяло в работу уже существующую по тому же файлу
    public bool Existing { get; init; }
    public IReadOnlyList<AudioThreadVersion> NewVersions { get; init; } = [];
}
