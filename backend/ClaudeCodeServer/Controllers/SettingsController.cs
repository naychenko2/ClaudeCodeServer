using ClaudeCodeServer.Models;
using ClaudeCodeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCodeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public class SettingsController(AppSettingsService appSettings) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(appSettings.Get());

    [HttpPut]
    public IActionResult Update([FromBody] AppSettings settings) => Ok(appSettings.Save(settings));
}
