using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudeHomeServer.Services.TestRuns;

// Файлы рабочего дерева агента, которые бэкенд читает и пишет на ХОСТЕ (у container-владельца
// дерево пишет агент из песочницы). Проверка пути и последующее открытие по пути разнесены во
// времени — между ними агент подменяет файл ссылкой, FIFO или устройством. Поэтому:
//  • чтение — один открытый дескриптор: на Linux без блокировки на FIFO (O_NONBLOCK), тип и
//    длина — по дескриптору, реальный путь дескриптора (/proc/self/fd) — внутри реального
//    корня, читается не больше потолка + 1 байт, сколько бы файл ни заявлял о себе;
//  • запись — только новый файл (CreateNew: по готовой ссылке не идёт), каталоги до него без
//    ссылок, после открытия на Linux реальный путь сверяется с корнем, промах — файл удаляется
//    пустым, до записи.
// Windows сверяет путь до и после открытия, но не по дескриптору: ссылки, созданные в
// песочнице на bind-mount Windows-хоста, хост ссылками не видит, а local-агент и так работает
// с правами хоста. Прочие платформы — отказ (чтения нет, записи нет), а не молчаливый пропуск.
internal static class TreeFiles
{
    // Путь лексически в дереве, и ни один существующий сегмент ниже корня (включая сам файл) не
    // ссылка. Сам корень не проверяется: дерево вправе лежать под ссылкой
    internal static bool NoLinksUnder(string workingDirectory, string path)
    {
        var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rest = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (rest == "." || Path.IsPathRooted(rest) || rest == ".." || rest.StartsWith(".." + Path.DirectorySeparatorChar))
            return false;
        var walked = root;
        foreach (var segment in rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Path.Combine(walked, segment);
            // Ссылка на каталог (и junction на Windows) — тоже ссылка
            if (new FileInfo(walked).LinkTarget is not null || new DirectoryInfo(walked).LinkTarget is not null) return false;
            if (!File.Exists(walked) && !Directory.Exists(walked)) return true;
        }
        return true;
    }

    // Содержимое обычного файла дерева не длиннее maxBytes; иначе (нет, не обычный файл, FIFO,
    // устройство, пусто, больше потолка, на Linux — открылся вне корня) — null
    internal static byte[]? ReadInTree(string workingDirectory, string path, int maxBytes)
    {
        using var handle = OpenRead(path);
        if (handle is null) return null;
        if (OperatingSystem.IsLinux() && !OpenedInside(workingDirectory, handle)) return null;
        if ((File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.Device)) != 0) return null;
        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        // FIFO и сокет не перематываются; у устройства (/dev/zero) длина 0
        if (!stream.CanSeek || stream.Length is <= 0 || stream.Length > maxBytes) return null;
        return ReadBounded(stream, maxBytes);
    }

    // То же текстом: кодировка — по BOM, без него UTF-8 (как File.ReadAllText)
    internal static string? ReadTextInTree(string workingDirectory, string path, int maxBytes)
    {
        if (ReadInTree(workingDirectory, path, maxBytes) is not { } bytes) return null;
        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // Не больше maxBytes + 1 байт из потока, сколько бы он ни заявлял о своей длине: длина по
    // fstat у устройства и у дописываемого файла врёт. Больше потолка или пусто — null
    internal static byte[]? ReadBounded(Stream stream, int maxBytes)
    {
        var buffer = new byte[Math.Min(maxBytes + 1, 81920)];
        using var content = new MemoryStream();
        while (content.Length <= maxBytes)
        {
            var want = (int)Math.Min(buffer.Length, maxBytes + 1 - content.Length);
            var read = stream.Read(buffer, 0, want);
            if (read == 0) break;
            content.Write(buffer, 0, read);
        }
        return content.Length is > 0 && content.Length <= maxBytes ? content.ToArray() : null;
    }

    // Новый файл в дереве под запись. null — путь через ссылку, файл уже есть (в том числе
    // подложенная ссылка) или открылся вне корня: писать в дерево нельзя
    internal static FileStream? CreateNewInTree(string workingDirectory, string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return null;
        if (!NoLinksUnder(workingDirectory, path)) return null;
        FileStream stream;
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var inside = OperatingSystem.IsLinux()
            ? OpenedInside(workingDirectory, stream.SafeFileHandle)
            : OperatingSystem.IsWindows() && NoLinksUnder(workingDirectory, path);
        if (inside) return stream;
        // Каталог подменили между проверкой и открытием: файл пуст и создан нами — убрать его
        // там, где он реально оказался, и в дерево не писать
        var real = OperatingSystem.IsLinux() ? RealPath(stream.SafeFileHandle) : null;
        stream.Dispose();
        try { if (real is not null) File.Delete(real); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // Открытие на чтение без ожидания на FIFO; не получилось — null
    private static SafeFileHandle? OpenRead(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var fd = open(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
                return fd < 0 ? null : new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            }
            if (OperatingSystem.IsWindows())
                return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // Linux: реальный путь дескриптора внутри реального пути корня (корень вправе лежать под ссылкой)
    private static bool OpenedInside(string workingDirectory, SafeFileHandle handle)
    {
        var root = RealPath(workingDirectory);
        var file = RealPath(handle);
        return root is not null && file is not null
            && file.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    private static string? RealPath(string directory)
    {
        var fd = open(directory, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0) return null;
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        return RealPath(handle);
    }

    private static string? RealPath(SafeFileHandle handle)
    {
        var added = false;
        handle.DangerousAddRef(ref added);
        try { return new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle()}").LinkTarget; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    // Linux: флаги одинаковы на x86-64 и arm64 (в отличие от O_DIRECTORY/O_NOFOLLOW)
    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_CLOEXEC = 0x80000;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
