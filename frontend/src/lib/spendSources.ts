// Признак «бесплатный» источник расхода по ключу CLI-провайдера.
// Единая точка правды на фронте — раньше та же эвристика дублировалась локально в
// QuotasTab. На бэке правило живёт в SpendSources.IsFree; серверный признак в ответе
// баланса/каталоге пока не отдаётся, поэтому правило держим здесь и поддерживаем
// идентичным серверному. Сможет сервер отдавать isFree в ProviderBalanceInfo —
// переключим потребителя на серверное поле здесь же, в одном месте.

// Подпись бейджа для полосы «Все провайдеры» и карточки: null — бейджа нет.
// ollama → «локально», FreeLLM/прямой адаптер (endsWith('-direct'), startsWith('freellm')) → «бесплатный».
export function freeSourceLabel(key: string): string | null {
  if (key === 'ollama') return 'локально';
  if (key.startsWith('freellm') || key.endsWith('-direct')) return 'бесплатный';
  return null;
}

// Булев признак для карточки провайдера (isFree). false, если бесплатных условий нет.
export function isFreeSource(key: string): boolean {
  return freeSourceLabel(key) != null;
}
