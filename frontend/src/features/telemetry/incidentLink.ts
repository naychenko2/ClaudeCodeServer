// Канал открытия карточки инцидента по диплинку из уведомления
// (#/telemetry/incident/{fingerprint}).
//
// Два конца, потому что случая тоже два: раздел ещё не смонтирован (App кладёт
// отпечаток в sessionStorage, страница забирает его при монтировании) и раздел УЖЕ
// открыт (switchHubTab его не перемонтирует — тогда App диспатчит событие). Без второго
// конца тап по уведомлению при открытом разделе не делал бы ничего, а инцидент всплыл
// бы при следующем заходе, когда он уже неактуален. Тот же приём, что у чатов и
// календаря.
const PENDING_KEY = 'cc_pending_incident';

export const INCIDENT_OPEN_EVENT = 'cc-open-incident';

/// Запомнить отпечаток до перехода в раздел
export function setPendingIncident(fingerprint: string) {
  sessionStorage.setItem(PENDING_KEY, fingerprint);
}

/// Забрать отпечаток ОДИН раз: повторный заход в раздел не должен снова открывать
/// карточку, которую человек закрыл
export function takePendingIncident(): string | null {
  const value = sessionStorage.getItem(PENDING_KEY);
  if (value) sessionStorage.removeItem(PENDING_KEY);
  return value;
}
