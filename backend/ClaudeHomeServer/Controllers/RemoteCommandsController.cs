using ClaudeHomeServer.Services.RemoteCommands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Пульт удалённых команд: запуск и остановка заранее объявленных действий на машине, где
// крутится продукт (туннель VS Code, Dify, службы Windows).
//
// Главный замок — не роль, а конфигурация: исполняется только то, что хозяин машины руками
// записал в appsettings.Local.json. Из веб-морды команды не заводятся и не редактируются
// никогда — морда торчит наружу, и поле произвольной команды было бы прямым RCE. По HTTP
// ездит один лишь ключ действия, тексты команд наружу не отдаются даже админу.
//
// Семантика выключенного рубильника — как у питания: GET отвечает 200 всегда (его зовёт
// шапка при монтировании, и 404 шумел бы ошибкой в консоли), мутирующие — 404 («здесь
// ничего нет» честнее, чем «вам нельзя», когда дело не в правах).
[ApiController]
[Route("api/admin/remote-commands")]
[Authorize(Roles = "admin")]
public class RemoteCommandsController(RemoteCommandsService pult) : ControllerBase
{
    /// <summary>Список действий с кэшированным состоянием. Не исполняет ни одной команды.</summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetStatus() => Ok(new
    {
        enabled = pult.Enabled,
        actions = pult.List(),
    });

    /// <summary>
    /// Запускает действие. Токен запроса сюда НЕ передаётся сознательно: это
    /// <c>HttpContext.RequestAborted</c>, а обрыв соединения (прокси с 60-секундным лимитом,
    /// закрытая вкладка) не должен убивать дерево уже идущей команды посреди работы. Единственный
    /// рубильник прерывания — объявленный в конфиге <c>TimeoutSeconds</c> (§3, §10.4).
    /// </summary>
    [HttpPost("{key}/start")]
    public async Task<IActionResult> Start(string key)
    {
        if (!pult.Enabled) return NotFound();
        return Answer(await pult.StartAsync(key, UserName));
    }

    /// <summary>Останавливает действие. Токен запроса не передаётся — причина та же, что у Start.</summary>
    [HttpPost("{key}/stop")]
    public async Task<IActionResult> Stop(string key)
    {
        if (!pult.Enabled) return NotFound();
        return Answer(await pult.StopAsync(key, UserName));
    }

    /// <summary>
    /// Свежая проверка состояния. 409 не отвечает НИКОГДА: при идущей операции возвращает
    /// кэш с <c>busy: true</c> — второе окно во время долгого старта должно видеть живое
    /// «занято», а не конфликт.
    /// </summary>
    [HttpPost("{key}/refresh")]
    public async Task<IActionResult> Refresh(string key, CancellationToken ct)
    {
        if (!pult.Enabled) return NotFound();
        return Answer(await pult.RefreshAsync(key, ct));
    }

    /// <summary>Накопленный вывод действия — только админу и только из памяти.</summary>
    [HttpGet("{key}/output")]
    public IActionResult Output(string key)
    {
        if (!pult.Enabled) return NotFound();
        var text = pult.Output(key);
        return text is null ? NotFound() : Ok(new { text });
    }

    private IActionResult Answer(RemoteCommandOperation? operation)
    {
        if (operation is null) return NotFound(); // неизвестный ключ
        if (operation.Status == RemoteCommandOperationStatus.Busy)
            return Conflict(new { error = operation.Detail, state = operation.State, busy = true });

        return Ok(new
        {
            ok = operation.Status == RemoteCommandOperationStatus.Ok,
            state = operation.State,
            busy = operation.Busy,
            detail = operation.Detail,
            // Время проверки и код возврата едут тем же ответом: карточка рисует их рядом с
            // бейджем, и без них после успешного refresh под свежим состоянием висело бы
            // «Ещё не проверялось».
            checkedAt = operation.CheckedAt,
            lastExitCode = operation.LastExitCode,
        });
    }

    private string UserName => User.Identity?.Name ?? "неизвестный";
}
