using System.IdentityModel.Tokens.Jwt;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Единая точка детской границы «свой проект-предмета» для взрослых page-level ручек:
/// список чатов (Sessions), список сервисов (Preview), чтение «Знаний» (Knowledge), ручки задач
/// (Tasks) и чтение чата (Chats/ChatContext). До Х-W4-47 (хвост ревью e748c039) эти же хелперы
/// были скопированы в каждый из контроллеров; после пятой и шестой копий хвост сказал вынести
/// их в одно место. Смысл каждого метода — ровно тот, что был в контроллерах; решения —
/// ADR-041 (в частности решение 81).
/// </summary>
public static class ChildSubjectAccess
{
    // Код отказа чужого проекта ребёнку — единый для всех границ этой фичи («Знания», «Задачи»,
    // «Чаты», «Сервисы»): граница одна, поэтому и код один.
    public const string NotSubjectCode = "not_child_subject";

    // Ребёнок ли текущий владелец — та же точка правды, что у гейта И1: IChildModePolicy по стору,
    // а не по claim (образец ChatsController, хвост ревью R-KM-B6f). Резолвим из RequestServices
    // через GetRequiredService: отсутствие политики в скопе запроса — исключение, а не взрослая
    // ветка (в проде политику регистрирует AddSessionCore, молчаливый фолбэк на взрослом прятал
    // бы обрыв связи). Без DI-скопа (ручные сборки без RequestServices) — не ребёнок: детские
    // запросы к не-размеченным ручкам сюда всё равно не доходят — их отсекает сам гейт И1.
    public static bool IsChild(HttpContext http, string userId) =>
        http.RequestServices?.GetRequiredService<IChildModePolicy>()?.For(userId) is not null;

    // «Проект-предмет этого ребёнка» (решение 81): маркер из единой точки его чтения
    // (ProjectCapabilities.IsChildSubject) + владение: проект-предмета принадлежит самому
    // ребёнку (ChildSubjectProjectProvisioner). Предмет, удалённый из профиля, не помеха:
    // по ADR-041 проект живёт, и его данные ребёнку остаются видны.
    public static bool IsOwnSubjectProject(Project? p, string childId) =>
        ProjectCapabilities.IsChildSubject(p) && p!.OwnerId == childId;

    // Отказ чужого проекта ребёнку — та же форма, что у детских отказов, до всякой работы
    // с проектом. `what` — название области в человеческом сообщении («Чаты», «Сервисы»):
    // текст отказа отличается по ручке, код — один.
    public static JsonResult NotOwnSubject(string what) =>
        new(new { code = NotSubjectCode, message = $"{what} доступны только в твоём предмете." })
        { StatusCode = StatusCodes.Status403Forbidden };
}
