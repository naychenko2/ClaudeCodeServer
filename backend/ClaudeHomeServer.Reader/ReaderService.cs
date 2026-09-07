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
    /// Проба встраиваемости (ADR-006, §1): headers-only GET тем же клиентом и тем же конвейером
    /// рубежей, что /read; вердикт — по заголовкам финального ответа цепочки редиректов.
    /// Таймаут — только 5 с на заголовки хопа (ADR-005 §5): тело не читается, общая операция
    /// из §5 здесь неприменима. Любая ошибка пробы — <c>embeddable: false</c> с reason из §6,
    /// то есть молчаливый MD-фолбэк, а не 500.
    /// </summary>
    public async Task<EmbedCheckResult> CheckEmbedAsync(string rawUrl, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        EmbedCheckResult result;
        try
        {
            result = await CheckEmbedCoreAsync(rawUrl, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = EmbedCheckResult.No(ReaderErrorCode.Timeout.ToWireName());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ридер: неожиданная ошибка пробы встраиваемости");
            result = EmbedCheckResult.No(ReaderErrorCode.Unreachable.ToWireName());
        }

        LogEmbedCheck(rawUrl, result, sw.Elapsed);
        return result;
    }

    private async Task<EmbedCheckResult> CheckEmbedCoreAsync(string rawUrl, CancellationToken ct)
    {
        var headerTimeout = TimeSpan.FromSeconds(config.GetValue("Reader:HeaderTimeoutSeconds", 5));
        var maxRedirects = config.GetValue("Reader:MaxRedirects", 5);

        var walk = await WalkToFinalResponseAsync(rawUrl, headerTimeout, maxRedirects, ct);
        if (!walk.IsFinal)
            return EmbedCheckResult.No(walk.Error!.Value.ToWireName());

        using var response = walk.Response!;
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
            return EmbedCheckResult.No(StatusReason(status, response));

        // В sandbox-iframe без plugins отрисуются только HTML-документы — PDF и прочее
        // уходят в MD-режим отдельным reason (ADR-006 §1).
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (mediaType is not ("text/html" or "application/xhtml+xml"))
            return EmbedCheckResult.No("not-html");

        if (XFrameOptionsDeny(response) || FrameAncestorsDeny(response))
            return EmbedCheckResult.No("frame-denied");

        return EmbedCheckResult.Yes();
    }

    /// <summary>
    /// reason по статусу финального ответа — коды ADR-005 §6. Тело не читается принципиально,
    /// поэтому маркер бот-щита здесь — только заголовочный (cf-mitigated), без «Just a
    /// moment» в теле.
    /// </summary>
    private static string StatusReason(int status, HttpResponseMessage response)
    {
        if (status is 404 or 410) return ReaderErrorCode.NotFound.ToWireName();
        if (status == 429) return ReaderErrorCode.BlockedBySite.ToWireName();
        if (status is 401 or 403 or 503)
        {
            if (HasBotShieldHeaders(response)) return ReaderErrorCode.BlockedBySite.ToWireName();
            return status == 503 ? ReaderErrorCode.ServerError.ToWireName() : ReaderErrorCode.AuthRequired.ToWireName();
        }
        return ReaderErrorCode.ServerError.ToWireName();
    }

    /// <summary>
    /// X-Frame-Options: ЛЮБОЕ валидное значение (DENY, SAMEORIGIN, устаревший ALLOW-FROM) — запрет.
    /// Невалидные значения (ALLOWALL, мусор) игнорируются — как делают браузеры: строже браузера
    /// быть не нужно (ADR-006 §1).
    /// </summary>
    private static bool XFrameOptionsDeny(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Frame-Options", out var values)) return false;
        foreach (var part in values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (part.Equals("DENY", StringComparison.OrdinalIgnoreCase)) return true;
            if (part.Equals("SAMEORIGIN", StringComparison.OrdinalIgnoreCase)) return true;
            if (IsAllowFrom(part)) return true;
        }
        return false;
    }

    private static bool IsAllowFrom(string token) =>
        token.StartsWith("ALLOW-FROM", StringComparison.OrdinalIgnoreCase) &&
        (token.Length == "ALLOW-FROM".Length || token["ALLOW-FROM".Length] is ' ' or '=');

    /// <summary>
    /// CSP frame-ancestors: только enforced-заголовок Content-Security-Policy (Report-Only не
    /// блокирует и игнорируется). 'none', 'self' и явный список источников — запрет; свой origin
    /// в списке НЕ матчится (инстансов несколько, чужой сайт нас не назовёт, консервативный false
    /// всегда даёт рабочий MD-фолбэк). '*' и схемные вайлдкарды http:/https: — не запрет (ADR-006 §1).
    /// </summary>
    private static bool FrameAncestorsDeny(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Content-Security-Policy", out var policies)) return false;
        // Заголовок может нести несколько политик через запятую — действие должно выполняться
        // для всех, поэтому запрет в ЛЮБОЙ из них запретывает встраивание.
        foreach (var directive in policies
            .SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .SelectMany(p => p.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var spaceIdx = directive.IndexOf(' ');
            var name = spaceIdx < 0 ? directive : directive[..spaceIdx];
            if (!name.Equals("frame-ancestors", StringComparison.OrdinalIgnoreCase)) continue;

            // Пустой список источников запретывает всё, как 'none'.
            var sources = spaceIdx < 0
                ? []
                : directive[(spaceIdx + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var permissive = sources.Any(s =>
                s is "*" || s.Equals("http:", StringComparison.OrdinalIgnoreCase) || s.Equals("https:", StringComparison.OrdinalIgnoreCase));
            if (!permissive) return true;
        }
        return false;
    }

    private void LogEmbedCheck(string rawUrl, EmbedCheckResult result, TimeSpan elapsed)
    {
        // Формула «домен + исход + длительность» — полный URL в лог не идёт никогда (ADR-005 §1).
        var domain = Uri.TryCreate(rawUrl, UriKind.Absolute, out var u) ? u.Host : "?";
        var verdict = result.Embeddable ? "embeddable" : result.Reason;
        logger.LogInformation("Ридер-проба: {Domain} -> {Verdict} за {ElapsedMs} мс",
            domain, verdict, (int)elapsed.TotalMilliseconds);
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
        // Порог «сколько извлечённого текста уже считаем страницей» (см. ParseArticle — там же
        // разбор, почему решение принимается по результату, а не по вердикту SmartReader).
        // 150 — на уровне собственного порога библиотеки MinContentLengthReadearable (140),
        // которым она решает, содержателен ли отдельный абзац: одного настоящего абзаца
        // достаточно. Ниже опускаться нельзя — начнёт проходить навигационная обвязка
        // (замер: страница-меню из семи пунктов даёт 99 символов текста); выше — снова
        // отрежет одноэкранные страницы. ВНИМАНИЕ, эталон протух: обоснование опиралось на
        // example.com со 175 символами, но 2026-09-08 IANA переписала страницу короче — там
        // теперь 127, и порог отвергает её ЗАКОННО. Решение оставить 150 принято осознанно
        // (владелец, 2026-09-08): запас над навигационным мусором (меню в 99 символов) важнее
        // чтения тестовой заглушки, а настоящие одноэкранные страницы — карточки, лендинги,
        // справки — длиннее 150. Двигать порог вниз только по замеру НАСТОЯЩИХ страниц
        // целевого класса, а не по одной заглушке: на 110 до мусора остаётся 11 символов.
        var charThreshold = config.GetValue("Reader:CharThreshold", 150);

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(totalTimeout);

        var walk = await WalkToFinalResponseAsync(rawUrl, headerTimeout, maxRedirects, overallCts.Token);
        if (!walk.IsFinal)
            return ReaderOutcome.Fail(walk.Error!.Value, walk.HttpStatus);

        using var response = walk.Response!;
        return await HandleResponseAsync(walk.Uri, response, maxBodyBytes, maxElemsToParse, charThreshold, overallCts.Token);
    }

    /// <summary>
    /// Общий конвейер рубежей 1–4 (ADR-005, §2) для /read и пробы встраиваемости (ADR-006, §1):
    /// разбор URL (схема http/https, порт 80/443, без userinfo, #fragment отброшен), SsrfGuard,
    /// редиректы ≤ <paramref name="maxRedirects"/> с перепроверкой схемы, порта и адреса НА КАЖДОМ
    /// хопе. Ходит тем же клиентом <see cref="HttpClientName"/> (общий хендлер с /read — без кук,
    /// без прокси, ConnectCallback с SSRF-фильтром). Возвращает финальный ответ, НЕ читая тело:
    /// соединение закрывается после заголовков, ответ утилизирует вызывающий.
    /// </summary>
    private async Task<WalkResult> WalkToFinalResponseAsync(
        string rawUrl, TimeSpan hopTimeout, int maxRedirects, CancellationToken ct)
    {
        if (!TryParseUrl(rawUrl, out var currentUri))
            return WalkResult.Fail(ReaderErrorCode.InvalidUrl);

        var client = httpClientFactory.CreateClient(HttpClientName);
        var redirects = 0;

        while (true)
        {
            var addressCheck = await SsrfGuard.CheckAsync(currentUri, ct);
            if (addressCheck == SsrfGuard.AddressCheck.Private) return WalkResult.Fail(ReaderErrorCode.LocalAddress);
            if (addressCheck == SsrfGuard.AddressCheck.DnsFailed) return WalkResult.Fail(ReaderErrorCode.DnsFailed);

            using var hopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hopCts.CancelAfter(hopTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, hopCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return WalkResult.Fail(ReaderErrorCode.Timeout);
            }
            catch (HttpRequestException ex)
            {
                return WalkResult.Fail(MapHttpException(ex));
            }

            if (!IsRedirect(response.StatusCode))
                return WalkResult.Final(currentUri, response);

            using (response)
            {
                var location = response.Headers.Location;
                // Редирект без разбираемого Location: сайт ОТВЕТИЛ, но ответ негодный — это
                // server-error с кодом, а не «соединиться не удалось» (у сетевого сбоя кода нет
                // и быть не может, иначе текст для модели противоречит сам себе)
                if (location is null) return WalkResult.Fail(ReaderErrorCode.ServerError, (int)response.StatusCode);

                redirects++;
                if (redirects > maxRedirects) return WalkResult.Fail(ReaderErrorCode.TooManyRedirects);

                var next = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!IsAllowedScheme(next) || !IsAllowedPort(next) || HasUserInfo(next))
                    return WalkResult.Fail(ReaderErrorCode.InvalidUrl);

                currentUri = StripFragment(next);
            }
        }
    }

    /// <summary>Итог конвейера хопов: либо финальный ответ (не утилизирован), либо код ошибки ADR-005 §6.</summary>
    private sealed class WalkResult(Uri uri, HttpResponseMessage? response, ReaderErrorCode? error, int? httpStatus)
    {
        public Uri Uri { get; } = uri;
        public HttpResponseMessage? Response { get; } = response;
        public ReaderErrorCode? Error { get; } = error;
        public int? HttpStatus { get; } = httpStatus;
        public bool IsFinal => Response is not null;

        public static WalkResult Final(Uri uri, HttpResponseMessage response) => new(uri, response, null, null);
        public static WalkResult Fail(ReaderErrorCode error, int? httpStatus = null) => new(null!, null, error, httpStatus);
    }

    private async Task<ReaderOutcome> HandleResponseAsync(
        Uri uri, HttpResponseMessage response, int maxBodyBytes, int maxElemsToParse, int charThreshold,
        CancellationToken ct)
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
            return ParseArticle(uri, text, maxElemsToParse, charThreshold, response, status);

        // text/plain, прочие text/* и application/json: контент подконтролен чужому серверу,
        // без экранирования он миновал бы белый список тегов конвертера (см. ADR).
        return ReaderOutcome.Ok(title: uri.Host, siteName: uri.Host, byline: null, markdown: FencePlainText(text));
    }

    private ReaderOutcome ParseArticle(
        Uri uri, string html, int maxElemsToParse, int charThreshold, HttpResponseMessage response, int status)
    {
        if (HasBotShieldHeaders(response) || HasBotShieldBody(html.Length > 4096 ? html[..4096] : html))
            return ReaderOutcome.Fail(ReaderErrorCode.BlockedBySite, status);

        SmartReader.Article article;
        try
        {
            var reader = new SmartReader.Reader(uri.ToString(), html)
            {
                MaxElemsToParse = maxElemsToParse,
                // Тот же порог отдаём и самой библиотеке: её дефолт (500) означал бы, что её
                // собственное «достаточно ли текста» расходится с нашим — при недоборе она
                // переразбирает документ с ослабленными фильтрами очистки. На проверенных
                // фикстурах присваивание разбор не меняет (замер 2026-09-07 при 100/150/500),
                // так что дефект оно НЕ лечит — гейт ниже, в приёмке результата; здесь это
                // согласование контрактов.
                CharThreshold = charThreshold,
            };
            article = reader.GetArticle();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ридер: SmartReader не смог разобрать страницу");
            return ReaderOutcome.Fail(ReaderErrorCode.NotReadable, status);
        }

        // Диагноз «нужен вход» ставится по тем же признакам и в тех же случаях, что и раньше:
        // разведка сказала «не статья», а в разметке есть форма входа.
        if (!article.IsReadable && LooksLikeLoginPage(html))
            return ReaderOutcome.Fail(ReaderErrorCode.AuthRequired, status);

        // А вот отвергать страницу по одному вердикту разведки нельзя: IsReadable — это
        // ПРЕДсказание IsProbablyReaderable, набирающее баллы по длине ОТДЕЛЬНЫХ абзацев
        // (абзац короче 140 символов не считается вовсе, сумма должна перевалить 20 — то есть
        // одному абзацу нужно за 500 символов). Разбор при этом отрабатывает и текст извлекает:
        // у example.com IsReadable = false при 175 символах готового текста. Так терялся целый
        // класс живых страниц — одноэкранные лендинги, карточки товаров, страницы-указатели.
        // Решаем по РЕЗУЛЬТАТУ разбора: сколько текста реально извлечено (замер 2026-09-07 на
        // тех же фикстурах — страница-меню даёт 99 символов, заглушка 5, пустое тело 0).
        var extractedText = (article.TextContent ?? "").Trim();
        if (extractedText.Length < charThreshold)
            return ReaderOutcome.Fail(ReaderErrorCode.NotReadable, status);

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

    /// <summary>
    /// Заголовочный маркер бот-щита — РОВНО один: <c>cf-mitigated</c>. Он появляется только
    /// когда Cloudflare сам погасил запрос (challenge/block). <c>cf-ray</c> сюда не годится
    /// принципиально: это идентификатор запроса, он есть в КАЖДОМ ответе Cloudflare, включая
    /// успешный 200, — а за Cloudflare стоит огромная доля интернета, и по нему ридер
    /// отказывал в чтении почти всему вебу (дефект 2026-09-07, проверено на example.com).
    /// </summary>
    private static bool HasBotShieldHeaders(HttpResponseMessage response) =>
        response.Headers.Contains("cf-mitigated");

    /// <summary>
    /// Телесный маркер бот-щита — <c>&lt;title&gt;</c> страницы-заглушки. Кроме «Just a moment»
    /// (challenge Cloudflare, приходит с кодом 200) сюда входят титулы блок-страниц: их отдают
    /// и Cloudflare с action Block, и Akamai/DataDome/Imperva/PerimeterX — все под 403, который
    /// иначе диагностировался бы как «требуется вход». Эти титулы встречаются только на
    /// страницах отказа, поэтому ложных срабатываний на статьях не дают.
    /// </summary>
    private static bool HasBotShieldBody(string snippet) => BotShieldTitleRegex().IsMatch(snippet);

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

    [GeneratedRegex(
        @"<title[^>]*>\s*(just a moment|attention required|access denied|you have been blocked)",
        RegexOptions.IgnoreCase)]
    private static partial Regex BotShieldTitleRegex();

    [GeneratedRegex("(login|signin|auth|session)", RegexOptions.IgnoreCase)]
    private static partial Regex LoginActionRegex();
}
