using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Mcp.Catalog;

/// <summary>
/// Декларация server.json официального реестра → карточка поиска с предзаполнением
/// формы заведения. Здесь вся защита импорта (план «Каталог MCP-серверов», волна 1):
/// каталог — источник предложения, а не доверия, поэтому всё, что мы не умеем показать
/// человеку честно, делает карточку неподключаемой (Connectable=false + Notice),
/// а не съедается молча. Исключения не бросает: любой мусор в декларации — пометка.
/// </summary>
public static class McpCatalogMapper
{
    public const string OfficialMetaKey = "io.modelcontextprotocol.registry/official";

    // npm-имя пакета (https://github.com/npm/validate-npm-package-name): отсекает
    // git+https://…, *.tgz, file:… и npm:-алиасы одной веткой
    private static readonly Regex NpmNamePattern =
        new(@"^(@[a-z0-9-~][a-z0-9-._~]*/)?[a-z0-9-~][a-z0-9-._~]*$", RegexOptions.Compiled);

    // Имя проекта PyPI: буква/цифра в начале и в конце, внутри [. _ -] (правила PyPI).
    // Отсекает git+https://…, *.tgz, file:… и версию-диапазон, вписанную в имя (pkg>=1)
    private static readonly Regex PyPiNamePattern =
        new(@"^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    // Точный semver (без диапазонов ^~&gt;= и dist-тегов latest/next); prerelease/build допустимы
    private static readonly Regex SemVerPattern =
        new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    // Имя заголовка — token по RFC 7230 (без разделителей и непечатных)
    private static readonly Regex HeaderTokenPattern =
        new(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$", RegexOptions.Compiled);

    // Шаблонная переменная в url: {COMPANY}, {port}
    private static readonly Regex UrlVarPattern = new(@"\{([A-Za-z0-9_.-]+)\}", RegexOptions.Compiled);

    // Env, меняющая исполнение узла/сети/секреты. Это не граница доверия (npx и так
    // выполняет код пакета), а отказ переносить то, что человек не сможет разглядеть
    // в предпросмотре строки запуска. Сравнение префиксное, без учёта регистра.
    private static readonly string[] EnvPrefixBlacklist =
        ["NODE_", "NPM_CONFIG_", "npm_config_", "LD_", "DYLD_", "PIP_", "UV_", "ANTHROPIC_", "CLAUDE_"];

    private static readonly string[] EnvExactBlacklist =
        ["PATH", "SSL_CERT_FILE", "GIT_SSH_COMMAND", "BASH_ENV", "PERL5OPT", "RUBYOPT", "JAVA_TOOL_OPTIONS"];

    // Заголовки, которыми можно сломать сам запрос к серверу (маршрутизация, длина,
    // соединение): их импорт — не предзаполнение, а подмена транспорта
    private static readonly string[] HeaderBlacklist =
        ["Host", "Connection", "Transfer-Encoding", "Content-Length"];

    private const int KeyMaxLength = 40;
    private const int LabelMaxLength = 120;
    private const int DescriptionMaxLength = 600;

    /// <summary>
    /// Ответ реестра <c>{ servers: [...], metadata: { nextCursor } }</c> → страница карточек:
    /// записи со статусом deleted отбрасываются, дубли версий одного name схлопываются
    /// (приоритет isLatest, затем поздний publishedAt).
    /// </summary>
    public static McpCatalogSearchResult MapSearchResponse(JsonElement root)
    {
        var servers = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("servers", out var serversNode) && serversNode.ValueKind == JsonValueKind.Array
            ? serversNode.EnumerateArray().ToList() : [];

        var byName = new Dictionary<string, McpCatalogCardDto>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var entry in servers)
        {
            var card = MapEntry(entry);
            if (card is null) continue;
            if (!byName.ContainsKey(card.Name)) order.Add(card.Name);
            // Дедуп по name: isLatest бьёт всё, иначе свежайший publishedAt, иначе первый
            if (byName.TryGetValue(card.Name, out var kept))
            {
                if (kept.IsLatest && !card.IsLatest) continue;
                if (kept.IsLatest == card.IsLatest && FirstIsFresher(kept, card)) continue;
            }
            byName[card.Name] = card;
        }
        var cards = order.Select(n => byName[n]).ToList();

        var nextCursor = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("nextCursor", out var cursor) && cursor.ValueKind == JsonValueKind.String
            ? cursor.GetString() : null;
        return new McpCatalogSearchResult(cards, string.IsNullOrEmpty(nextCursor) ? null : nextCursor);
    }

    // kept опубликован не позже card? Известный publishedAt «новее» неизвестного
    private static bool FirstIsFresher(McpCatalogCardDto kept, McpCatalogCardDto card) =>
        (kept.PublishedAt ?? DateTime.MinValue) >= (card.PublishedAt ?? DateTime.MinValue);

    /// <summary>
    /// Разбор ответа GET /v0.1/servers/{name}/versions/latest (ревизия, волна 2):
    /// статус — из официального блока _meta, версия — из server.version. Мусорный
    /// корень даёт (null, null): «проверить не удалось» на стороне клиента решается
    /// по исключению разбора, а здесь остаются только честные отсутствия полей.
    /// </summary>
    public static (string? Status, string? Version) MapLatestVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, null);
        var status = CleanText(Str(OfficialMeta(root), "status"), 40)?.ToLowerInvariant();
        var version = root.TryGetProperty("server", out var server)
            && server.ValueKind == JsonValueKind.Object
            ? CleanText(Str(server, "version"), 40) : null;
        return (status, version);
    }

    /// <summary>Одна запись ответа (<c>{ server, _meta }</c>) → карточка; null — пропустить.</summary>
    public static McpCatalogCardDto? MapEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (!entry.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object)
            return null;
        var name = NameOf(entry);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var meta = OfficialMeta(entry);
        var status = Str(meta, "status") ?? "unknown";
        if (status.Equals("deleted", StringComparison.OrdinalIgnoreCase)) return null;

        var title = CleanText(Str(server, "title"), LabelMaxLength);
        var description = CleanText(Str(server, "description"), DescriptionMaxLength);
        var repositoryUrl = server.TryGetProperty("repository", out var repo)
            && repo.ValueKind == JsonValueKind.Object ? Str(repo, "url") : null;
        var publishedAt = DateTimeOffset.TryParse(Str(meta, "publishedAt"),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published)
            ? (DateTime?)published.UtcDateTime : null;
        var isLatest = Bool(meta, "isLatest");

        var deprecated = status.Equals("deprecated", StringComparison.OrdinalIgnoreCase);
        McpCatalogPrefillDto? prefill = null;
        string? notice = null;
        if (!deprecated)
        {
            // remotes безопаснее локального процесса — если есть годный remote, предлагаем его;
            // stdio-пакет — запасной путь. Отказы копим: если не годится ничего, покажем первый
            (prefill, notice) = MapRemote(server, name, title, description);
            if (prefill is null)
            {
                var (pkgPrefill, pkgNotice) = MapPackage(server, name, title, description);
                if (pkgPrefill is not null) (prefill, notice) = (pkgPrefill, null);
                else notice ??= pkgNotice;
            }
            else notice = null;
        }
        else notice = "Автор пометил сервер устаревшим. Подключить из каталога нельзя — если он всё-таки нужен, добавьте вручную";

        return new McpCatalogCardDto(
            Name: name, Title: title, Description: description, RepositoryUrl: repositoryUrl,
            Version: prefill?.VersionOf() ?? CleanText(Str(server, "version"), 40),
            PublishedAt: publishedAt, Status: status.ToLowerInvariant(), IsLatest: isLatest,
            Connectable: prefill is not null, Notice: prefill is not null ? null : (notice ?? "Этот сервер нельзя подключить из каталога: в его описании не хватает данных для настройки"),
            Prefill: prefill);
    }

    // ── remotes[] → Http/Sse ─────────────────────────────────────────────────────────

    private static (McpCatalogPrefillDto? Prefill, string? Notice) MapRemote(
        JsonElement server, string name, string? title, string? description)
    {
        if (!server.TryGetProperty("remotes", out var remotes) || remotes.ValueKind != JsonValueKind.Array)
            return (null, null);
        string? firstNotice = null;
        foreach (var remote in remotes.EnumerateArray())
        {
            if (remote.ValueKind != JsonValueKind.Object) continue;
            var type = Str(remote, "type")?.ToLowerInvariant();
            var transport = type switch
            {
                "http" or "streamable-http" => "http",
                "sse" => "sse",
                _ => null,
            };
            var url = Str(remote, "url");
            if (transport is null || string.IsNullOrWhiteSpace(url))
            {
                firstNotice ??= "Сервер подключается способом, которого AI Home пока не умеет";
                continue;
            }
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                firstNotice ??= "Адрес сервера начинается с http:// — по незащищённому каналу ваш ключ уехал бы открытым текстом";
                continue;
            }

            // Заголовки: имя — token, без чёрного списка (Host/Proxy-*/Connection…)
            var fields = new List<McpCatalogFieldDto>();
            var secretNames = new HashSet<string>(StringComparer.Ordinal);
            var headers = remote.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Array
                ? h.EnumerateArray().ToList() : [];
            foreach (var header in headers)
            {
                var headerName = Str(header, "name");
                if (string.IsNullOrWhiteSpace(headerName)
                    || !HeaderTokenPattern.IsMatch(headerName)
                    || HeaderBlacklist.Contains(headerName, StringComparer.OrdinalIgnoreCase)
                    || headerName.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                {
                    firstNotice ??= $"Сервер просит служебный заголовок «{headerName}» — такие мы не отправляем";
                    goto nextRemote;
                }
                var secret = Bool(header, "isSecret");
                if (secret) secretNames.Add(headerName);
                fields.Add(new McpCatalogFieldDto("header", headerName,
                    CleanText(Str(header, "description"), DescriptionMaxLength),
                    Required: false, Secret: secret, Default: null));
            }

            // Секретные имена env этого remote тоже могут встать в url — см. ниже
            if (server.TryGetProperty("packages", out var pkgs) && pkgs.ValueKind == JsonValueKind.Array)
                foreach (var pkg in pkgs.EnumerateArray())
                    foreach (var env in EnvVarsOf(pkg))
                        if (env.Secret) secretNames.Add(env.Name);

            // Шаблонные переменные url → поля формы; секрет в url не импортируем:
            // модель секретов URL не поддерживает (утечёт в строку запуска и логи)
            var urlVars = UrlVarPattern.Matches(url);
            foreach (Match match in urlVars)
            {
                var varName = match.Groups[1].Value;
                if (secretNames.Contains(varName))
                {
                    firstNotice ??= "Ключ пришлось бы вписать прямо в адрес сервера — так он попал бы в резервную копию открытым. Такие серверы из каталога не подключаем";
                    goto nextRemote;
                }
                fields.Insert(0, new McpCatalogFieldDto("url", varName, null,
                    Required: true, Secret: false, Default: null));
            }

            return (new McpCatalogPrefillDto(
                Key: SlugOf(name), Label: title, Description: description, Transport: transport,
                Command: null, Args: [], Url: url.Trim(), Fields: fields), null);
            nextRemote: ;
        }
        return (null, firstNotice);
    }

    // ── packages[] npm/pypi → Stdio ──────────────────────────────────────────────────

    private static (McpCatalogPrefillDto? Prefill, string? Notice) MapPackage(
        JsonElement server, string name, string? title, string? description)
    {
        if (!server.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            return (null, null);
        string? firstNotice = null;
        foreach (var pkg in packages.EnumerateArray())
        {
            if (pkg.ValueKind != JsonValueKind.Object) continue;

            // npm и pypi — две экосистемы, которые умеем запускать; рантайм у каждой свой
            var registryType = Str(pkg, "registryType")?.ToLowerInvariant();
            var runtime = registryType switch { "npm" => "npx", "pypi" => "uvx", _ => null };
            if (runtime is null)
            {
                // Для oci реестр имеет в виду Docker-образ — человеку полезнее прямое слово,
                // а не кодовое «oci»; прочие registryType показываем как есть
                firstNotice ??= registryType == "oci"
                    ? "Сервер поставляется как Docker-образ — в этой версии AI Home так подключать нельзя"
                    : $"Сервер поставляется в формате «{registryType}» — в этой версии AI Home так подключать нельзя";
                continue;
            }
            // пакеты с http/sse-транспортом поднимают адрес сами ({port} локально) —
            // как stdio их не запустить, а адрес придумал пакет, не реестр
            if (pkg.TryGetProperty("transport", out var transport) && transport.ValueKind == JsonValueKind.Object
                && !string.Equals(Str(transport, "type"), "stdio", StringComparison.OrdinalIgnoreCase))
                continue;

            // runtimeHint: спека его не требует — у обоих реестров отсутствие трактуем
            // как дефолт своей экосистемы (npx/uvx). Любой другой рантайм (docker,
            // bunx…) — не наш: как запускать пакет, мы не знаем
            var runtimeHint = Str(pkg, "runtimeHint")?.Trim().ToLowerInvariant();
            if (runtimeHint is not null && runtimeHint != runtime)
            {
                firstNotice ??= $"Сервер запускается через «{runtimeHint}» — в этой версии AI Home так нельзя";
                continue;
            }

            var identifier = Str(pkg, "identifier")?.Trim() ?? "";
            var nameValid = runtime == "uvx" ? PyPiNamePattern.IsMatch(identifier)
                : NpmNamePattern.IsMatch(identifier);
            if (!nameValid)
            {
                firstNotice ??= $"В имени пакета «{identifier.Crop(60)}» вместо обычного имени ссылка или путь — код скачался бы из чужого места";
                continue;
            }
            // PyPI-имя нормализуем по PEP 503: серии «-_.» и регистр схлопываются —
            // предпросмотр показывает каноническое имя, которое и запустит uvx
            if (runtime == "uvx") identifier = NormalizePyPiName(identifier);
            var version = Str(pkg, "version")?.Trim();
            if (version is null || !SemVerPattern.IsMatch(version))
            {
                firstNotice ??= $"Автор не зафиксировал версию («{version ?? "—"}»): такой сервер обновлялся бы сам, без вашего ведома. Подключить нельзя";
                continue;
            }

            // runtimeArguments — жёсткий allow-list, у каждого рантайма свой: npx —
            // только молчаливое подтверждение -y; uvx — пуст (у него нет ни
            // подтверждений, ни безобидных флагов: --from/--with/--index/--index-url/
            // --extra-index-url/--find-links подменяют источник пакета ровно как
            // --registry у npx). Всё прочее — предпросмотр строки перестал бы
            // описывать то, что реально запустится
            var args = new List<string>();
            var runtimeArgs = ArrayOf(pkg, "runtimeArguments");
            foreach (var runtimeArg in runtimeArgs)
            {
                if (Bool(runtimeArg, "isSecret"))
                {
                    firstNotice ??= "Ключ пришлось бы передать прямо в строке запуска — так он попал бы в резервную копию открытым. Такие серверы из каталога не подключаем";
                    goto nextPackage;
                }
                if (runtime == "npx"
                    && string.Equals(Str(runtimeArg, "type"), "positional", StringComparison.OrdinalIgnoreCase)
                    && Str(runtimeArg, "value") == "-y")
                {
                    args.Add("-y");
                    continue;
                }
                firstNotice ??= $"Сервер просит запускаться с флагом «{Str(runtimeArg, "value") ?? Str(runtimeArg, "name")}» — он подменяет источник кода, поэтому подключить нельзя";
                goto nextPackage;
            }

            var fields = new List<McpCatalogFieldDto>();
            foreach (var env in EnvVarsOf(pkg))
            {
                if (EnvBlacklisted(env.Name))
                {
                    firstNotice ??= $"Сервер просит переменную «{env.Name}» — ей можно подменить исполняемый код, поэтому подключить нельзя";
                    goto nextPackage;
                }
                // Значение по умолчанию переносим только у несекретных: секретных
                // значений из реестра не принимает ни одно поле (план, принцип 3)
                fields.Add(new McpCatalogFieldDto("env", env.Name,
                    CleanText(env.Description, DescriptionMaxLength),
                    env.Required, env.Secret, env.Secret ? null : env.Default));
            }

            args.Add($"{identifier}@{version}");
            var positionalIndex = 0;
            foreach (var arg in ArrayOf(pkg, "packageArguments"))
            {
                if (Bool(arg, "isSecret"))
                {
                    firstNotice ??= "Ключ пришлось бы передать прямо в строке запуска — так он попал бы в резервную копию открытым. Такие серверы из каталога не подключаем";
                    goto nextPackage;
                }
                var isNamed = string.Equals(Str(arg, "type"), "named", StringComparison.OrdinalIgnoreCase);
                var argName = Str(arg, "name");
                var default_ = Str(arg, "default");
                // isRepeated (встречается в спеке, в живом реестре — 0 из ~600 аргументов):
                // одно значение на поле; кратность на форме не моделируем
                if (isNamed && !string.IsNullOrWhiteSpace(argName))
                {
                    args.Add("--" + argName.Trim().ToLowerInvariant());
                    if (default_ is not null) args.Add(default_);
                    else
                    {
                        args.Add("{" + argName.Trim() + "}");
                        fields.Add(new McpCatalogFieldDto("args", argName.Trim(),
                            CleanText(Str(arg, "description"), DescriptionMaxLength),
                            Bool(arg, "isRequired"), Secret: false, Default: null));
                    }
                }
                else
                {
                    positionalIndex++;
                    if (default_ is not null) args.Add(default_);
                    else
                    {
                        var placeholder = "arg" + positionalIndex.ToString(CultureInfo.InvariantCulture);
                        args.Add("{" + placeholder + "}");
                        fields.Add(new McpCatalogFieldDto("args", placeholder,
                            CleanText(Str(arg, "description"), DescriptionMaxLength),
                            Bool(arg, "isRequired"), Secret: false, Default: null));
                    }
                }
            }

            return (new McpCatalogPrefillDto(
                Key: SlugOf(name), Label: title, Description: description, Transport: "stdio",
                Command: runtime, Args: args, Url: null, Fields: fields), null);
            nextPackage: ;
        }
        return (null, firstNotice);
    }

    // ── общие помощники ──────────────────────────────────────────────────────────────

    private sealed record EnvVar(string Name, string? Description, bool Required, bool Secret, string? Default);

    private static IEnumerable<EnvVar> EnvVarsOf(JsonElement pkg)
    {
        if (!pkg.TryGetProperty("environmentVariables", out var vars) || vars.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var v in vars.EnumerateArray())
        {
            var varName = Str(v, "name");
            if (string.IsNullOrWhiteSpace(varName)) continue;
            yield return new EnvVar(varName, Str(v, "description"),
                Bool(v, "isRequired"), Bool(v, "isSecret"), Str(v, "default"));
        }
    }

    private static bool EnvBlacklisted(string name)
    {
        if (name.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var exact in EnvExactBlacklist)
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in EnvPrefixBlacklist)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // PEP 503: регистр не значим, серия «-_.» эквивалентна одному «-» (My_Pkg.js →
    // my-pkg-js). Валидацию имени делает PyPiNamePattern — здесь только канонизация
    private static string NormalizePyPiName(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[-_.]+", "-");

    /// <summary>
    /// Ключ записи: slug последнего сегмента name (io.github.owner/filesystem → filesystem),
    /// обрезка до 40 по формату ключа реестра. Зарезервированность и коллизии проверяет
    /// Create как обычно — это предложение, а не гарантия.
    /// </summary>
    public static string SlugOf(string name)
    {
        var lastSegment = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        var slug = new string(lastSegment.ToLowerInvariant()
            .Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' ? c : '-')
            .ToArray());
        slug = slug.Trim('-');
        if (slug.Length > KeyMaxLength) slug = slug[..KeyMaxLength].Trim('-');
        return slug.Length == 0 ? "server" : slug;
    }

    // title/description/label: без переводов строк и управляющих символов, с обрезкой
    private static string? CleanText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = new string(text.Select(c =>
            c is '\r' or '\n' or '\t' ? ' ' : char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length > maxLength ? cleaned[..maxLength].TrimEnd() : cleaned;
    }

    private static string? NameOf(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (!entry.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object)
            return null;
        var name = Str(server, "name");
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static JsonElement OfficialMeta(JsonElement entry) =>
        entry.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
        && meta.TryGetProperty(OfficialMetaKey, out var official) && official.ValueKind == JsonValueKind.Object
            ? official : default;

    private static List<JsonElement> ArrayOf(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList() : [];

    private static string? Str(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool Bool(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

file static class StringExtensions
{
    // Обрезка мусорных имён для текста ошибки: без ожидания длины от реестра
    public static string Crop(this string? text, int maxLength) =>
        text is null ? "—"
            : text.Length <= maxLength ? text : text[..maxLength] + "…";
}

file static class PrefillVersionExtensions
{
    // Версия карточки = версия выбранного артефакта; у stdio она же уходит в CatalogRef.
    // Это первый аргумент вида pkg@version (npx -y pkg@1.2.3 — или сразу pkg@1.2.3)
    public static string? VersionOf(this McpCatalogPrefillDto prefill)
    {
        if (prefill.Transport != "stdio") return null;
        foreach (var arg in prefill.Args)
        {
            var at = arg.LastIndexOf('@');
            if (at > 0) return arg[(at + 1)..];
        }
        return null;
    }
}
