using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Личный реестр MCP-серверов владельца. Не admin: список per-user, чужие записи
// недостижимы — все операции идут по (UserId, id).
[ApiController]
[Authorize]
[Route("api/mcp/servers")]
public class McpServersController(McpRegistry registry, McpSecretStore secrets,
    PersonaBindingsService bindings, McpStatusStore statuses, McpProbeService probe,
    PersonaManager personas, ProjectManager projects) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet]
    public IActionResult List()
    {
        var observed = statuses.GetByOwner(UserId);
        return Ok(registry.GetByOwner(UserId)
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => McpServerMapper.ToDto(r, observed.GetValueOrDefault(r.Key))));
    }

    // Всё, что подключено помимо личного реестра: сервисы продукта (tasks, notes, wsp…),
    // интеграции с внешними системами (dify/fal-ai/glif), выделенные серверы памяти персон
    // (pmem_*) и наследие CLI из глобального .mcp.json/~/.claude.json и плагинов. В реестре
    // их нет, но наблюдения из system/init по ним копятся в том же сторе. Экран показывает
    // их плитками — только статус, без правки и удаления; поле group разносит плитки по
    // группам (см. McpBuiltinGroups). Ключи записей реестра отсюда убраны: их отдаёт List.
    [HttpGet("builtin")]
    public IActionResult Builtin()
    {
        var own = registry.GetByOwner(UserId)
            .Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Живые серверы памяти персон: pmem_<handle> попадает в конфиг хода только персоне
        // с включённой памятью. Наблюдение по удалённой или выключенной персоне — хвост,
        // который больше никогда не обновится; убираем его из выдачи, сам стор не трогая.
        var livePmem = personas.GetByOwner(UserId)
            .Where(p => p.MemoryEnabled)
            .Select(p => PersonaConsultantToolset.PmemServerKey(p.Handle))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Ok(statuses.GetByOwner(UserId)
            .Where(kv => !own.Contains(kv.Key))
            .Where(kv => !kv.Key.StartsWith(McpRegistry.ConsultantMemoryPrefix, StringComparison.OrdinalIgnoreCase)
                || livePmem.Contains(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new
            {
                key = kv.Key,
                group = McpRegistry.BuiltinGroupOf(kv.Key),
                status = new McpServerStatusDto(kv.Value.Status, kv.Value.ObservedAt,
                    kv.Value.Source.ToString().ToLowerInvariant(), kv.Value.SessionId, kv.Value.Error),
            }));
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id) =>
        registry.Get(UserId, id) is { } record
            ? Ok(McpServerMapper.ToDto(record, statuses.Get(UserId, record.Key)))
            : NotFound(new { error = "Сервер не найден" });

    // Разовая проверка «по кнопке»: поднимаем сервер как это сделал бы ход и спрашиваем
    // список инструментов. Результат кладётся в стор наблюдений и возвращается человеку.
    [HttpPost("{id}/probe")]
    public async Task<IActionResult> Probe(string id, CancellationToken ct)
    {
        var record = registry.Get(UserId, id);
        if (record is null) return NotFound(new { error = "Сервер не найден" });
        var result = await probe.ProbeAsync(UserId, record, ct);
        return Ok(new
        {
            ok = result.Ok,
            status = result.Status,
            serverName = result.ServerName,
            toolCount = result.ToolCount,
            toolNames = result.ToolNames,
            error = result.Error,
        });
    }

    [HttpPost]
    public IActionResult Create([FromBody] McpServerUpsertRequest req)
    {
        var draft = new McpServerRecord { Key = req.Key ?? "" };
        if (Apply(draft, req, existing: null) is { } error) return BadRequest(new { error });
        try
        {
            return Ok(McpServerMapper.ToDto(registry.Create(UserId, draft)));
        }
        catch (InvalidOperationException ex)
        {
            // Ключ не прошёл валидацию — секреты черновика остались бы сиротами
            secrets.Remove(UserId, McpRegistry.SecretRefsOf(draft));
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] McpServerUpsertRequest req)
    {
        var existing = registry.Get(UserId, id);
        if (existing is null) return NotFound(new { error = "Сервер не найден" });

        // Черновик собираем от копии: старые ссылки на секреты нужны и для «оставить как было»,
        // и для уборки тех, что после правки стали никому не нужны
        var oldRefs = McpRegistry.SecretRefsOf(existing).ToList();
        var oldKey = existing.Key;
        var draft = new McpServerRecord { Key = req.Key ?? existing.Key };
        if (Apply(draft, req, existing) is { } error) return BadRequest(new { error });

        try
        {
            var updated = registry.Update(UserId, id, draft);
            if (updated is null) return NotFound(new { error = "Сервер не найден" });
            var keptRefs = McpRegistry.SecretRefsOf(updated).ToHashSet(StringComparer.Ordinal);
            secrets.Remove(UserId, oldRefs.Where(r => !keptRefs.Contains(r)));
            // Смена ключа осиротила привязки персон на прежний «mcp:<ключ>» — тот же
            // случай, что и удаление записи: протухший ключ валит следующий bindings_set
            if (!string.Equals(oldKey, updated.Key, StringComparison.OrdinalIgnoreCase))
            {
                bindings.PurgeMcpBindings(UserId, oldKey);
                // Выдачи сервера в проекты (McpServersOn) под старым ключом — та же протухшая
                // выдача: новый сервер под этим ключом унаследовал бы чужие права
                projects.PurgeMcpKey(UserId, oldKey);
                // Наблюдение висело на прежнем ключе — под новым именем оно бы врало
                statuses.Remove(UserId, oldKey);
            }
            return Ok(McpServerMapper.ToDto(updated));
        }
        catch (InvalidOperationException ex)
        {
            var keptRefs = oldRefs.ToHashSet(StringComparer.Ordinal);
            secrets.Remove(UserId, McpRegistry.SecretRefsOf(draft).Where(r => !keptRefs.Contains(r)));
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/enable")]
    public IActionResult Enable(string id, [FromBody] McpEnableRequest req) =>
        registry.SetEnabled(UserId, id, req.Enabled) is { } record
            ? Ok(McpServerMapper.ToDto(record))
            : NotFound(new { error = "Сервер не найден" });

    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        var removed = registry.Delete(UserId, id);
        if (removed is null) return NotFound(new { error = "Сервер не найден" });
        secrets.Remove(UserId, McpRegistry.SecretRefsOf(removed));
        // Привязки персон на этот сервер осиротели: чистим их сразу, иначе следующая
        // полная замена привязок (bindings_set) упала бы на несуществующем ключе
        bindings.PurgeMcpBindings(UserId, removed.Key);
        // Выдачи сервера в проекты (McpServersOn) тоже осиротели: протухшая выдача опаснее
        // протухшего запрета — новый сервер под тем же ключом унаследовал бы чужие права
        projects.PurgeMcpKey(UserId, removed.Key);
        statuses.Remove(UserId, removed.Key);
        return NoContent();
    }

    // Импорт вставленного фрагмента {"mcpServers": {...}}: записи заводятся ВЫКЛЮЧЕННЫМИ,
    // значения env/headers приезжают голыми — пометить их секретами человек может потом,
    // правкой записи. Занятый или невалидный ключ — не ошибка запроса: такая запись
    // пропускается с причиной, остальные заводятся.
    [HttpPost("import")]
    public IActionResult Import([FromBody] JsonElement body)
    {
        var drafts = McpRegistry.ParseImport(body);
        if (drafts.Count == 0)
            return BadRequest(new { error = "В фрагменте нет ни одного описания MCP-сервера" });

        var created = new List<McpServerDto>();
        var skipped = new List<object>();
        foreach (var draft in drafts)
        {
            try { created.Add(McpServerMapper.ToDto(registry.Create(UserId, draft))); }
            catch (InvalidOperationException ex) { skipped.Add(new { key = draft.Key, reason = ex.Message }); }
        }
        return Ok(new { created, skipped });
    }

    // Переносит поля запроса в черновик записи, разруливая секреты:
    // заполненное секретное значение уезжает в McpSecretStore и заменяется плейсхолдером,
    // пустое — наследует плейсхолдер прежней записи (фронт значения секрета не знает).
    // Возвращает текст ошибки или null.
    private string? Apply(McpServerRecord draft, McpServerUpsertRequest req, McpServerRecord? existing)
    {
        draft.Label = req.Label ?? existing?.Label ?? draft.Key;
        draft.Description = req.Description ?? existing?.Description;
        draft.Enabled = req.Enabled ?? existing?.Enabled ?? true;
        draft.AlwaysLoad = req.AlwaysLoad ?? existing?.AlwaysLoad ?? false;
        draft.AllowReadOnlyPersonas = req.AllowReadOnlyPersonas ?? existing?.AllowReadOnlyPersonas ?? false;
        draft.AllowOutsideProjects = req.AllowOutsideProjects ?? existing?.AllowOutsideProjects ?? false;
        draft.Source = existing?.Source ?? McpServerSource.Manual;

        var transportRaw = req.Transport ?? existing?.Transport.ToString() ?? "stdio";
        if (!Enum.TryParse<McpTransport>(transportRaw, ignoreCase: true, out var transport))
            return "Транспорт: stdio, http или sse";
        draft.Transport = transport;

        if (transport == McpTransport.Stdio)
        {
            draft.Command = (req.Command ?? existing?.Command)?.Trim();
            if (string.IsNullOrWhiteSpace(draft.Command)) return "Для stdio нужна команда запуска";
            draft.Args = req.Args ?? existing?.Args;
            draft.Env = MergeValues(req.Env, existing?.Env);
        }
        else
        {
            draft.Url = (req.Url ?? existing?.Url)?.Trim();
            if (string.IsNullOrWhiteSpace(draft.Url)
                || !Uri.TryCreate(draft.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "Для http/sse нужен абсолютный адрес http(s)://";
            draft.Headers = MergeValues(req.Headers, existing?.Headers);
        }

        var authRaw = req.Auth?.Kind ?? existing?.Auth.Kind.ToString() ?? "none";
        if (!Enum.TryParse<McpAuthKind>(authRaw, ignoreCase: true, out var authKind))
            return "Авторизация: none, apikey, bearer или oauth2";
        var auth = new McpAuthConfig
        {
            Kind = authKind,
            HeaderName = req.Auth?.HeaderName ?? existing?.Auth.HeaderName,
            SecretRef = existing?.Auth.SecretRef,
            // OAuth-настройки (issuer, токены) правит только волна 7 (McpOAuthService) —
            // здесь переносим как есть. Единственное исключение — ClientId: его можно
            // вписать заранее, для серверов без автоматической регистрации клиента (DCR),
            // до первого нажатия «Войти».
            OAuth = existing?.Auth.OAuth,
        };
        if (authKind == McpAuthKind.ApiKey && string.IsNullOrWhiteSpace(auth.HeaderName))
            return "Для ключа API нужно имя заголовка";
        if (!string.IsNullOrEmpty(req.Auth?.Secret))
            auth.SecretRef = secrets.Set(UserId, req.Auth.Secret);
        if (authKind is McpAuthKind.ApiKey or McpAuthKind.Bearer && string.IsNullOrEmpty(auth.SecretRef))
            return "Не задано значение ключа или токена";
        if (authKind == McpAuthKind.None) auth.SecretRef = null;
        if (authKind == McpAuthKind.OAuth2 && !string.IsNullOrWhiteSpace(req.Auth?.ClientId))
        {
            // Копия, а не правка на месте: OAuth — тот же объект, что живёт в реестре
            // (Get не клонирует), а неудачная валидация ниже не должна портить его задним числом
            var existingOAuth = auth.OAuth;
            auth.OAuth = new McpOAuthConfig
            {
                AuthorizationServer = existingOAuth?.AuthorizationServer,
                TokenEndpoint = existingOAuth?.TokenEndpoint,
                ClientId = req.Auth.ClientId.Trim(),
                ClientSecretRef = existingOAuth?.ClientSecretRef,
                Scopes = existingOAuth?.Scopes,
                AccessTokenRef = existingOAuth?.AccessTokenRef,
                RefreshTokenRef = existingOAuth?.RefreshTokenRef,
                ExpiresAt = existingOAuth?.ExpiresAt,
                RedirectUri = existingOAuth?.RedirectUri,
            };
        }
        draft.Auth = auth;
        return null;
    }

    // Пары значений из запроса → словарь записи. Секрет с пустым значением наследует
    // плейсхолдер прежней записи; секрет без прежнего значения выбрасывается.
    private Dictionary<string, string>? MergeValues(
        List<McpValueInput>? input, Dictionary<string, string>? existing)
    {
        if (input is null) return existing;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in input)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            var name = item.Name.Trim();
            if (item.Secret)
            {
                if (!string.IsNullOrEmpty(item.Value)) result[name] = secrets.Set(UserId, item.Value);
                else if (existing is not null && existing.TryGetValue(name, out var old)
                         && McpSecretStore.TryParseRef(old, out _)) result[name] = old;
            }
            else result[name] = item.Value ?? "";
        }
        return result.Count > 0 ? result : null;
    }
}

/// <summary>Значение env/headers из формы. Secret + пустой Value = «оставить как было».</summary>
public record McpValueInput(string Name, string? Value, bool Secret = false);

public record McpAuthInput(string? Kind, string? HeaderName, string? Secret, string? ClientId);

public record McpServerUpsertRequest(
    string? Key, string? Label, string? Description, string? Transport,
    string? Command, List<string>? Args, List<McpValueInput>? Env,
    string? Url, List<McpValueInput>? Headers, McpAuthInput? Auth,
    bool? Enabled, bool? AlwaysLoad, bool? AllowReadOnlyPersonas, bool? AllowOutsideProjects);

public record McpEnableRequest(bool Enabled);
