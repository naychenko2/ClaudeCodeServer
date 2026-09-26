using ClaudeHomeServer.Services.Desktop;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Раздача агента устройства (agent-distribution Р10, Р11): скрипты установки, указатель
/// текущей выкатки и архивы версий. Пять ручек плюс HEAD на те же пути.
///
/// Анонимно намеренно: установщик качается ДО сопряжения, когда у машины нет никаких учётных
/// данных, а обновляющийся агент ходит той же ручкой, потому что хеш архива он уже получил по
/// аутентифицированному каналу устройства (Hello). Отдаётся только то, что сервер и так
/// публикует всем: код агента без секретов. От молотьбы — лимит по IP (политика в Program.cs).
///
/// Строки запроса в путь к диску не попадают никогда: RID сверяется с белым списком, версия —
/// с версиями каталога, имя файла — с именем архива в манифесте, а путь собирает каталог из
/// данных манифеста. Скрипты — встроенные ресурсы сборки, а не файлы.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
[Route("agent")]
public sealed class AgentDownloadsController(
    ILogger<AgentDownloadsController> log, AgentReleaseCatalog? catalog = null) : ControllerBase
{
    public const string RateLimitPolicy = "agent-download";

    public const string DesktopDisabled =
        AgentReleaseCatalog.NotServedPrefix + ": подсистема устройств выключена на этом сервере";

    private const string ScriptResourcePrefix = "AgentInstall.";

    [HttpGet("install.ps1")]
    [HttpHead("install.ps1")]
    public IActionResult InstallPs1() => Script("install.ps1");

    [HttpGet("install.sh")]
    [HttpHead("install.sh")]
    public IActionResult InstallSh() => Script("install.sh");

    /// <summary>Указатель текущей выкатки — не каталог: версии, кроме текущей, наружу не перечисляются.</summary>
    [HttpGet("manifest.json")]
    [HttpHead("manifest.json")]
    public IActionResult Manifest()
    {
        if (catalog is null) return Unavailable(DesktopDisabled);
        var snapshot = catalog.Current();
        if (snapshot.Latest is not { } latest) return Unavailable(snapshot.Problem!);

        // Выкатка меняет указатель — кэш у клиента или прокси отдал бы прошлую версию
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            version = latest.Version,
            archives = latest.Archives.Values.ToDictionary(
                a => a.Rid, a => new { file = a.File, size = a.Size, sha256 = a.Sha256 }),
        });
    }

    [HttpGet("{version}/{rid}/{file}")]
    [HttpHead("{version}/{rid}/{file}")]
    public IActionResult Archive(string version, string rid, string file)
    {
        if (catalog is null) return Unavailable(DesktopDisabled);
        var snapshot = catalog.Current();
        if (!snapshot.Served && snapshot.Versions.Count == 0) return Unavailable(snapshot.Problem!);

        var archive = catalog.FindArchive(version, rid, file);
        if (archive is null) return NotFound(new { error = "Такого архива агента нет" });

        Stream stream;
        try
        {
            stream = catalog.OpenArchive(archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Манифест есть, а файла нет — каталог релизов испорчен руками или ротацией
            log.LogWarning(ex, "Архив агента {Path} описан в манифесте, но не читается", archive.RelativePath);
            return NotFound(new { error = "Такого архива агента нет" });
        }

        return File(stream, ContentTypeOf(archive.File), archive.File);
    }

    private IActionResult Script(string name)
    {
        if (catalog is null) return Unavailable(DesktopDisabled);
        var stream = typeof(AgentDownloadsController).Assembly.GetManifestResourceStream(ScriptResourcePrefix + name);
        if (stream is null)
        {
            log.LogError("Встроенный скрипт установки агента {Name} не найден в сборке", name);
            return Unavailable(AgentReleaseCatalog.NotServedPrefix + ": скрипт установки не собран в сервер");
        }
        return File(stream, "text/plain; charset=utf-8");
    }

    // 503 с причиной — честная форма для выключенного и ненастроенного: клиент (установщик,
    // UI) отличает «сервер не раздаёт» от «такой версии нет»
    private ObjectResult Unavailable(string reason) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = reason });

    private static string ContentTypeOf(string file) =>
        file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "application/zip"
        : file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
            ? "application/gzip"
            : "application/octet-stream";
}
