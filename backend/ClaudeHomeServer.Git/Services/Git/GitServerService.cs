using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeHomeServer.Services.Git;

// Репозиторий на Forgejo: cloneUrl — для origin (адрес, достижимый с бэкенда/из песочницы),
// htmlUrl — публичная ссылка для deep-link «Открыть в Forgejo».
public sealed record ForgejoRepo(string CloneUrl, string HtmlUrl);

// Обёртка над REST API локального git-сервера Forgejo: провижн пользователей
// (аккаунт + персональный PAT) и создание репозиториев. ТОЛЬКО remote-специфика —
// историю/статус/диффы читает GitService из локального git.
// Без Forgejo:BaseUrl/AdminToken сервис тихо выключен (как Dify без ApiKey).
public sealed class GitServerService(IConfiguration config, IHttpClientFactory httpFactory, IForgejoAccountStore accounts, ILogger<GitServerService> logger)
{
    private string BaseUrl => (config["Forgejo:BaseUrl"] ?? "").TrimEnd('/');
    private string AdminToken => config["Forgejo:AdminToken"] ?? "";
    // Публичный URL для ссылок в браузере; не задан — совпадает с BaseUrl
    private string PublicUrl => (config["Forgejo:PublicUrl"] ?? BaseUrl).TrimEnd('/');

    public bool Enabled => BaseUrl.Length > 0 && AdminToken.Length > 0;

