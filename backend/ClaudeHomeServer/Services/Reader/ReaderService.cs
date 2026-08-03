using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ClaudeHomeServer.Services.Reader;

/// <summary>
/// Серверная часть режима чтения ссылок (ADR-005): скачивает страницу сам (не через
/// загрузчик SmartReader — тот обошёл бы SSRF-проверку), рубежи адресов проверяются
/// на КАЖДОМ хопе редиректа, извлечение — SmartReader (порт Readability), провод — markdown
/// через белый список тегов (<see cref="HtmlToMarkdownConverter"/>). Кеша нет намеренно.
/// </summary>
public sealed partial class ReaderService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<ReaderService> logger)
{
    public const string HttpClientName = "link-reader";

    public async Task<ReaderOutcome> ReadAsync(string rawUrl, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ReaderOutcome outcome;
        try
        {
            outcome = await ReadCoreAsync(rawUrl, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = ReaderOutcome.Fail(ReaderErrorCode.Timeout);
        }
        catch (Exception ex)
        {
            // Недоверенный контент чужого сайта не должен уронить эндпоинт 500-й — неизвестная
            // причина трактуется как недоступность, а не как «страница нечитаема».
            logger.LogWarning(ex, "Ридер: неожиданная ошибка обработки");
            outcome = ReaderOutcome.Fail(ReaderErrorCode.Unreachable);
        }

        LogOutcome(rawUrl, outcome, sw.Elapsed);
        return outcome;
    }

    /// <summary>
    /// Проксирует картинку статьи через тот же SsrfGuard (продуктовое решение поверх ADR-005 —
    /// без него браузер человека ходил бы за картинкой на CDN сайта напрямую своим IP, и обещание
    /// «сайт видит только сервер» держалось бы только для текста). null — любой отказ; коду вызова
    /// достаточно 404/502, отдельная таксономия ошибок (как у /read) картинке не нужна.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> ReadImageAsync(string rawUrl, CancellationToken ct)
    {
        try
        {
            return await ReadImageCoreAsync(rawUrl, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ридер: не удалось проксировать картинку статьи");
            return null;
        }
    }

    private async Task<(byte[] Bytes, string ContentType)?> ReadImageCoreAsync(string rawUrl, CancellationToken ct)
    {
        var headerTimeout = TimeSpan.FromSeconds(config.GetValue("Reader:HeaderTimeoutSeconds", 5));
        var totalTimeout = TimeSpan.FromSeconds(config.GetValue("Reader:TotalTimeoutSeconds", 10));
        var maxBytes = config.GetValue("Reader:MaxImageBytes", 8 * 1024 * 1024);
        var maxRedirects = config.GetValue("Reader:MaxRedirects", 5);

        if (!TryParseUrl(rawUrl, out var currentUri)) return null;

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(totalTimeout);

        var client = httpClientFactory.CreateClient(HttpClientName);
        var redirects = 0;

        while (true)
        {
            if (await SsrfGuard.CheckAsync(currentUri, overallCts.Token) != SsrfGuard.AddressCheck.Public)
                return null;

            using var hopCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            hopCts.CancelAfter(headerTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, hopCts.Token);
            }
            catch (HttpRequestException)
            {
                return null;
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null) return null;

                    redirects++;
                    if (redirects > maxRedirects) return null;

                    var next = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    if (!IsAllowedScheme(next) || !IsAllowedPort(next) || HasUserInfo(next)) return null;

                    currentUri = StripFragment(next);
                    continue;
                }

                if (!response.IsSuccessStatusCode) return null;

                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                if (!mediaType.StartsWith("image/", StringComparison.Ordinal)) return null;

                var (bytes, truncated) = await ReadBoundedAsync(response.Content, maxBytes, overallCts.Token);
                if (truncated) return null;

                return (bytes, mediaType);
            }
        }
    }

    private async Task<ReaderOutcome> ReadCoreAsync(string rawUrl, CancellationToken ct)
    {
        var headerTimeout = TimeSpan.FromSeconds(config.GetValue("Reader:HeaderTimeoutSeconds", 5));
        var totalTimeout = TimeSpan.FromSeconds(config.GetValue("Reader:TotalTimeoutSeconds", 10));
        var maxBodyBytes = config.GetValue("Reader:MaxBodyBytes", 2 * 1024 * 1024);
        var maxRedirects = config.GetValue("Reader:MaxRedirects", 5);
        var maxElemsToParse = config.GetValue("Reader:MaxElemsToParse", 100_000);

        if (!TryParseUrl(rawUrl, out var currentUri))
            return ReaderOutcome.Fail(ReaderErrorCode.InvalidUrl);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(totalTimeout);

        var client = httpClientFactory.CreateClient(HttpClientName);
        var redirects = 0;

        while (true)
        {
            var addressCheck = await SsrfGuard.CheckAsync(currentUri, overallCts.Token);
            if (addressCheck == SsrfGuard.AddressCheck.Private) return ReaderOutcome.Fail(ReaderErrorCode.LocalAddress);
            if (addressCheck == SsrfGuard.AddressCheck.DnsFailed) return ReaderOutcome.Fail(ReaderErrorCode.DnsFailed);

            using var hopCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            hopCts.CancelAfter(headerTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, hopCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ReaderOutcome.Fail(ReaderErrorCode.Timeout);
            }
            catch (HttpRequestException ex)
            {
                return ReaderOutcome.Fail(MapHttpException(ex));
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null) return ReaderOutcome.Fail(ReaderErrorCode.Unreachable, (int)response.StatusCode);

                    redirects++;
                    if (redirects > maxRedirects) return ReaderOutcome.Fail(ReaderErrorCode.TooManyRedirects);

                    var next = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    if (!IsAllowedScheme(next) || !IsAllowedPort(next) || HasUserInfo(next))
                        return ReaderOutcome.Fail(ReaderErrorCode.InvalidUrl);

                    currentUri = StripFragment(next);
                    continue;
                }

                return await HandleResponseAsync(currentUri, response, maxBodyBytes, maxElemsToParse, overallCts.Token);
            }
        }
    }

    private async Task<ReaderOutcome> HandleResponseAsync(
        Uri uri, HttpResponseMessage response, int maxBodyBytes, int maxElemsToParse, CancellationToken ct)
    {
        var status = (int)response.StatusCode;

        if (status is 404 or 410) return ReaderOutcome.Fail(ReaderErrorCode.NotFound, status);
        if (status == 429) return ReaderOutcome.Fail(ReaderErrorCode.BlockedBySite, status);

        if (status is 401 or 403 or 503)
        {
            var shielded = HasBotShieldHeaders(response) || HasBotShieldBody(await PeekAsync(response, ct));
            if (shielded) return ReaderOutcome.Fail(ReaderErrorCode.BlockedBySite, status);
            return ReaderOutcome.Fail(status == 503 ? ReaderErrorCode.ServerError : ReaderErrorCode.AuthRequired, status);
        }
        if (status >= 500) return ReaderOutcome.Fail(ReaderErrorCode.ServerError, status);
        if (!response.IsSuccessStatusCode) return ReaderOutcome.Fail(ReaderErrorCode.ServerError, status);

        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (mediaType == "application/pdf") return ReaderOutcome.Fail(ReaderErrorCode.Pdf, status);

        var isHtml = mediaType is "text/html" or "application/xhtml+xml";
        var isMarkdown = mediaType == "text/markdown";
        var isPlainText = mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType == "application/json";
        if (!isHtml && !isMarkdown && !isPlainText)
            return ReaderOutcome.Fail(ReaderErrorCode.NotAPage, status);

        var (bytes, truncated) = await ReadBoundedAsync(response.Content, maxBodyBytes, ct);
        if (truncated) return ReaderOutcome.Fail(ReaderErrorCode.TooLarge, status);

        var encoding = ReaderCharset.Detect(response.Content.Headers.ContentType?.CharSet, bytes);
        var text = encoding.GetString(bytes);

        if (isMarkdown)
            return ReaderOutcome.Ok(title: uri.Host, siteName: uri.Host, byline: null, markdown: text);

        if (isHtml)
            return ParseArticle(uri, text, maxElemsToParse, response, status);

        // text/plain, прочие text/* и application/json: контент подконтролен чужому серверу,
        // без экранирования он миновал бы белый список тегов конвертера (см. ADR).
        return ReaderOutcome.Ok(title: uri.Host, siteName: uri.Host, byline: null, markdown: FencePlainText(text));
    }

    private ReaderOutcome ParseArticle(Uri uri, string html, int maxElemsToParse, HttpResponseMessage response, int status)
    {
        if (HasBotShieldHeaders(response) || HasBotShieldBody(html.Length > 4096 ? html[..4096] : html))
            return ReaderOutcome.Fail(ReaderErrorCode.BlockedBySite, status);

        SmartReader.Article article;
        try
        {
            var reader = new SmartReader.Reader(uri.ToString(), html) { MaxElemsToParse = maxElemsToParse };
            article = reader.GetArticle();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ридер: SmartReader не смог разобрать страницу");
            return ReaderOutcome.Fail(ReaderErrorCode.NotReadable, status);
        }

        if (!article.IsReadable)
        {
            return LooksLikeLoginPage(html)
                ? ReaderOutcome.Fail(ReaderErrorCode.AuthRequired, status)
                : ReaderOutcome.Fail(ReaderErrorCode.NotReadable, status);
        }

        var markdown = ConvertArticleContent(article.Content ?? "");
        if (string.IsNullOrWhiteSpace(markdown))
            return ReaderOutcome.Fail(ReaderErrorCode.NotReadable, status);

        return ReaderOutcome.Ok(article.Title ?? uri.Host, article.SiteName ?? uri.Host, article.Byline, markdown);
    }

    private static string ConvertArticleContent(string contentHtml)
    {
        var doc = new HtmlParser().ParseDocument(contentHtml);
        var root = (IElement?)doc.Body ?? doc.DocumentElement;
        return HtmlToMarkdownConverter.Convert(root);
    }

    private static bool LooksLikeLoginPage(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        if (doc.QuerySelectorAll("input[type=password]").Length > 0) return true;
        return doc.QuerySelectorAll("form[action]")
            .Any(f => LoginActionRegex().IsMatch(f.GetAttribute("action") ?? ""));
    }

    private static async Task<string> PeekAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var (bytes, _) = await ReadBoundedAsync(response.Content, 8192, ct);
            return Encoding.Latin1.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static bool HasBotShieldHeaders(HttpResponseMessage response) =>
        response.Headers.Contains("cf-mitigated") || response.Headers.Contains("cf-ray");

    private static bool HasBotShieldBody(string snippet) => JustAMomentTitleRegex().IsMatch(snippet);

    private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(HttpContent content, int capBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > capBytes)
            {
                var remaining = capBytes - (int)buffer.Length;
                if (remaining > 0) buffer.Write(chunk, 0, remaining);
                return (buffer.ToArray(), true);
            }
            buffer.Write(chunk, 0, read);
        }
        return (buffer.ToArray(), false);
    }

    private static string FencePlainText(string text)
    {
        var maxRun = 0;
        var run = 0;
        foreach (var ch in text)
        {
            if (ch == '`') { run++; maxRun = Math.Max(maxRun, run); }
            else run = 0;
        }
        var fence = new string('`', Math.Max(3, maxRun + 1));
        return $"{fence}\n{text}\n{fence}";
    }

    private static ReaderErrorCode MapHttpException(HttpRequestException ex)
    {
        if (ex.InnerException is ReaderConnectBlockedException blocked)
            return blocked.DnsFailed ? ReaderErrorCode.DnsFailed : ReaderErrorCode.LocalAddress;

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => ReaderErrorCode.DnsFailed,
            HttpRequestError.SecureConnectionError => ReaderErrorCode.TlsInvalid,
            _ => ReaderErrorCode.Unreachable,
        };
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static bool TryParseUrl(string raw, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (!IsAllowedScheme(parsed) || !IsAllowedPort(parsed) || HasUserInfo(parsed)) return false;
        uri = StripFragment(parsed);
        return true;
    }

    private static bool IsAllowedScheme(Uri uri) => uri.Scheme is "http" or "https";

    // Uri.Port возвращает дефолтный порт схемы (80/443), если в строке порт не указан явно
    private static bool IsAllowedPort(Uri uri) => uri.Port is 80 or 443;

    private static bool HasUserInfo(Uri uri) => !string.IsNullOrEmpty(uri.UserInfo);

    private static Uri StripFragment(Uri uri) =>
        uri.Fragment.Length == 0 ? uri : new UriBuilder(uri) { Fragment = "" }.Uri;

    private void LogOutcome(string rawUrl, ReaderOutcome outcome, TimeSpan elapsed)
    {
        // Формула "домен + исход + длительность" — полный URL и текст статьи в лог не идут никогда.
        var domain = Uri.TryCreate(rawUrl, UriKind.Absolute, out var u) ? u.Host : "?";
        if (outcome.Success)
            logger.LogInformation("Ридер: {Domain} -> ok за {ElapsedMs} мс", domain, (int)elapsed.TotalMilliseconds);
        else
            logger.LogInformation("Ридер: {Domain} -> {Code} (HTTP {Status}) за {ElapsedMs} мс",
                domain, outcome.Error!.Value.ToWireName(), outcome.HttpStatus, (int)elapsed.TotalMilliseconds);
    }

    [GeneratedRegex(@"<title[^>]*>\s*just a moment", RegexOptions.IgnoreCase)]
    private static partial Regex JustAMomentTitleRegex();

    [GeneratedRegex("(login|signin|auth|session)", RegexOptions.IgnoreCase)]
    private static partial Regex LoginActionRegex();
}
