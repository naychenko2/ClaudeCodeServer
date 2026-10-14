namespace ClaudeHomeServer.Models;

// Потолки внутренних повторов CLI (429/529) — секция конфига LlmProviders:Retry.
//
// Зачем: с CLI 2.1.246 headless-сессии (-p) сами дотягивают ответ, оборванный ошибкой
// сервера/сети/зависанием, и могут переждать 529 МИНУТАМИ внутри процесса. Архитектурное
// решение (ADR-007 §4, разбор Александра 2026-10-15): владелец фолбэка — НАША цепочка, а CLI
// получает только КОРоткий внутренний backoff, чтобы отказ доходил до TurnErrorClassifier за
// десятки секунд, а не минуты. Значения — здесь, в конфиге, а не в коде.
//
// Все значения 0 = «в конфиге не задано» → реестр подставляет встроенный дефолт (см.
// LlmProviderRegistry.DefaultRetry*); 0 не означает «потолок выключен» — выключить потолок
// целиком можно только уходя от дефолта, и дефолт у нас всегда «короткий».
public class LlmRetryPolicy
{
    // Базовая задержка ПЕРВОГО повтора перегрузки (CLAUDE_CODE_OVERLOADED_RETRY_BASE_DELAY_MS)
    public int OverloadedRetryBaseDelayMs { get; set; }
    // Потолок задержки МЕЖДУ повторами перегрузки (CLAUDE_CODE_OVERLOADED_RETRY_MAX_DELAY_MS)
    public int OverloadedRetryMaxDelayMs { get; set; }
    // Максимальное время ожидания retry-watchdog (CLAUDE_CODE_RETRY_WATCHDOG_MAX_WAIT_MS)
    public int RetryWatchdogMaxWaitMs { get; set; }
    // Потолок повторных отпражей non-streaming fallback, упавшего по таймауту
    // (CLAUDE_CODE_NONSTREAMING_TIMEOUT_RETRIES)
    public int NonStreamingTimeoutRetries { get; set; }
}
