using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.Plugins.Controllers;

// REST установки и согласия человека (ADR-031 §2.3, §4, шаг 1.4). Всё под [Authorize];
// владелец — claim sub. Два гейта, оба → 403 с читаемой причиной:
//   1) подсистема выключена (Subsystems:plugins:Enabled=false) — SubsystemGate;
//   2) флаг plugins-mvp выключен для владельца — IFeatureFlagGate.
//
// Чужой пакет (нет записи с таким id у владельца) → 404, не 403: не подтверждаем
// существование (per-owner изоляция, инвариант 1). Сервисный JWT (токен MCP, typ=svc)
// на grant/uninstall → 403: права и удаление выдаёт только пользовательский токен.
//
// Вертикаль — статическая (ApplicationPart добавляет MSBuild), поэтому при выключенной
// подсистеме контроллер MVC находит, но его зависимости (PluginStore/PluginPackageValidator/
// PluginUninstall) в контейнер не попали — все три опциональные (nullable), гейт срабатывает
// раньше, чем они нужны (иначе 500 вместо 403, см. грабли Program.cs по Notes/Spend).
[ApiController]
[Authorize]
[Route("api/plugins")]
public class PluginsController(
    IConfiguration config,
    IFeatureFlagGate flags,
    PluginStore? store,
    PluginPackageValidator? validator,
    PluginUninstall? uninstall) : ControllerBase
{
    // Claim'ы — литералы: вертикаль не тянет JwtBearer-пакет, а MapInboundClaims=false
    // (Program.cs) оставляет raw-имена. Смысл 1:1 с JwtService (sub = id владельца,
    // typ=svc — сервисный токен MCP).
    private const string SubClaim = "sub";
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";

    private string UserId => User.FindFirstValue(SubClaim)!;
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

    // Гейт: null — прошёл; иначе 403 с читаемой причиной.
    private IActionResult? Gate()
    {
        if (!SubsystemGate.IsEnabled(config, "plugins"))
            return StatusCode(403, new { error = "Подсистема плагинов выключена (Subsystems:plugins:Enabled=false)" });
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.PluginsMvp))
            return StatusCode(403, new { error = "Флаг plugins-mvp выключен" });
        return null;
    }

    // --- GET /api/plugins — список установленных плагинов владельца ---

    [HttpGet]
    public IActionResult List()
    {
        var gate = Gate(); if (gate is not null) return gate;
        return Ok(store!.LoadInstalled(UserId));
    }

    // --- POST /api/plugins/install — принять пакет из папки ---

    [HttpPost("install")]
    public IActionResult Install([FromBody] PluginInstallRequest req)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (string.IsNullOrWhiteSpace(req.Source))
            return BadRequest(new { error = "Не задан source (путь к папке пакета)" });
        if (!Directory.Exists(req.Source))
            return BadRequest(new { error = $"Папка пакета не найдена: {req.Source}" });

        // 1) Манифест из папки: id + version (источник правды о пакете).
        var manifest = ReadManifest(req.Source);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Version))
            return BadRequest(new { error = "Манифест пакета не найден или без id/version" });

        // 2) Валидация файлов пакета (allow-list + sha256).
        var validation = validator!.Validate(req.Source);
        if (!validation.IsValid)
            return BadRequest(new { error = "Пакет не прошёл валидацию", problems = validation.Problems });

        var id = manifest.Id;
        var version = manifest.Version;
        var sha256 = validation.Sha256!;

        // 3) Запрошенные права (потолок): из тела; fallback на манифест.
        var requested = req.RequestedPermissions.Count > 0
            ? req.RequestedPermissions
            : manifest.Permissions?.Requested ?? [];

        // 4) 409: запись с тем же id, но sha256 пакета изменился → «обновить».
        var records = store!.LoadInstalled(UserId).ToList();
        var existing = records.FirstOrDefault(r => r.Id == id);
        if (existing is not null && existing.Sha256 != sha256)
            return Conflict(new { error = "Пакет изменился (sha256 не совпадает с записью) — обновите", id, version });

        // 5) Копирование в packages/{id}/{version}/ (идемпотентно: есть — не трогаем).
        var targetDir = store.PackageDir(UserId, id, version);
        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            CopyDirectory(req.Source, targetDir);
        }

        // 6) Запись PluginInstall: requested + granted (MVP: requested минус net:* —
        //    net:* выдаёт человек явно через grant; остальные отмечены автоматически).
        var record = new PluginInstall
        {
            Id = id,
            Version = version,
            Sha256 = sha256,
            Requested = requested,
            Granted = AutoGrant(requested),
            Enabled = true,
            Source = req.Source,
        };
        records.RemoveAll(r => r.Id == id);
        records.Add(record);
        store.SaveInstalled(UserId, records);

        return Ok(record);
    }

    // --- POST /api/plugins/{id}/grant — выдать права/секреты ---

    [HttpPost("{id}/grant")]
    public IActionResult Grant(string id, [FromBody] PluginGrantRequest req)
    {
        var gate = Gate(); if (gate is not null) return gate;
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: права выдаёт только пользовательский токен" });

        var records = store!.LoadInstalled(UserId).ToList();
        var record = records.FirstOrDefault(r => r.Id == id);
        if (record is null) return NotFound();

        // permissions ⊆ requested: выдать право, которого нет в requested, — 400.
        var notInRequested = req.Permissions.Where(p => !record.Requested.Contains(p, StringComparer.Ordinal)).ToList();
        if (notInRequested.Count > 0)
            return BadRequest(new { error = "Право не входит в запрошенные (requested)", permissions = notInRequested });

        // Обязательные секреты манифеста (requires.secrets[].required=true) обязаны быть заданы.
        var manifest = ReadManifest(store.PackageDir(UserId, id, record.Version));
        var requiredSecrets = manifest?.Validate().RequiredSecrets ?? [];
        var provided = req.Secrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var missingRequired = requiredSecrets.Where(n => !provided.Contains(n)).ToList();
        if (missingRequired.Count > 0)
            return BadRequest(new { error = "Не заданы обязательные секреты", secrets = missingRequired });

        // Выдать: granted = одобренные права, секреты — в запись, плагин включается.
        record.Granted = req.Permissions;
        record.Secrets = req.Secrets
            .Select(s => new PluginInstallSecret { Name = s.Name, Value = s.Value })
            .ToList();
        record.Enabled = true;
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

        if (!uninstall!.Uninstall(UserId, id))
            return NotFound();
        return Ok(new { id, removed = true });
    }

    // --- GET /api/plugins/{id}/permissions — requested + granted для диалога согласия ---

    [HttpGet("{id}/permissions")]
    public IActionResult Permissions(string id)
    {
        var gate = Gate(); if (gate is not null) return gate;
        var record = store!.LoadInstalled(UserId).FirstOrDefault(r => r.Id == id);
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
        var records = store!.LoadInstalled(UserId).ToList();
        var record = records.FirstOrDefault(r => r.Id == id);
        if (record is null) return NotFound();
        record.Enabled = enabled;
        store.SaveInstalled(UserId, records);
        return Ok(new { id, enabled });
    }

    // --- Вспомогательное ---

    // MVP: granted = requested минус net:* (net:* — серверный прокси, выдаёт человек явно).
    private static List<string> AutoGrant(List<string> requested) =>
        requested.Where(p => !p.StartsWith("net:", StringComparison.Ordinal)).ToList();

    // Манифест пакета: ccs-plugin.json → plugin.json → .claude-plugin/plugin.json (порядок
    // allow-list валидатора). null — манифеста нет.
    private static PluginManifest? ReadManifest(string packageRoot)
    {
        foreach (var name in new[] { "ccs-plugin.json", "plugin.json", ".claude-plugin/plugin.json" })
        {
            var path = Path.Combine(packageRoot, name);
            if (!System.IO.File.Exists(path)) continue;
            try
            {
                return JsonSerializer.Deserialize<PluginManifest>(
                    System.IO.File.ReadAllText(path),
                    new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    // Рекурсивное копирование файлов пакета (source → target). Каталоги создаёт по мере
    // необходимости; существующие файлы перезаписывает (пакет неизменяем после записи).
    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            System.IO.File.Copy(file, dest, overwrite: true);
        }
    }
}
