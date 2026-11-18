namespace ClaudeHomeServer.Services.Llm.Gateway;

// Шов для теста (фича «знания репетитора», КИ1a): стор материалов (КИ1b) тестируется
// с фейком OCR, без заглушки vLLM. Второй реализации OCR нет: LocalOcrService — единственная,
// и регистрация в LlmSubsystem указывает на ТОТ ЖЕ экземпляр (форвардер, не второй
// синглтон: семафор сервиса = один слот GPU на экземпляр).
public interface ILocalPageOcr
{
    Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct);
}
