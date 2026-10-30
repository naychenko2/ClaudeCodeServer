using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Отказ «настройки чата — не тебе» (S2a-10b): режим прав, цикл, штаб, смена модели и провайдера
/// принадлежат владельцу проекта (арендатору чата). Тот же гейт права (Configure) на все настройки:
/// отдельного гейта «на модель» нет — есть этот. Участнику, которому чат виден, отвечаем 403 с этим
/// кодом (фронт по нему держит выключенные строки меню модели: «выбирает владелец»); постороннему
/// остаётся 404 — это решает вызывающий (TurnRightExtensions.Deny).
/// </summary>
public static class OwnerSettingsGate
{
    public const string ErrorCode = "owner_settings";

    public static ObjectResult Denied() =>
        new(new { error = "Настройки чата выбирает владелец", code = ErrorCode }) { StatusCode = StatusCodes.Status403Forbidden };
}
