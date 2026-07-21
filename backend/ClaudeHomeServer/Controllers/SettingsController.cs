using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public class SettingsController(AppSettingsService appSettings) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(appSettings.Get());

    // Настройки серверные и общие для всех (ClaudeBilling влияет на учёт стоимости у каждого),
    // поэтому запись — только админам. Чтение оставляем всем: UI показывает режим биллинга
    // в чате, и без GET он бы не отрисовался.
    [HttpPut]
    [Authorize(Roles = "admin")]
    public IActionResult Update([FromBody] AppSettings settings) => Ok(appSettings.Save(settings));
}
