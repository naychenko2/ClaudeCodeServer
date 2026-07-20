using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Git;

// Репозиторий на Forgejo: cloneUrl — для origin (адрес, достижимый с бэкенда/из песочницы),
// htmlUrl — публичная ссылка для deep-link «Открыть в Forgejo».
public sealed record ForgejoRepo(string CloneUrl, string HtmlUrl);

// Обёртка над REST API локального git-сервера Forgejo: провижн пользователей
// (аккаунт + персональный PAT) и создание репозиториев. ТОЛЬКО remote-специфика —
// историю/статус/диффы читает GitService из локального git.
// Без Forgejo:BaseUrl/AdminToken сервис тихо выключен (как Dify без ApiKey).
public sealed class GitServerService(IConfiguration config, IHttpClientFactory httpFactory, UserStore users, ILogger<GitServerService> logger)
{
    private string BaseUrl => (config["Forgejo:BaseUrl"] ?? "").TrimEnd('/');
    private string AdminToken => config["Forgejo:AdminToken"] ?? "";
    // Публичный URL для ссылок в браузере; не задан — совпадает с BaseUrl
    private string PublicUrl => (config["Forgejo:PublicUrl"] ?? BaseUrl).TrimEnd('/');

    public bool Enabled => BaseUrl.Length > 0 && AdminToken.Length > 0;

    /// <summary>
    /// Публичная веб-ссылка из clone-URL: внутренний хост (BaseUrl, напр. localhost:3005)
    /// заменяется на PublicUrl (домен), суффикс .git срезается. Чужой remote — как есть без .git.
    /// </summary>
    public string ToPublicHtmlUrl(string cloneUrl)
    {
        var noGit = cloneUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? cloneUrl[..^4] : cloneUrl;
        if (BaseUrl.Length > 0 && noGit.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
            return PublicUrl + noGit[BaseUrl.Length..];
        return noGit;
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

    // Логин Forgejo из логина приложения: латиница/цифры/дефис (правила имён Gitea/Forgejo)
    private static string SlugifyUsername(string username)
    {
        var sb = new StringBuilder();
        foreach (var c in username.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var slug = sb.ToString().Trim('-');
        return slug.Length > 0 ? slug : "user";
    }

    /// <summary>
    /// Идемпотентный провижн: аккаунт в Forgejo + персональный токен → User.Forgejo*.
    /// Уже провижнен — короткий выход. Возвращает логин Forgejo.
    /// </summary>
    public async Task<string> EnsureUserAsync(User user, CancellationToken ct = default)
    {
        if (!Enabled) throw new GitCommandException("Forgejo не настроен (Forgejo:BaseUrl/AdminToken)");
        if (!string.IsNullOrEmpty(user.ForgejoUsername) && !string.IsNullOrEmpty(user.ForgejoToken))
            return user.ForgejoUsername;

        // Пароль сохраняем в User (открыто — решение владельца, как токен): им пользователь
        // входит в веб-UI Forgejo, иначе приватные репо отдают анониму 404
        var password = RandomNumberGenerator.GetHexString(24, lowercase: true);

        using var http = Client();
        http.DefaultRequestHeaders.Authorization = TokenAuth(AdminToken);

        // Имена вида admin/user/api в Forgejo зарезервированы: создание вернёт 422 при
        // НЕсуществующем пользователе — тогда пробуем вариант с суффиксом.
        var slug = SlugifyUsername(user.Username);
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
            throw new GitCommandException($"Forgejo: не удалось подобрать логин для «{user.Username}»");

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

        users.SetForgejoAccount(user.Id, login, token, password);
        user.ForgejoUsername = login;
        user.ForgejoToken = token;
        user.ForgejoPassword = password;
        logger.LogInformation("Forgejo: провижн пользователя {Login} завершён", login);
        return login;
    }

    /// <summary>Сброс пароля веб-входа (утерян/скомпрометирован) — новый сохраняется в User.</summary>
    public async Task<string> ResetPasswordAsync(User user, CancellationToken ct = default)
    {
        if (!Enabled) throw new GitCommandException("Forgejo не настроен");
        var login = await EnsureUserAsync(user, ct);
        var password = RandomNumberGenerator.GetHexString(24, lowercase: true);
        using var http = Client();
        http.DefaultRequestHeaders.Authorization = TokenAuth(AdminToken);
        var patch = await http.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"admin/users/{login}")
        {
            Content = JsonContent.Create(new { login_name = login, source_id = 0, password, must_change_password = false }),
        }, ct);
        if (!patch.IsSuccessStatusCode)
            throw new GitCommandException($"Forgejo: не удалось сбросить пароль ({(int)patch.StatusCode})");
        users.SetForgejoAccount(user.Id, login, user.ForgejoToken!, password);
        user.ForgejoPassword = password;
        return password;
    }

    /// <summary>
    /// Создаёт (или находит) репозиторий проекта под аккаунтом пользователя. Идемпотентность
    /// и коллизии слагов («Проект» vs «проект!») решаются меткой projectId в description репо:
    /// свой — переиспользуем, чужой с тем же именем — берём слаг с суффиксом -2, -3…
    /// </summary>
    public async Task<ForgejoRepo> CreateRepoAsync(User user, string repoName, string projectId, CancellationToken ct = default)
    {
        if (!Enabled) throw new GitCommandException("Forgejo не настроен");
        var login = await EnsureUserAsync(user, ct);
        var baseName = SlugifyRepoName(repoName);

        using var http = Client();
        http.DefaultRequestHeaders.Authorization = TokenAuth(AdminToken);
        for (var i = 1; i <= 20; i++)
        {
            var name = i == 1 ? baseName : $"{baseName}-{i}";
            var create = await http.PostAsJsonAsync($"admin/users/{login}/repos", new
            {
                name,
                description = $"claude-home:{projectId}",
                @private = true,
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
    // «project», и разные проекты молча цеплялись к одному репозиторию (инцидент на проде 20.07)
    private static string SlugifyRepoName(string name)
    {
        var slug = PersonaManager.Slugify(name);
        return slug.Length > 0 ? slug : "project";
    }
}
