namespace ClaudeHomeServer.Services.Media;

// Метка происхождения прогона ComfyUI: кто запустил (OwnerId), из какой сессии (SessionId,
// null — не из чата), вид (Kind: image | video | audio | train | other) и операция (Op),
// тяжёлый ли прогон (Heavy). Внутренний тип — в DTO наружу не включается; OwnerId
// обезличивает Main, REST его не отдаёт.
public sealed record LocalJobOrigin(string OwnerId, string? SessionId, string Kind, string Op, bool Heavy);
