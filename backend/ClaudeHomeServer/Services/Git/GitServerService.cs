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

        // Пароль одноразовый: нужен лишь чтобы выпустить токен basic-аутентификацией
        // (создание PAT в Gitea/Forgejo API требует basic, не token). Дальше не храним.
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

        users.SetForgejoAccount(user.Id, login, token);
        user.ForgejoUsername = login;
        user.ForgejoToken = token;
        logger.LogInformation("Forgejo: провижн пользователя {Login} завершён", login);
        return login;
    }

    /// <summary>
    /// Создаёт (или находит существующий) репозиторий под аккаунтом пользователя.
    /// </summary>
    public async Task<ForgejoRepo> CreateRepoAsync(User user, string repoName, CancellationToken ct = default)
    {
        if (!Enabled) throw new GitCommandException("Forgejo не настроен");
        var login = await EnsureUserAsync(user, ct);
        var name = SlugifyRepoName(repoName);

        using var http = Client();
        http.DefaultRequestHeaders.Authorization = TokenAuth(AdminToken);
        var create = await http.PostAsJsonAsync($"admin/users/{login}/repos", new
        {
            name,
            @private = true,
            auto_init = false,
        }, ct);

        if (create.IsSuccessStatusCode)
            return ToRepo(await create.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct), login, name);

        if (create.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await http.GetAsync($"repos/{login}/{name}", ct);
            if (existing.IsSuccessStatusCode)
                return ToRepo(await existing.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct), login, name);
        }
        throw new GitCommandException($"Forgejo: не удалось создать репозиторий {login}/{name} ({(int)create.StatusCode})");
    }

    private ForgejoRepo ToRepo(JsonElement json, string login, string name)
    {
        // clone_url от Forgejo собран из его ROOT_URL и может быть недостижим с бэкенда —
        // строим от своего BaseUrl; html-ссылку для браузера — от PublicUrl
        return new ForgejoRepo($"{BaseUrl}/{login}/{name}.git", $"{PublicUrl}/{login}/{name}");
    }

    private static string SlugifyRepoName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        var slug = sb.ToString().Trim('-', '.');
        return slug.Length > 0 ? slug : "project";
    }
}
