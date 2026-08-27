namespace AiHomeDesktop.App.Settings;

/// <summary>
/// Куда клиент кладёт своё на этой машине. Одна точка правды: каталог профиля WebView2
/// и каталог клиента попадают в deny-list запуска (ADR-008, «desktop_run и журнал»), а
/// разъехавшиеся литералы означали бы deny-list не того каталога.
///
/// Разделение намеренное:
/// <list type="bullet">
/// <item>%APPDATA% — настройки и защищённый DPAPI токен: это учётные данные пользователя,
/// они переезжают вместе с роуминг-профилем;</item>
/// <item>%LOCALAPPDATA% — профиль WebView2 и журнал вызовов: тяжёлый кеш и минутный
/// журнал роумингу не подлежат.</item>
/// </list>
/// </summary>
public static class ClientPaths
{
    private const string FolderName = "AiHomeDesktop";

    /// <summary>Каталог настроек и учётных данных устройства (%APPDATA%\AiHomeDesktop).</summary>
    public static string Roaming { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName));

    /// <summary>Каталог кешей клиента (%LOCALAPPDATA%\AiHomeDesktop).</summary>
    public static string Local { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName));

    /// <summary>Настройки: адрес сервера и имя устройства. Секретов здесь нет.</summary>
    public static string SettingsFile => Path.Combine(Roaming, "settings.json");

    /// <summary>Device-токен под DPAPI CurrentUser — единственный секрет на клиенте.</summary>
    public static string CredentialsFile => Path.Combine(Roaming, "device.bin");

    /// <summary>
    /// Профиль WebView2: логин в веб-морду обязан переживать перезапуск, иначе клиент
    /// заставляет входить заново при каждом старте.
    /// </summary>
    public static string WebViewProfile => Ensure(Path.Combine(Local, "WebView2"));

    /// <summary>Локальный журнал вызовов по callId (TTL — минуты, не история).</summary>
    public static string CallJournalFile => Path.Combine(Local, "calls.json");

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
