namespace ClaudeHomeServer.HandsBridge.Browser.Launch;

/// <summary>Занятость профиля: <see cref="Detail"/> — по-русски, для журнала моста.</summary>
public sealed record ChromeProfileState(bool Busy, string Detail);

/// <summary>
/// Проверка занятости профиля ДО запуска Chrome. Запуск в занятый профиль не падает: новый
/// процесс отдаёт командную строку уже открытому Chrome и выходит, труба рвётся, а причина
/// теряется. Признаков два, и хватает любого: <c>lockfile</c> профиля держит живой Chrome
/// (открыть его эксклюзивно нельзя) и скрытое окно синглтона <c>Chrome_MessageWindow</c> с
/// заголовком, равным пути профиля. Окно ищет мост через WinAPI, сюда приходит делегат.
/// </summary>
public static class ChromeProfileLock
{
    public const string LockFileName = "lockfile";

    /// <summary>Отказ инструмента: профиль открыт человеком (или чужим Chrome) в обход руки.</summary>
    public const string BusyRefusal =
        "The browser profile of this project is already open in another Chrome window: " +
        "close that Chrome window and try again.";

    public static ChromeProfileState Check(string profileDirectory, Func<string, bool> messageWindowExists)
    {
        var (lockBusy, lockDetail) = LockFileState(Path.Combine(profileDirectory, LockFileName));
        var window = messageWindowExists(profileDirectory);
        return new ChromeProfileState(lockBusy || window,
            $"{lockDetail}; окно Chrome_MessageWindow «{profileDirectory}»: {(window ? "есть" : "нет")}");
    }

    private static (bool Busy, string Detail) LockFileState(string lockFile)
    {
        if (!File.Exists(lockFile))
            return (false, "lockfile нет");

        try
        {
            // Оставшийся от убитого Chrome lockfile открывается — профиль свободен
            using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return (false, "lockfile есть, открылся эксклюзивно");
        }
        catch (FileNotFoundException)
        {
            return (false, "lockfile исчез при проверке");
        }
        catch (IOException ex)
        {
            return (true, $"lockfile держат: {ex.Message.Trim()} (HResult 0x{ex.HResult:X8})");
        }
        catch (UnauthorizedAccessException ex)
        {
            return (true, $"lockfile недоступен: {ex.Message.Trim()}");
        }
    }
}
