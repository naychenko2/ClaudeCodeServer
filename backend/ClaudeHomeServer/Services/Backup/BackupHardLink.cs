using System.Runtime.InteropServices;

namespace ClaudeHomeServer.Services.Backup;

// Жёсткие ссылки для инкрементальных снимков. В публичном API .NET их нет, поэтому
// P/Invoke: link() из libc на Unix, CreateHardLinkW на Windows.
//
// ИНВАРИАНТ (держится всем кодом снимков, а не только этим классом): файлы внутри
// каталога снимка НИКОГДА не меняются на месте и не переносятся обратно ссылкой — ни
// восстановлением, ни чем-либо ещё. Ссылка делит один inode со всеми снимками, где файл
// не менялся: правка «своего» снимка молча испортила бы все остальные. Восстановление
// только КОПИРУЕТ наружу (File.Copy), удаление снимка — Directory.Delete каталога (уходят
// лишь его имена, данные живут, пока на них есть другие ссылки).
internal static class BackupHardLink
{
    /// <summary>
    /// Создать жёсткую ссылку <paramref name="linkPath"/> на существующий файл
    /// <paramref name="existingPath"/>. false — не вышло (другая ФС, нет прав, платформа
    /// без поддержки): вызывающий обязан упасть назад на честную копию, а не на провал снимка.
    /// </summary>
    public static bool TryCreate(string existingPath, string linkPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                return link(existingPath, linkPath) == 0;
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
