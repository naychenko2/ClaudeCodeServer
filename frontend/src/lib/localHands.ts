// Руки локального проекта в веб-морде (ADR-016 §7): тексты секции проекта, бейджа чата и
// строк ленты. Чистые функции — компоненты только рисуют то, что вернулось отсюда.
//
// Решения владельца 2026-09-27: сеанса «на N минут» нет, руки включает тумблер проекта;
// белого списка программ нет; терминал в чате с руками разрешён. Поэтому ни сроков, ни
// «разрешите в трее», ни «добавьте программу» здесь нет и появляться не должно.

// Состояния события hands_status — зеркало HandsChatStates (HandsProtocol.cs)
export const HandsChatState = {
  Active: 'active',
  Allowed: 'allowed',
  NoSession: 'no-session',
  Stopped: 'stopped',
  ProviderNotAllowed: 'provider-not-allowed',
} as const;

// Причины остановки — зеркало HandsEndReason (HandsProtocol.cs)
export const HandsEndReason = {
  Expired: 'expired',
  Idle: 'idle',
  StoppedFromTray: 'tray-stop',
  StoppedFromCli: 'cli-stop',
  HandsDisabled: 'disabled',
  AgentStopping: 'agent-stopping',
} as const;

export const HANDS_OWN_WINDOWS_TEXT = 'ИИ видит и трогает только окна, которые открыл сам.';
export const HANDS_SETTINGS_HINT = 'Меню профиля → «Руки на устройствах».';

// ---------- секция проекта ----------

export interface HandsSectionInput {
  handsEnabled: boolean;
  // handsRefusal проекта: почему тумблер включить нельзя; null — можно
  refusal: string | null;
  // Устройство проекта не в сети; null — неизвестно (серверный проект, устройство отозвано)
  deviceOffline: boolean;
  // Список доверенных провайдеров пуст; null — ещё не загружен
  noProviders: boolean | null;
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
  noProvidersWarning: boolean;
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
      noProvidersWarning: false,
    };
  }
  const noProviders = i.handsEnabled && i.noProviders === true;
  return {
    summary: !i.handsEnabled ? 'Выключены' : noProviders ? 'Нет провайдеров' : 'Включены',
    tone: !i.handsEnabled ? 'neutral' : noProviders ? 'warning' : 'ok',
    canToggle: true,
    refusal: null,
    offlineNote: i.deviceOffline,
    noProvidersWarning: noProviders,
  };
}

// ---------- бейдж чата ----------

export type HandsBadgeTone = 'neutral' | 'success' | 'warning';

export interface HandsBadgeView {
  tone: HandsBadgeTone;
  text: string;
  short: string;
  title: string;
  // Кнопка «Стоп» — прервать ход тем же механизмом, что кнопка остановки в композере
  canStop: boolean;
}

export interface HandsStatusSnapshot {
  state: string;
  deviceName?: string | null;
  reason?: string | null;
}

function onDevice(name: string | null | undefined): string {
  return name ? ` «${name}»` : '';
}

// status=null — событий ещё не было (ручки начального состояния пока нет): нейтральный
// бейдж «руки включены в проекте», а не «активен» — активность знает только событие
export function handsBadgeView(status: HandsStatusSnapshot | null, projectDeviceName: string | null): HandsBadgeView {
  const device = status?.deviceName || projectDeviceName;
  switch (status?.state) {
    case HandsChatState.Active:
      return {
        tone: 'success',
        text: device ? `ИИ за компьютером ${device}` : 'ИИ за компьютером',
        short: 'Руки',
        title: `ИИ управляет окнами на устройстве${onDevice(device)}. ${HANDS_OWN_WINDOWS_TEXT} «Стоп» прервёт ход.`,
        canStop: true,
      };
    case HandsChatState.Allowed:
      return {
        tone: 'neutral',
        text: 'Руки готовы',
        short: 'Руки',
        title: `Руки на устройстве${onDevice(device)} готовы: ИИ возьмёт их, когда понадобится. ${HANDS_OWN_WINDOWS_TEXT}`,
        canStop: false,
      };
    case HandsChatState.NoSession:
      return {
        tone: 'warning',
        text: 'Руки недоступны на устройстве',
        short: 'Нет рук',
        title: `Руки на устройстве${onDevice(device)} сейчас недоступны — например, заняты другим ходом на этой машине. Ход идёт без рук.`,
        canStop: false,
      };
    case HandsChatState.Stopped:
      return {
        tone: 'warning',
        text: 'Руки остановлены на устройстве',
        short: 'Стоп',
        title: stoppedReasonText(status.reason),
        canStop: false,
      };
    case HandsChatState.ProviderNotAllowed:
      return {
        tone: 'neutral',
        text: 'Руки недоступны: провайдер',
        short: 'Нет рук',
        title: `Провайдеру этого чата руки не доверены — ход идёт без рук. ${HANDS_SETTINGS_HINT}`,
        canStop: false,
      };
    default:
      return {
        tone: 'neutral',
        text: 'Руки включены',
        short: 'Руки',
        title: `Руки включены в проекте: ИИ сможет открывать программы на устройстве${onDevice(device)}. ${HANDS_OWN_WINDOWS_TEXT}`,
        canStop: false,
      };
  }
}

function stoppedReasonText(reason: string | null | undefined): string {
  switch (reason) {
    case HandsEndReason.StoppedFromTray:
    case HandsEndReason.StoppedFromCli:
      return 'Человек у компьютера нажал «Стоп» — ход прерван, окна, открытые ходом, закрыты.';
    case HandsEndReason.HandsDisabled:
      return 'Руки на устройстве выключены — ход прерван.';
    case HandsEndReason.AgentStopping:
      return 'Агент устройства остановился — ход прерван.';
    default:
      return 'Руки отключились от хода на устройстве.';
  }
}

// ---------- строки ленты ----------

// Строка ленты на событие hands_status; null — событие ленту не трогает (активность,
// провайдер — они живут в бейдже и повторялись бы каждым ходом)
export function handsStatusFeedLine(status: HandsStatusSnapshot): string | null {
  if (status.state !== HandsChatState.Stopped) return null;
  const where = `на устройстве${onDevice(status.deviceName)}`;
  switch (status.reason) {
    case HandsEndReason.StoppedFromTray:
    case HandsEndReason.StoppedFromCli:
      return `Ход прерван ${where}: руки выключены кнопкой «Стоп». Окна, открытые ходом, закрыты.`;
    case HandsEndReason.HandsDisabled:
      return `Ход прерван ${where}: руки на нём выключены.`;
    case HandsEndReason.AgentStopping:
      return `Ход прерван ${where}: агент устройства остановился.`;
    default:
      return `Руки отключились от хода ${where}.`;
  }
}
