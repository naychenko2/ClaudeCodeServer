using System.Security.Claims;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.VideoEditor.Controllers;

// Фильмы и сохранение сцены в личном чате вне проекта (ADR-022 §2): диска проекта нет, поэтому те же маршруты, что у
// проектных, отвечают personal_scope_no_films — после проверки флага и своего чата (иначе 404, как везде). Ничего
// не читают и не пишут: отказ ДО обращения к чему бы то ни было.
[ApiController]
[Authorize]
[Route(VideoEditorRoutes.PersonalBase)]
public class PersonalFilmController(VideoEditScopeGate gate) : ControllerBase
{
    private string UserId => User.FindFirstValue("sub")!;

    [HttpGet(VideoEditorRoutes.Films)]
    [HttpGet(VideoEditorRoutes.FilmState)]
    [HttpPatch(VideoEditorRoutes.FilmPatchRoute)]
    [HttpPost(VideoEditorRoutes.FilmBuild)]
    [HttpGet(VideoEditorRoutes.FilmBuild)]
    [HttpDelete(VideoEditorRoutes.FilmBuild)]
    [HttpPost(VideoEditorRoutes.FilmMusic)]
    [HttpPost(VideoEditorRoutes.SceneSave)]
    public IActionResult NoFilms(string sessionId) =>
        gate.TryPersonalChat(UserId, sessionId, out _, out var denied) ? FilmHttp.PersonalRefusal() : denied;
}
