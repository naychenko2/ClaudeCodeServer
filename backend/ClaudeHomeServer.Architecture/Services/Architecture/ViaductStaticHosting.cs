using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>
/// Вклад вертикали в конвейер Main: ветку Viaduct ставит генерический цикл по
/// <see cref="IStaticBranchContributor"/> на прежнем месте — после защитных middleware
/// (forwarded headers, HTTPS-редирект, перехватчик превью-хоста) и до SPA-фолбэка.
/// </summary>
public sealed class ViaductStaticBranch(IConfiguration config) : IStaticBranchContributor
{
    public void Configure(IApplicationBuilder app) =>
        app.UseViaductStatic(ViaductStaticHosting.ResolveDistDir(config));
}

/// <summary>
/// Раздача собранного Viaduct Community (C4-редактор) по пути <see cref="RequestPath"/>.
/// Сборку кладёт скрипт <c>scripts/build-viaduct.ps1</c> в <c>{data}/modules/viaduct/dist</c>;
/// код Viaduct в репу CCS не попадает, а сама папка — воспроизводимый кеш, в бэкап не едет
/// (<c>BackupPaths.ShouldInclude</c>). План встраивания — docs/research/viaduct-embed-plan.md.
///
/// Папки может не быть (модуль не установлен) или она появится/сменится при живом сервере:
/// провайдер ленивый, рестарт после установки или обновления не нужен. Не установлено —
/// любой запрос под путём получает 404 с кодом <see cref="NotInstalledCode"/>, по нему
/// раздел «Архитектура» честно пишет «модуль не установлен».
/// </summary>
public static class ViaductStaticHosting
{
    public const string RequestPath = "/modules/viaduct";
    public const string NotInstalledCode = "viaduct_not_installed";

    // Политика для документа, который открывается в iframe sandbox="allow-scripts" без
    // allow-same-origin: всё только со своего адреса. 'unsafe-inline' у скриптов — ради
    // загрузочного инлайн-скрипта index.html Viaduct; вреда от него в песочнице нет —
    // opaque origin не видит ни куки, ни хранилище CCS. Внешних адресов нет сознательно:
    // телеметрия и Umami выключены на сборке, шрифты и Redoc вендорены.
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "worker-src 'self' blob:; " +
        "frame-src 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'none'; " +
        "frame-ancestors 'self'";

    /// <summary>Каталог сборки: <c>{data}/modules/viaduct/dist</c>.</summary>
    public static string ResolveDistDir(IConfiguration config) =>
        SubsystemHostingExtensions.ResolveDataDir(config, "modules", "viaduct", "dist");

    public static bool IsInstalled(string distDir) => File.Exists(Path.Combine(distDir, "index.html"));

    /// <summary>
    /// Ставит ветку <see cref="RequestPath"/>. Ветка терминальная: промах не проваливается
    /// в SPA-фолбэк фронта CCS (иначе вместо ассета редактора приехал бы index.html CCS).
    /// </summary>
    public static void UseViaductStatic(this IApplicationBuilder app, string distDir)
    {
        var files = new LazyPhysicalFileProvider(distDir);
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

        app.Map(RequestPath, branch =>
        {
            branch.Use(async (ctx, next) =>
            {
                // Роутинг к этому моменту уже мог выбрать SPA-фолбэк CCS (MapFallbackToFile
                // матчит «не-файлы», в том числе корень /modules/viaduct/), а StaticFiles и
                // DefaultFiles при выбранном endpoint молча пропускают запрос. Ветка
                // терминальная — endpoint ей не нужен
                ctx.SetEndpoint(null);
                ApplyCommonHeaders(ctx.Response.Headers);
                if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
                {
                    ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }
                if (!IsInstalled(distDir))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        error = NotInstalledCode,
                        message = "Модуль Viaduct не установлен: соберите его скриптом scripts/build-viaduct.ps1",
                    });
                    return;
                }
                await next();
            });
            branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            branch.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                ContentTypeProvider = contentTypes,
                OnPrepareResponse = ctx => ApplyFileHeaders(ctx.Context, ctx.File.Name),
            });
            branch.Run(ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        });
    }

    private static void ApplyCommonHeaders(IHeaderDictionary headers)
    {
        // ES-модули из opaque origin уходят CORS-запросом с Origin: null — без этого
        // заголовка бандл в песочнице не загрузится вовсе
        headers.AccessControlAllowOrigin = "*";
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
    }

    private static void ApplyFileHeaders(HttpContext ctx, string fileName)
    {
        var headers = ctx.Response.Headers;
        if (fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            // Сборку меняют скриптом при живом сервере — документ всегда свежий
            headers.CacheControl = "no-store, no-cache, must-revalidate";
        }
        else if (ctx.Request.Path.StartsWithSegments("/assets"))
        {
            // Хэши в именах бандлов Vite: кэшируем «вечно»
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
        else
        {
            headers.CacheControl = "no-cache";
        }
    }

    // PhysicalFileProvider бросает на несуществующем корне, а папка сборки появляется и
    // подменяется при живом сервере. Провайдер резолвит пути строкой на каждый запрос,
    // поэтому атомарная подмена папки скриптом (rename) подхватывается без рестарта.
    private sealed class LazyPhysicalFileProvider(string root) : IFileProvider
    {
        private PhysicalFileProvider? _inner;

        private PhysicalFileProvider? Inner
        {
            get
            {
                if (_inner is not null) return _inner;
                if (!Directory.Exists(root)) return null;
                try { return _inner = new PhysicalFileProvider(root); }
                catch (DirectoryNotFoundException) { return null; }
            }
        }

        public IFileInfo GetFileInfo(string subpath) =>
            Inner?.GetFileInfo(subpath) ?? new NotFoundFileInfo(subpath);

        public IDirectoryContents GetDirectoryContents(string subpath) =>
            Inner?.GetDirectoryContents(subpath) ?? NotFoundDirectoryContents.Singleton;

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }
}
