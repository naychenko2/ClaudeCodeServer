using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
            remoteUrl = section[kvp.Key + ":Frontend:RemoteUrl"],
            exposedModule = section[kvp.Key + ":Frontend:ExposedModule"] ?? "./subsystem",
        }).Where(m => !string.IsNullOrEmpty(m.remoteUrl)).ToList();
        return Ok(new { items });
    }
}
