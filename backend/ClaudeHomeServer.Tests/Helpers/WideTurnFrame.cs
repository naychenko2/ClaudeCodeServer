using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Тестовый шов вердикта рамки хода (ITurnFrameSource) с широкой рамкой. Нужен тестовым
/// фабрикам тулсетов, где шов стал обязательным параметром (fail-closed, R-S2a-3b):
/// широкий сохранят прежнее поведение таких тестов (рамка ничего не резала при nullable-шовом).
/// </summary>
public sealed class WideTurnFrame : ITurnFrameSource
{
    public TurnFrameKind Frame(string? sessionId, string ownerId) => TurnFrameKind.Wide;
}
