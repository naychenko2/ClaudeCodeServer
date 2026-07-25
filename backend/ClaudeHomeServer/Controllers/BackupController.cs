using ClaudeHomeServer.Services.Backup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Состояние бэкапов и ручной снапшот. Только для админов: бэкап инстансный —
// архивы и расписание общие для всех пользователей.
//
// Настройки отсюда НЕ правятся: они живут в секции «Backup» файла конфигурации
// (appsettings.Local.json) и задаются руками. Причина не в удобстве, а в том, что
// восстановление уносит каталог data целиком — настройки, лежащие внутри него,
// откатились бы вместе с данными, включая путь к папке собственных архивов.
//
// Восстановления тут тоже нет: оно требует остановленного сервера (иначе тот продолжит
// писать в подменяемый каталог) и живёт в CLI-режиме exe --restore и в меню трея —
// они работают и тогда, когда сервер не поднимается, то есть ровно в том случае,
// ради которого бэкап и делается.
[ApiController]
[Route("api/admin/backup")]
[Authorize(Roles = "admin")]
public class BackupController(BackupService backups) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var options = backups.Options;
        var ctx = backups.BuildContext();
        var state = backups.LoadState();

        return Ok(new
        {
            enabled = options.Enabled,
            // Куда архивы ложатся фактически (при пустом Backup:Path — папка по умолчанию)
            effectivePath = ctx.BackupDir,
            secretsPath = ctx.SecretsDir,
            intervalHours = options.IntervalHours,
            lastSuccessAt = state.LastSuccessAt,
            lastError = state.LastError,
            lastAttemptAt = state.LastAttemptAt,
            recent = state.Recent,
        });
    }

    // Снапшот идёт синхронно: на реальных объёмах он занимает секунды, а ответ сразу
    // несёт результат — иначе UI пришлось бы опрашивать состояние.
    [HttpPost("run")]
    public IActionResult Run()
    {
        var result = backups.RunSnapshot();
        if (!result.Ok) return StatusCode(500, new { error = result.Error });

        return Ok(new
        {
            file = System.IO.Path.GetFileName(result.ArchivePath),
            createdAt = result.Manifest?.CreatedAt,
            summary = result.Manifest?.Summary,
        });
    }
}
