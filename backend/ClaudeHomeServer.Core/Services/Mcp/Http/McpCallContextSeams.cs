using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp.Http;

// Узкие швы контекста вызова MCP-over-HTTP-тулсетов (волна NotesToolset):
// параметризованные тулсеты (tasks/notes) на каждый вызов резолвят
// сессию-вызывателя, персону и её привязки по хвосту маршрута.
// Реализации — форвардеры в Main (PromptSeamAdapters.cs), DI — в Program.cs.
//
// Прецедент — IPersonaBindingsSource/IPersonaPromptAssembler (Turn) и
// PersonaResolverAdapter (PromptSeamAdapters): интерфейс в Core, адаптер 1:1 в Main.
//
// IMcpSessionAccessor НЕ схлопнут с ISessionDirectory: тот отдаёт GetById/GetAll
// БЕЗ проверки владельца (полный каталог для Spend/backfill), а MCP-тулсету
// достаточно ровно одной сессии-вызывателя «своего владельца». Выдать ему
// ISessionDirectory значило бы расширить права: GetAll — все сессии системы,
// GetById — чужая сессия без ownership-проверки.

// SessionManager.GetOwned: резолв сессии-вызывателя по id с проверкой владельца.
// null — нет такой сессии или она принадлежит другому владельцу.
public interface IMcpSessionAccessor
{
    Session? GetOwned(string sessionId, string ownerId);
}

// PersonaBindingsService: гейты Tool/Section-привязок персоны на MCP-вызов.
// Сигнатуры 1:1 с `PersonaBindingsService.EffectiveToolEnabled`/`SectionEnabled`.
// НЕ тот же шов, что IPersonaServerToolGate (Services/Composition): там
// ServerToolEnabled (deny-only, БЕЗ фолбэка на Persona.Tools), а здесь
// EffectiveToolEnabled (есть фолбэк на Persona.Tools). Не путать.
public interface IMcpPersonaBindings
{
    bool EffectiveToolEnabled(string? ownerId, Persona? persona, string key);
    bool SectionEnabled(string? ownerId, Persona? persona, string key);
}