    /// <summary>
    /// Публичная веб-ссылка из clone-URL: внутренний хост (BaseUrl, напр. localhost:3005)
    /// заменяется на PublicUrl (домен), суффикс .git срезается. Чужой remote — как есть без .git.
    /// Граница та же, что у кред (MatchesOrigin): голого строкового префикса мало — под
    /// «http://localhost:3005» подошёл бы чужой «http://localhost:30051/…» и получил бы в
    /// отображаемой ссылке наш хост.
    /// </summary>
    public string ToPublicHtmlUrl(string cloneUrl)
    {
        var noGit = cloneUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? cloneUrl[..^4] : cloneUrl;
        if (MatchesOrigin(noGit, BaseUrl) && noGit.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
            return PublicUrl + noGit[BaseUrl.Length..];
        return noGit;
    }

    /// <summary>
    /// Адрес ведёт на ЭТОТ Forgejo? По нему решается, подставлять ли к remote-операции пару
    /// логин-токен владельца: чужому адресу она не подойдёт, а вместе с ней гасится цепочка
    /// системных credential helper'ов — публикация на сторонний сервер упёрлась бы в 401
    /// без шанса спросить настоящий токен. Forgejo не настроен (пустой BaseUrl) — всегда false.
    /// Сверяются ОБА адреса: внутренний BaseUrl (по нему собран clone URL) и публичный PublicUrl —
    /// именно его человек видит в кнопке «Открыть в Forgejo» и вводит руками в диалоге публикации.
    /// </summary>
    public bool OwnsUrl(string? url) =>
        url is not null && (MatchesOrigin(url, BaseUrl) || MatchesOrigin(url, PublicUrl));

    /// <summary>
    /// Совпадение адреса с нашим по схеме, хосту, порту и префиксу пути (Forgejo может стоять
    /// за реверс-прокси на /git). Строкового префикса тут МАЛО: «https://git.example.com»
    /// начинает и чужой «https://git.example.com.attacker.net/repo.git», а «http://localhost:3000» —
    /// любой «http://localhost:30001/…», и токен с правом записи уехал бы в Basic-заголовке
    /// на посторонний хост.
    /// </summary>
    private static bool MatchesOrigin(string url, string origin)
    {
        if (origin.Length == 0) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
        if (!string.Equals(u.Scheme, o.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(u.Host, o.Host, StringComparison.OrdinalIgnoreCase)) return false;
        if (u.Port != o.Port) return false;   // порт эффективный: у http/https подставлен дефолтный

        var basePath = o.AbsolutePath.TrimEnd('/');
        if (basePath.Length == 0) return true;
        var path = u.AbsolutePath.TrimEnd('/');
        return string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient Client()
    {
        var http = httpFactory.CreateClient("forgejo");
        http.BaseAddress = new Uri(BaseUrl + "/api/v1/");
        http.Timeout = TimeSpan.FromSeconds(20);
        return http;
    }

    private static AuthenticationHeaderValue TokenAuth(string token) => new("token", token);
    private static AuthenticationHeaderValue BasicAuth(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    // Логин Forgejo из логина приложения: латиница/цифры/дефис (правила имён Gitea/Forgejo —
    // надмножество нашего slug-формата, так что общий примитив в них укладывается).
    // Транслит ОБЯЗАТЕЛЕН по той же причине, что у имён репозиториев (инцидент 20.07):
    // `Username` нигде не валидируется, а прежняя ASCII-only копия отправляла КАЖДЫЙ
    // кириллический логин в один и тот же фолбэк «user» — разные пользователи молча
    // цеплялись к одному аккаунту Forgejo. Уже провижненные логины персистятся
    // в `User.ForgejoUsername` (короткий выход в EnsureUserAsync) и сменой алгоритма
    // не затрагиваются.
    private static string SlugifyUsername(string username)
    {
        var slug = Slugifier.Slugify(username, Slugifier.XStyle.H);
        return slug.Length > 0 ? slug : "user";
    }

    /// <summary>
    /// Идемпотентный провижн: аккаунт в Forgejo + персональный токен.
    /// Уже провижнен — короткий выход. Возвращает логин Forgejo.
    /// Мутация <c>User</c> — обязанность вызывающего (он владеет User и читает
    /// результат через <see cref="IForgejoAccountStore.GetAccount"/>).
    /// </summary>
    public async Task<string> EnsureUserAsync(string userId, string username, CancellationToken ct = default)
    {
        if (!Enabled) throw new GitCommandException("Forgejo не настроен (Forgejo:BaseUrl/AdminToken)");
        var existing = accounts.GetAccount(userId);
        if (!string.IsNullOrEmpty(existing?.ForgejoUsername) && !string.IsNullOrEmpty(existing?.ForgejoToken))
            return existing.ForgejoUsername!;

        // Пароль сохраняем в User (открыто — решение владельца, как токен): им пользователь
        // входит в веб-UI Forgejo, иначе приватные репо отдают анониму 404
        var password = RandomNumberGenerator.GetHexString(24, lowercase: true);

        using var http = Client();
        http.DefaultRequestHeaders.Authorization = TokenAuth(AdminToken);

        // Имена вида admin/user/api в Forgejo зарезервированы: создание вернёт 422 при
        // НЕсуществующем пользователе — тогда пробуем вариант с суффиксом.
        var slug = SlugifyUsername(username);
        string? login = null;
        foreach (var candidate in new[] { slug, slug + "-cc" })
        {
            var create = await http.PostAsJsonAsync("admin/users", new
            {
                username = candidate,
                email = $"{candidate}@claude-home.local",
                password,
                must_change_password = false,
            }, ct);
            if (create.IsSuccessStatusCode) { login = candidate; break; }

            if (create.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var exists = await http.GetAsync($"users/{candidate}", ct);
                if (exists.IsSuccessStatusCode)
                {
                    // Пользователь уже есть (повторный провижн) — сбросить пароль на одноразовый
                    var patch = await http.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"admin/users/{candidate}")
                    {
                        Content = JsonContent.Create(new { login_name = candidate, source_id = 0, password, must_change_password = false }),
                    }, ct);
                    if (!patch.IsSuccessStatusCode)
                        throw new GitCommandException($"Forgejo: не удалось обновить пользователя {candidate} ({(int)patch.StatusCode})");
                    login = candidate;
                    break;
                }
                continue; // имя зарезервировано — следующий кандидат
            }
            throw new GitCommandException($"Forgejo: не удалось создать пользователя {candidate} ({(int)create.StatusCode})");
        }
        if (login is null)
            throw new GitCommandException($"Forgejo: не удалось подобрать логин для «{username}»");

        // 2. Выпустить персональный токен от лица пользователя (basic-auth одноразовым паролем).
        //    Имя токена уникальное — прежние «claude-home-*» не мешают повторному провижну.
        using var userHttp = Client();
        userHttp.DefaultRequestHeaders.Authorization = BasicAuth(login, password);
        var tokenResp = await userHttp.PostAsJsonAsync($"users/{login}/tokens", new
        {
            name = $"claude-home-{Guid.NewGuid():N}"[..24],
            scopes = new[] { "write:repository", "read:user" },
        }, ct);
        if (!tokenResp.IsSuccessStatusCode)
            throw new GitCommandException($"Forgejo: не удалось выпустить токен для {login} ({(int)tokenResp.StatusCode})");
        var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var token = tokenJson.GetProperty("sha1").GetString()
            ?? throw new GitCommandException("Forgejo: токен без sha1");

        accounts.SetForgejoAccount(userId, login, token, password);
        logger.LogInformation("Forgejo: провижн пользователя {Login} завершён", login);
        return login;
    }

    /// <summary>Сброс пароля веб-входа (утерян/скомпрометирован) — возвращает новый пароль.</summary>
    public async Task<string> ResetPasswordAsync(string userId, string username, CancellationToken ct = default)
    {
        if (!Enabled) throw new GitCommandException("Forgejo не настроен");
        var login = await EnsureUserAsync(userId, username, ct);
        var password = RandomNumberGenerator.GetHexString(24, lowercase: true);
        using var http = Client();
        http.DefaultRequestHeaders.Authorization = TokenAuth(AdminToken);
        var patch = await http.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"admin/users/{login}")
        {
            Content = JsonContent.Create(new { login_name = login, source_id = 0, password, must_change_password = false }),
        }, ct);
        if (!patch.IsSuccessStatusCode)
            throw new GitCommandException($"Forgejo: не удалось сбросить пароль ({(int)patch.StatusCode})");
        var existing = accounts.GetAccount(userId);
        accounts.SetForgejoAccount(userId, login, existing?.ForgejoToken!, password);
        return password;
    }

