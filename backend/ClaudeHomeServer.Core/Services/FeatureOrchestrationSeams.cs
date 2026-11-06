using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Швы конвейера фич для вертикали Tasks (она не видит ни Llm, ни SessionManager): счётчик
// локальных исполнителей в хвосте хода оркестратора и серверный пинок при их простое.
// Реализации ставит Main (Program.cs); нет реализации — секция и пинок молчат.

// Локальный исполнитель: персона-исполнитель, чья модель для её тира разворачивается в
// локального провайдера (UserModelTierResolver.ModelFor + IModelResolver.IsLocal).
public sealed record LocalExecutor(string PersonaId, string Name);

// Локальный ход задачи: её эффективная модель (тир задачи → модель/тир персоны → слот
// владельца) разворачивается в локального провайдера. Одно физическое место на движке.
// PersonaName — имя персоны задачи (null — у задачи нет персоны).
public sealed record LocalTaskTurn(string? PersonaName);

public interface ILocalExecutorRoster
{
    // Локальные персоны-исполнители владельца, доступные в проекте. Пусто — их нет или
    // локальный провайдер выключен.
    IReadOnlyList<LocalExecutor> For(string ownerId, string? projectId);

    // Ход задачи на локальном движке: эффективная модель задачи (тот же порядок, что при
    // запуске хода, — ModelAssignmentResolver.ExecutorModel) разворачивается в локального
    // провайдера. Считаются НЕ только исполнители: ревью, доработка и хвост фичи с
    // переопределённым тиром занимают тот же слот движка. null — ход не локальный.
    LocalTaskTurn? LocalTurnOf(TaskItem task, string ownerId);
}

// Карточка в ключевой чат фичи — та же доставка, что у карточек снятия паузы
// (TaskExecutionService.DeliverKeyChatCardAsync). false — доставить не удалось.
public interface IKeyChatCardSender
{
    Task<bool> TrySendAsync(string sessionId, string text);
}
