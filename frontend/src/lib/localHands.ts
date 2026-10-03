// Руки локального проекта в веб-морде (ADR-016 §7): тексты секции проекта и строк ленты.
// Чистые функции — компоненты только рисуют то, что вернулось отсюда.
//
// Решения владельца 2026-09-27: сеанса «на N минут» нет, руки включает тумблер проекта;
// белого списка программ нет; терминал в чате с руками разрешён. Поэтому ни сроков, ни
// «разрешите в трее», ни «добавьте программу» здесь нет и появляться не должно.

// Состояния события hands_status — зеркало HandsChatStates (HandsProtocol.cs)
export const HandsChatState = {
  Active: 'active',
  Allowed: 'allowed',
  Unavailable: 'unavailable',
  Stopped: 'stopped',
} as const;

// Причины остановки — зеркало HandsEndReason (HandsProtocol.cs)
export const HandsEndReason = {
  StoppedFromTray: 'tray-stop',
  HandsDisabled: 'disabled',
  AgentStopping: 'agent-stopping',
  // У unavailable: руки держит другой ход на той же машине
  Busy: 'busy',
} as const;

// ---------- секция проекта ----------

export interface HandsSectionInput {
  handsEnabled: boolean;
  // handsRefusal проекта: почему тумблер включить нельзя; null — можно
  refusal: string | null;
  // Устройство проекта не в сети; null — неизвестно (серверный проект, устройство отозвано)
  deviceOffline: boolean;
}

export type HandsSectionTone = 'neutral' | 'ok' | 'warning';

export interface HandsSectionView {
  summary: string;
  tone: HandsSectionTone;
  // Тумблер доступен: включить можно только без отказа, выключить — всегда
  canToggle: boolean;
  // Отказ матрицы — показываем текст сервера как есть
  refusal: string | null;
  offlineNote: boolean;
}

export function handsSectionView(i: HandsSectionInput): HandsSectionView {
  if (i.refusal !== null) {
    return {
      // Руки остались включены, а матрица их уже не пускает (руки сняли с устройства,
      // устройство отозвали): тумблер оставляем, чтобы их можно было выключить
      summary: i.handsEnabled ? 'Включены, но недоступны' : 'Недоступно',
      tone: i.handsEnabled ? 'warning' : 'neutral',
      canToggle: i.handsEnabled,
      refusal: i.refusal,
      offlineNote: false,
    };
  }
  return {
    summary: i.handsEnabled ? 'Включены' : 'Выключены',
    tone: i.handsEnabled ? 'ok' : 'neutral',
    canToggle: true,
    refusal: null,
    offlineNote: i.deviceOffline,
  };
}

// ---------- состояние рук чата ----------

export interface HandsStatusSnapshot {
  state: string;
  deviceName?: string | null;
  reason?: string | null;
}

function onDevice(name: string | null | undefined): string {
  return name ? ` «${name}»` : '';
}

// ---------- строки ленты ----------

// Строка ленты на событие hands_status; null — событие ленту не трогает (активность,
// провайдер — их показывает приложение агента устройства)
export function handsStatusFeedLine(status: HandsStatusSnapshot): string | null {
  if (status.state !== HandsChatState.Stopped) return null;
  const where = `на устройстве${onDevice(status.deviceName)}`;
  switch (status.reason) {
    case HandsEndReason.StoppedFromTray:
      return `Ход прерван ${where}: руки выключены кнопкой «Стоп». Окна, открытые ходом, закрыты.`;
    case HandsEndReason.HandsDisabled:
      return `Ход прерван ${where}: руки на нём выключены.`;
    case HandsEndReason.AgentStopping:
      return `Ход прерван ${where}: агент устройства остановился.`;
    default:
      return `Руки отключились от хода ${where}.`;
  }
}
