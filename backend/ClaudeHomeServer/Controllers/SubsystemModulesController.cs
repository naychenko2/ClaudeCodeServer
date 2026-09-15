using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Пилот Module Federation: список subsystem remotes, которые хост загружает
/// в рантайме (registerRemotes + loadRemote) и регистрирует в реестре слотов.
/// Источник — единая секция DynamicModules (манифест модуля): отдаём только
/// Frontend-часть каждого модуля ({ id, remoteUrl, exposedModule }). Модули
/// без Frontend (например, только Backend-сборка сценария Б) в ответ не попадают.
/// </summary>
[ApiController]
[Authorize]
[Route("api/subsystem-modules")]
public class SubsystemModulesController(IConfiguration config) : ControllerBase
{
    [HttpGet]
    public IActionResult List()
    {
        // DynamicModules — массив манифестов (DynamicModules:0:Key, ...:0:Frontend:RemoteUrl, ...).
        // Читаем строковым путём, а не биндом в C#-описатель: форма Frontend/Backend
        // под-объектов принадлежит бэк-задаче (ModuleDescriptor), контроллеру нужен
        // только RemoteUrl/ExposedModule, и неважно, есть ли у модуля Backend-сборка.
        var section = config.GetSection("DynamicModules");
        var items = section.GetChildren().Select(kvp => new
        {
            id = section[kvp.Key + ":Key"],
            enabled = section[kvp.Key + ":Enabled"] ?? "true",
            remoteUrl = section[kvp.Key + ":Frontend:RemoteUrl"],
            exposedModule = section[kvp.Key + ":Frontend:ExposedModule"] ?? "./subsystem",
        })
        // Условие включения — ОБА рубильника: `DynamicModules:N:Enabled` (DynamicModules.Enabled
        // фильтрует уже на уровне ModuleLoader — «загружать ли сборку вообще»),
        // и `Subsystems:{Key}:Enabled` через SubsystemGate.IsEnabled (CLAUDE.md «Отключаемость
        // подсистемы», единая точка истины: выключенная подсистема НЕ получает ни
        // Register, ни hosted-сервисов). Без второй проверки фронт продолжал бы грузить
        // remote выключенной по `Subsystems:{key}` подсистемы, и ModuleLoader хоста
        // оказывался в расхождении со снимком `/api/admin/subsystems` (active=False, а
        // фронт думает, что модуль жив).
        .Where(m => !string.IsNullOrEmpty(m.id)
            && !string.IsNullOrEmpty(m.remoteUrl)
            && bool.TryParse(m.enabled, out var on) && on
            && SubsystemGate.IsEnabled(config, m.id!))
        .Select(m => new { m.id, m.remoteUrl, m.exposedModule })
        .ToList();
        return Ok(new { items });
    }
}
