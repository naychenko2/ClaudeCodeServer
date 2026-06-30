using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/usage")]
public class UsageController(UsageService usage) : ControllerBase
{
    // История снимков использования лимитов подписки (для экрана usage + тренда)
    [HttpGet]
    public IActionResult Get() => Ok(usage.GetAll());
}
