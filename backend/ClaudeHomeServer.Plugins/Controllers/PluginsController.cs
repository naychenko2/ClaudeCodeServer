using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.Plugins.Controllers;

// REST установки и согласия человека (ADR-031 §2.3, §4, шаг 1.4). Всё под [Authorize];
// владелец — claim sub. Гейты:
//   1) подсистема выключена (Subsystems:plugins:Enabled=false) → 503 (M2: сервис не активен);
//   2) флаг plugins-mvp выключен для владельца → 403 (пользовательский гейт).
//
// Id пакета {publisher}/{slug} в URL — один сегмент publisher~slug (M1): {id} не
// разбивается по '/', в экшене декодируем '~' → '/'.
//
// Сервисный JWT (typ=svc) на install/grant/uninstall → 403: установка, права и удаление
// выдаёт только пользовательский токен (C2).
//
// Контроллер тонкий: гейты + маппинг на HTTP. Ядро установки (C1/C2/M5/M6) — в
// PluginInstaller; чтение/выдача прав и каскад удаления — в PluginUninstall.
//
// Вертикаль статическая (ApplicationPart добавляет MSBuild): при выключенной подсистеме
// контроллер MVC находит, но зависимости (PluginStore/PluginInstaller/PluginUninstall)
// в контейнер не попали — все опциональные (nullable), гейт 503 срабатывает раньше (M2).
[ApiController]
[Authorize]
[Route("api/plugins")]
public class PluginsController(
    IConfiguration config,
    IFeatureFlagGate flags,
    // `= null` — обязательность для DI: подсистема выключена → PluginStore/… не в контейнере,
    // и без явного значения по умолчанию активация контроллера падала (500, а не честный 503 гейта).
    PluginStore? store = null,
    PluginInstaller? installer = null,
    PluginUninstall? uninstall = null,
    PluginSecretsStore? secrets = null) : ControllerBase
{
    // Claim'ы — литералы: вертикаль не тянет JwtBearer-пакет, а MapInboundClaims=false
    // (Program.cs) оставляет raw-имена. Смысл 1:1 с JwtService (sub = id владельца,
    // typ=svc — сервисный токен MCP).
    private const string SubClaim = "sub";
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";

    private string UserId => User.FindFirstValue(SubClaim)!;
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

    // Гейт: null — прошёл; иначе отказ. Подсистема выключена → 503 (M2), флаг off → 403.
    private IActionResult? Gate()
    {
        if (!SubsystemGate.IsEnabled(config, "plugins"))
            return StatusCode(503, new { error = "Подсистема плагинов выключена (Subsystems:plugins:Enabled=false)" });
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.PluginsMvp))
            return StatusCode(403, new { error = "Флаг plugins-mvp выключен" });
        return null;
    }

    // Id пакета в URL — один сегмент publisher~slug. Нормализуем ЕДИНЫМ методом PluginId
    // (M3, minor 3: одна точка, не DecodeId + TryNormalize): ~ → / + проверка формата.
    // Не по формату — возвращаем как есть (поиск записи не найдёт → 404).
    private static string NormalizeId(string id) =>
        PluginId.TryNormalize(id, out var norm) ? norm.Value : id;

    // Манифест установленной версии пакета (ccs-plugin.json → .claude-plugin/plugin.json) —
    // источник contributes (panels) для DTO. Пакет неизменяем, id/version валидны (M3) —
    // PackageDir безопасен. Манифест исчез после установки → (null, null): панели деградируют
    // в пустой список (фронт покажет «панелей нет»), не 500.
    // Кэш (rework 12, minor 4): N+1 чтений ccs-plugin.json на каждый GET /api/plugins убран —
    // манифест неизменяем по (id, version, sha), кэш в PluginManifestCache, инвалидация при
    // установке/обновлении/удалении (в PluginInstaller/PluginUninstall).
    private PluginManifest? ManifestFor(PluginInstall record) =>
        PluginManifestCache.GetOrLoad(record.Id, record.Version, record.Sha256,
            () => PluginInstaller.ReadManifest(store!.PackageDir(UserId, record.Id, record.Version)).Manifest);

    // --- GET /api/plugins — список установленных (DTO без значений секретов, C5) ---

    [HttpGet]
    public IActionResult List()
    {
        var gate = Gate(); if (gate is not null) return gate;
        return Ok(store!.LoadInstalled(UserId).Select(r => PluginResponse.From(r, ManifestFor(r))));
    }

    // --- POST /api/plugins/install — принять пакет по метке источника (C1/C2) ---

    [HttpPost("install")]
    public IActionResult Install([FromBody] PluginInstallRequest req)
    {
        var gate = Gate(); if (gate is not null) return gate;

        // C2: установка — только пользовательский токен (сервисный → 403).
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: установку делает только пользовательский токен" });

        if (string.IsNullOrWhiteSpace(req.Source))
            return BadRequest(new { error = "Не задан source (метка источника: draft:/upload:/catalog:)" });

        var result = installer!.Install(UserId, req.Source);
        return result.Status switch
        {
            InstallStatus.Ok => Ok(PluginResponse.From(result.Record!, ManifestFor(result.Record!))),
            InstallStatus.NoOp => Ok(PluginResponse.From(result.Record!, ManifestFor(result.Record!))),
            InstallStatus.Conflict => Conflict(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error, problems = result.Problems }),
        };
    }

    // --- POST /api/plugins/{id}/grant — выдать права/секреты (id = publisher~slug, M1) ---

    [HttpPost("{id}/grant")]
    public IActionResult Grant(string id, [FromBody] PluginGrantRequest req)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: права выдаёт только пользовательский токен" });

        var pluginId = NormalizeId(id);

        // minor 1: load → check → save под одной блокировкой (PluginStore.Update<T>), не два
        // отдельных вызова (гонка: между LoadInstalled и SaveInstalled запись могли поменять).
        var outcome = store!.Update<GrantOutcome>(UserId, records =>
        {
            var record = records.FirstOrDefault(r => r.Id == pluginId);
            if (record is null) return GrantOutcome.NotFound;

            // permissions ⊆ requested: выдать право, которого нет в requested, — 400.
            var notInRequested = req.Permissions
                .Where(p => !record.Requested.Contains(p, StringComparer.Ordinal)).ToList();
            if (notInRequested.Count > 0)
                return GrantOutcome.BadRequest("Право не входит в запрошенные (requested)", notInRequested);

            // grant секретов сверяет имена с requires.secrets (minor): незнакомый → 400.
            var (manifest, _) = PluginInstaller.ReadManifest(store.PackageDir(UserId, pluginId, record.Version));
            var declaredSecrets = manifest?.Contributes?.Requires?.Secrets
                ?.Select(s => s.Name).ToHashSet(StringComparer.Ordinal) ?? [];
            var unknownSecrets = req.Secrets.Select(s => s.Name)
                .Where(n => !declaredSecrets.Contains(n)).ToList();
            if (unknownSecrets.Count > 0)
                return GrantOutcome.BadRequest("Секрет не заявлен в манифесте (requires.secrets)", unknownSecrets);

            // minor 4: выданный секрет сверяется с правом secret:{name} из requested:
            // выдать значение секрета, на который нет запрошенного права, — 400.
            var secretNotRequested = req.Secrets.Select(s => s.Name)
                .Where(n => !record.Requested.Contains($"secret:{n}", StringComparer.Ordinal)).ToList();
            if (secretNotRequested.Count > 0)
                return GrantOutcome.BadRequest("Секрет не покрыт запрошенным правом secret:{name}", secretNotRequested);

            // Обязательные секреты манифеста (requires.secrets[].required=true) обязаны быть заданы.
            var requiredSecrets = manifest?.Validate().RequiredSecrets ?? [];
            var provided = req.Secrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var missingRequired = requiredSecrets.Where(n => !provided.Contains(n)).ToList();
            if (missingRequired.Count > 0)
                return GrantOutcome.BadRequest("Не заданы обязательные секреты", missingRequired);

            // Выдать: granted = одобренные права, секреты — в запись. grant НЕ включает
            // выключенный плагин (minor): Enabled не трогаем.
            var oldSecretNames = record.Secrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            record.Granted = req.Permissions;
            // M7: значения секретов — в per-owner архиве plugin-secrets.json, в installed.json
            // остаётся только имя + признак «заполнен» (Set) + время изменения (UpdatedAt, шаг 3.3),
            // без значения.
            var now = DateTime.UtcNow;
            record.Secrets = req.Secrets
                .Select(s => new PluginInstallSecret { Name = s.Name, Set = true, UpdatedAt = now })
                .ToList();

            // minor 5: секреты, выданные ранее, но НЕ в этом grant — убрать их значения из
            // plugin-secrets.json (иначе они «выживают» после перезаписи record.Secrets).
            var newSecretNames = record.Secrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var oldName in oldSecretNames.Where(n => !newSecretNames.Contains(n)))
                secrets!.RemoveSecret(UserId, pluginId, oldName);

            foreach (var s in req.Secrets)
                secrets!.Set(UserId, pluginId, s.Name, s.Value);

            return GrantOutcome.Ok(record);
        });

        return outcome switch
        {
            GrantOutcome o when o.Kind == GrantKind.NotFound => NotFound(),
            GrantOutcome o when o.Kind == GrantKind.BadRequest =>
                BadRequest(new { error = o.Error, details = o.Details }),
            _ => Ok(new { requested = outcome!.Record!.Requested, granted = outcome.Record.Granted }),
        };
    }

    // --- POST /api/plugins/{id}/uninstall — удалить пакет и запись (каскад) ---

    [HttpPost("{id}/uninstall")]
    public IActionResult Uninstall(string id)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: удаление выдаёт только пользовательский токен" });

        var pluginId = NormalizeId(id);
        if (!uninstall!.Uninstall(UserId, pluginId))
            return NotFound();
        return Ok(new { id = pluginId, removed = true });
    }

    // --- GET /api/plugins/{id}/permissions — requested + granted для диалога согласия ---

    [HttpGet("{id}/permissions")]
    public IActionResult Permissions(string id)
    {
        var gate = Gate(); if (gate is not null) return gate;
        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == NormalizeId(id));
        if (record is null) return NotFound();
        return Ok(new { requested = record.Requested, granted = record.Granted });
    }

    // --- POST /api/plugins/{id}/disable · enable — тумблер включения ---

    [HttpPost("{id}/disable")]
    public IActionResult Disable(string id) => SetEnabled(id, enabled: false);

    [HttpPost("{id}/enable")]
    public IActionResult Enable(string id) => SetEnabled(id, enabled: true);

    private IActionResult SetEnabled(string id, bool enabled)
    {
        var gate = Gate(); if (gate is not null) return gate;
        // minor 6: тумблер — только пользовательский токен: сервисный JWT (агент) не должен
        // включать обратно плагин, выключенный человеком.
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: тумблер включения — только пользовательский токен" });

        var pluginId = NormalizeId(id);
        // minor 1: load → mutate → save под одной блокировкой (PluginStore.Update).
        var found = false;
        store!.Update(UserId, records =>
        {
            var rec = records.FirstOrDefault(r => r.Id == pluginId);
            if (rec is not null)
            {
                rec.Enabled = enabled;
                found = true;
            }
            return records;
        });
        if (!found) return NotFound();
        return Ok(new { id = pluginId, enabled });
    }

    // --- PUT /api/plugins/{id}/secrets/{name} — значение секрета (шаг 3.3, для UI 3.4) ---
    //
    // Значение в теле; user JWT (сервисный → 403). Значение — в per-owner архиве
    // plugin-secrets.json (PluginSecretsStore), в installed.json — имя + set + updatedAt.
    // Имя обязано быть заявлено в манифесте (requires.secrets) и покрыто правом secret:{name}.

    [HttpPut("{id}/secrets/{name}")]
    public IActionResult PutSecret(string id, string name, [FromBody] PluginSecretValueRequest req)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: секрет вводит только пользовательский токен" });

        var pluginId = NormalizeId(id);
        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == pluginId);
        if (record is null) return NotFound();

        // Выключенный плагин → 409 (rework 12, major 3): как у остальных ручек (call/assets/panel).
        if (!record.Enabled)
            return StatusCode(409, new { error = "Плагин выключен" });

        // Пустое значение → 400 (rework 12, minor 5): пустой секрет неинформативен.
        if (string.IsNullOrWhiteSpace(req.Value))
            return BadRequest(new { error = "Значение секрета не задано" });

        // Имя секрета — из манифеста (requires.secrets) и покрыто ВЫДАННЫМ правом secret:{name}
        // (rework 12, critical 2: раньше сверяли Requested, а не Granted — значение сохранялось,
        // даже когда право не выдано; теперь 400 с текстом без значения).
        var (manifest, _) = PluginInstaller.ReadManifest(store.PackageDir(UserId, pluginId, record.Version));
        var declared = manifest?.Contributes?.Requires?.Secrets
            ?.Select(s => s.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        if (!declared.Contains(name, StringComparer.Ordinal))
            return BadRequest(new { error = $"Секрет не заявлен в манифесте (requires.secrets): {name}" });
        if (!record.Granted.Contains($"secret:{name}", StringComparer.Ordinal))
            return BadRequest(new { error = $"Секрет не покрыт выданным правом secret:{name}" });

        // Значение — в per-owner архиве; в запись установки — set + updatedAt (load→save под lock).
        secrets!.Set(UserId, pluginId, name, req.Value);
        var now = DateTime.UtcNow;
        store!.Update(UserId, records =>
        {
            var rec = records.FirstOrDefault(r => r.Id == pluginId);
            if (rec is not null)
            {
                var s = rec.Secrets.FirstOrDefault(x => x.Name == name);
                if (s is null)
                    rec.Secrets.Add(new PluginInstallSecret { Name = name, Set = true, UpdatedAt = now });
                else
                {
                    s.Set = true;
                    s.UpdatedAt = now;
                }
            }
            return records;
        });
        return Ok(new { name, set = true, updatedAt = now });
    }

    // --- DELETE /api/plugins/{id}/secrets/{name} — убрать значение секрета (шаг 3.3) ---

    [HttpDelete("{id}/secrets/{name}")]
    public IActionResult DeleteSecret(string id, string name)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: секрет вводит только пользовательский токен" });

        var pluginId = NormalizeId(id);
        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == pluginId);
        if (record is null) return NotFound();

        // Выключенный плагин → 409 (rework 12, major 3): как у остальных ручек.
        if (!record.Enabled)
            return StatusCode(409, new { error = "Плагин выключен" });

        secrets!.RemoveSecret(UserId, pluginId, name);
        store!.Update(UserId, records =>
        {
            var rec = records.FirstOrDefault(r => r.Id == pluginId);
            if (rec is not null)
            {
                var s = rec.Secrets.FirstOrDefault(x => x.Name == name);
                if (s is not null)
                {
                    s.Set = false;
                    s.UpdatedAt = DateTime.UtcNow;
                }
            }
            return records;
        });
        return Ok(new { name, set = false });
    }
}

/// <summary>Тело <c>PUT /api/plugins/{id}/secrets/{name}</c> (шаг 3.3): значение секрета.</summary>
public class PluginSecretValueRequest
{
    public string Value { get; set; } = "";
}

// Исход grant-операции (minor 1: возвращается из PluginStore.Update<T>, а не список записей).
internal sealed class GrantOutcome
{
    public GrantKind Kind { get; }
    public string? Error { get; }
    public List<string>? Details { get; }
    public PluginInstall? Record { get; }

    private GrantOutcome(GrantKind kind, string? error, List<string>? details, PluginInstall? record)
    {
        Kind = kind;
        Error = error;
        Details = details;
        Record = record;
    }

    public static GrantOutcome NotFound => new(GrantKind.NotFound, null, null, null);
    public static GrantOutcome BadRequest(string error, List<string> details) =>
        new(GrantKind.BadRequest, error, details, null);
    public static GrantOutcome Ok(PluginInstall record) => new(GrantKind.Ok, null, null, record);
}

internal enum GrantKind
{
    Ok,
    NotFound,
    BadRequest,
}