    /// <summary>
    /// Создаёт (или находит) репозиторий проекта под аккаунтом пользователя. Идемпотентность
    /// и коллизии слагов («Проект» vs «проект!») решаются меткой projectId в description репо:
    /// свой — переиспользуем, чужой с тем же именем — берём слаг с суффиксом -2, -3…
    /// </summary>
    public async Task<ForgejoRepo> CreateRepoAsync(string userId, string username, string repoName, string projectId, CancellationToken ct = default)
    {
        if (!Enabled) throw new GitCommandException("Forgejo не настроен");
        var login = await EnsureUserAsync(userId, username, ct);
        var baseName = SlugifyRepoName(repoName);

        using var http = Client();
        http.DefaultRequestHeaders.Authorization = TokenAuth(AdminToken);
        for (var i = 1; i <= 20; i++)
        {
            var name = i == 1 ? baseName : $"{baseName}-{i}";
            // private=false по решению владельца: анонимный просмотр внутри инстанса разрешён
            // (приватные репо отдавали 404 без логина в веб-UI)
            var create = await http.PostAsJsonAsync($"admin/users/{login}/repos", new
            {
                name,
                description = $"claude-home:{projectId}",
                @private = false,
                auto_init = false,
            }, ct);
            if (create.IsSuccessStatusCode)
                return ToRepo(login, name);

            if (create.StatusCode == HttpStatusCode.Conflict)
            {
                var existing = await http.GetAsync($"repos/{login}/{name}", ct);
                if (existing.IsSuccessStatusCode)
                {
                    var json = await existing.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    var desc = json.TryGetProperty("description", out var d) ? d.GetString() : null;
                    if (desc == $"claude-home:{projectId}")
                        return ToRepo(login, name);   // репо этого же проекта — переиспользуем
                }
                continue;   // занят другим проектом — следующий суффикс
            }
            throw new GitCommandException($"Forgejo: не удалось создать репозиторий {login}/{name} ({(int)create.StatusCode})");
        }
        throw new GitCommandException($"Forgejo: не удалось подобрать имя репозитория для «{repoName}»");
    }

    private ForgejoRepo ToRepo(string login, string name)
    {
        // clone_url от Forgejo собран из его ROOT_URL и может быть недостижим с бэкенда —
        // строим от своего BaseUrl; html-ссылку для браузера — от PublicUrl
        return new ForgejoRepo($"{BaseUrl}/{login}/{name}.git", $"{PublicUrl}/{login}/{name}");
    }

    // Транслит кириллицы ОБЯЗАТЕЛЕН: «Стратсессия» без него давала пустой слаг → фолбэк
    // «project», и разные проекты молча цеплялись к одному репозиторию (инцидент на проде 20.07).
    // XStyle.H обязателен: значения персистятся (имена репозиториев в Forgejo), смена стиля
    // задним числом сломала бы существующие аккаунты.
    private static string SlugifyRepoName(string name)
    {
        var slug = Slugifier.Slugify(name, Slugifier.XStyle.H);
        return slug.Length > 0 ? slug : "project";
    }
}
