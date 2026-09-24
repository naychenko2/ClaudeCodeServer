using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudeHomeServer.Services.Files;

/// <summary>
/// Сверка того, что РЕАЛЬНО открылось (агент устройства, ADR-016 §5): путь проверяют до
/// открытия, но ссылку по дороге могут подменить между проверкой и открытием (TOCTOU).
/// Бросает, если открытый путь вне корня; <paramref name="rootPath"/> — реальный корень.
/// </summary>
public interface IOpenedPathGuard
{
    void Verify(string rootPath, string openedPath);
}

/// <summary>
/// Реальный путь открытого дескриптора: Linux — <c>/proc/self/fd/N</c>, Windows —
/// <c>GetFinalPathNameByHandle</c>. Остальные платформы — отказ: сверка закрыта по
/// умолчанию, а не пропущена молча (агент на macOS пока не поддерживается).
/// </summary>
internal static class OpenedPaths
{
    public static string Of(SafeFileHandle handle)
    {
        var added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            if (OperatingSystem.IsLinux())
                return new FileInfo($"/proc/self/fd/{handle.DangerousGetHandle()}").LinkTarget
                       ?? throw new IOException("Не удалось узнать путь открытого файла (/proc не смонтирован?)");
            if (OperatingSystem.IsWindows()) return WindowsFinalPath(handle);
            throw new PlatformNotSupportedException("Сверка открытого файла на этой платформе не реализована");
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    /// <summary>Дескриптор каталога — только чтобы спросить его реальный путь.</summary>
    public static SafeFileHandle OpenDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var fd = open(path, O_RDONLY | O_CLOEXEC);
            if (fd < 0) throw Errno(Marshal.GetLastPInvokeError(), path);
            return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        }
        if (OperatingSystem.IsWindows())
        {
            var h = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) throw new IOException($"Не удалось открыть каталог {path}", Marshal.GetHRForLastWin32Error());
            return h;
        }
        throw new PlatformNotSupportedException("Сверка открытого каталога на этой платформе не реализована");
    }

    private static Exception Errno(int errno, string path) => errno switch
    {
        ENOENT or ENOTDIR => new DirectoryNotFoundException($"Каталога нет: {path}"),
        EACCES => new UnauthorizedAccessException($"Нет доступа к каталогу {path}"),
        _ => new IOException($"Не удалось открыть каталог {path}: errno {errno}"),
    };

    private static string WindowsFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        while (true)
        {
            var len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (len == 0) throw new IOException("Не удалось узнать путь открытого файла", Marshal.GetHRForLastWin32Error());
            if (len < buffer.Length)
            {
                var path = new string(buffer, 0, (int)len);
                // \\?\C:\… и \\?\UNC\сервер\… — к обычной форме, в которой считан корень
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
                return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
            }
            buffer = new char[len];
        }
    }

    // Linux: флаги одинаковы на x86-64 и arm64 (в отличие от O_DIRECTORY/O_NOFOLLOW)
    private const int O_RDONLY = 0;
    private const int O_CLOEXEC = 0x80000;
    private const int ENOENT = 2;
    private const int EACCES = 13;
    private const int ENOTDIR = 20;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, char[] buffer, uint length, uint flags);
}
