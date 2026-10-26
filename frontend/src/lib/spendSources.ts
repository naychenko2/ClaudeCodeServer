// Признак «бесплатный» источник расхода по ключу CLI-провайдера.
// Единая точка правды на фронте — раньше та же эвристика дублировалась локально в
// QuotasTab. На бэке правило живёт в SpendSources.IsFree; серверный признак в ответе
// баланса/каталоге пока не отдаётся, поэтому правило держим здесь и поддерживаем
// идентичным серверному.
//
// ВНИМАНИЕ: это правило — ТОЛЬКО для бейджа в полосе «Все провайдеры». На карточке
// провайдера бесплатность определяется классом с бэка (costTier, см. lib/costTiers):
// локальное правило знает freellm*, сервер — нет, и карточка называла цену дважды.

import { isLocalEngineKey } from './localEngine';

// Подпись бейджа для полосы «Все провайдеры»: null — бейджа нет.
// локальный движок (ollama, llama-server) → «локально»,
// FreeLLM/прямой адаптер (endsWith('-direct'), startsWith('freellm')) → «бесплатный».
export function freeSourceLabel(key: string): string | null {
  if (isLocalEngineKey(key)) return 'локально';
  if (key.startsWith('freellm') || key.endsWith('-direct')) return 'бесплатный';
  return null;
}
