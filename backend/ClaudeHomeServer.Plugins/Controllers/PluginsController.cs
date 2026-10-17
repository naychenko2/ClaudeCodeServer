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
    PluginStore? store,
    PluginInstaller? installer,
    PluginUninstall? uninstall,
    PluginSecretsStore? secrets) : ControllerBase
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

    // Id пакета в URL — один сегмент publisher~slug; декодируем в publisher/slug (M1).
    private static string DecodeId(string id) => id.Replace('~', '/');

    // --- GET /api/plugins — список установленных (DTO без значений секретов, C5) ---

    [HttpGet]
    public IActionResult List()
    {
        var gate = Gate(); if (gate is not null) return gate;
        return Ok(store!.LoadInstalled(UserId).Select(PluginResponse.From));
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
            InstallStatus.Ok => Ok(PluginResponse.From(result.Record!)),
            InstallStatus.NoOp => Ok(PluginResponse.From(result.Record!)),
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

        var pluginId = DecodeId(id);
        var records = store!.LoadInstalled(UserId).ToList();
        var record = records.FirstOrDefault(r => r.Id == pluginId);
        if (record is null) return NotFound();

        // permissions ⊆ requested: выдать право, которого нет в requested, — 400.
        var notInRequested = req.Permissions.Where(p => !record.Requested.Contains(p, StringComparer.Ordinal)).ToList();
        if (notInRequested.Count > 0)
            return BadRequest(new { error = "Право не входит в запрошенные (requested)", permissions = notInRequested });

        // grant секретов сверяет имена с requires.secrets (minor): незнакомый → 400.
        var (manifest, _) = PluginInstaller.ReadManifest(store.PackageDir(UserId, pluginId, record.Version));
        var declaredSecrets = manifest?.Contributes?.Requires?.Secrets?.Select(s => s.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var unknownSecrets = req.Secrets.Select(s => s.Name)
            .Where(n => !declaredSecrets.Contains(n))
            .ToList();
        if (unknownSecrets.Count > 0)
            return BadRequest(new { error = "Секрет не заявлен в манифесте (requires.secrets)", secrets = unknownSecrets });

        // Обязательные секреты манифеста (requires.secrets[].required=true) обязаны быть заданы.
        var requiredSecrets = manifest?.Validate().RequiredSecrets ?? [];
        var provided = req.Secrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var missingRequired = requiredSecrets.Where(n => !provided.Contains(n)).ToList();
        if (missingRequired.Count > 0)
            return BadRequest(new { error = "Не заданы обязательные секреты", secrets = missingRequired });

        // Выдать: granted = одобренные права, секреты — в запись. grant НЕ включает
        // выключенный плагин (minor): Enabled не трогаем.
        record.Granted = req.Permissions;
        // M7: значения секретов — в per-owner архиве plugin-secrets.json, в installed.json
        // остаётся только имя + признак «заполнен» (Set), без значения.
        record.Secrets = req.Secrets
            .Select(s => new PluginInstallSecret { Name = s.Name, Set = true })
            .ToList();
        foreach (var s in req.Secrets)
            secrets!.Set(UserId, pluginId, s.Name, s.Value);
        store.SaveInstalled(UserId, records);

        return Ok(new { requested = record.Requested, granted = record.Granted });
    }

    // --- POST /api/plugins/{id}/uninstall — удалить пакет и запись (каскад) ---

    [HttpPost("{id}/uninstall")]
    public IActionResult Uninstall(string id)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: удаление выдаёт только пользовательский токен" });

        var pluginId = DecodeId(id);
        if (!uninstall!.Uninstall(UserId, pluginId))
            return NotFound();
        return Ok(new { id = pluginId, removed = true });
    }

    // --- GET /api/plugins/{id}/permissions — requested + granted для диалога согласия ---

    [HttpGet("{id}/permissions")]
    public IActionResult Permissions(string id)
    {
        var gate = Gate(); if (gate is not null) return gate;
        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == DecodeId(id));
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
        var pluginId = DecodeId(id);
        var records = store!.LoadInstalled(UserId).ToList();
        var record = records.FirstOrDefault(r => r.Id == pluginId);
        if (record is null) return NotFound();
        record.Enabled = enabled;
        store.SaveInstalled(UserId, records);
        return Ok(new { id = pluginId, enabled });
    }
}
