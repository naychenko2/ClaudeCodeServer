namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>Живой маршрут предмета для перезакрепления его чатов: репетитор, поставщик (ключ записи пула или провайдера) и модель.</summary>
public sealed record ChildRepinTarget(string PersonaId, string ProviderKey, string Model);
