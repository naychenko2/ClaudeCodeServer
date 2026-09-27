// Руки локального проекта в веб-морде (ADR-016 §7): тексты секции проекта, полосы «Руки» и
// строк ленты. Чистые функции — компоненты только рисуют то, что вернулось отсюда.
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

// Границы «только свои окна» нет (решение владельца 2026-09-27, ADR-016 §7)
export const HANDS_ANY_WINDOW_TEXT = 'ИИ может видеть и трогать любые окна на этом компьютере, в том числе снимать экран.';

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

// ---------- состояние рук чата (база полосы «Руки»; «бейдж» — историческое имя) ----------

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

// Состояние бейджа: первая отрисовка — GET /api/sessions/{id}/hands-status, дальше события
// hands_status. none — у чата рук нет, бейдж не рисуется; loading/failed — нейтральный вид
export type HandsBadgeState =
  | { kind: 'loading' }
  | { kind: 'failed' }
  | { kind: 'none' }
  | { kind: 'status'; status: HandsStatusSnapshot };

export const HANDS_BADGE_LOADING: HandsBadgeState = { kind: 'loading' };

// Ответ GET. Событие, пришедшее раньше ответа, свежее его — ответ тогда не применяем
export function handsInitialLoaded(
  prev: HandsBadgeState,
  view: { state: string | null; reason?: string | null; deviceName?: string | null },
): HandsBadgeState {
  if (prev.kind === 'status') return prev;
  if (view.state === null) return { kind: 'none' };
  return { kind: 'status', status: { state: view.state, reason: view.reason, deviceName: view.deviceName } };
}

export function handsInitialFailed(prev: HandsBadgeState): HandsBadgeState {
  return prev.kind === 'loading' ? { kind: 'failed' } : prev;
}

export function handsEventReceived(status: HandsStatusSnapshot): HandsBadgeState {
  return { kind: 'status', status };
}

export function handsBadgeStatus(state: HandsBadgeState): HandsStatusSnapshot | null {
  return state.kind === 'status' ? state.status : null;
}

// status=null — состояние ещё грузится или запрос упал: нейтральный бейдж «руки включены
// в проекте», а не «активен» — активность знает только сервер
export function handsBadgeView(status: HandsStatusSnapshot | null, projectDeviceName: string | null): HandsBadgeView {
  const device = status?.deviceName || projectDeviceName;
  switch (status?.state) {
    case HandsChatState.Active:
      return {
        tone: 'success',
        text: device ? `ИИ за компьютером ${device}` : 'ИИ за компьютером',
        short: 'Руки',
        title: `ИИ управляет окнами на устройстве${onDevice(device)}. ${HANDS_ANY_WINDOW_TEXT} «Стоп» прервёт ход.`,
        canStop: true,
      };
    case HandsChatState.Allowed:
      return {
        tone: 'neutral',
        text: 'Руки готовы',
        short: 'Руки',
        title: `Руки на устройстве${onDevice(device)} готовы: ИИ возьмёт их, когда понадобится. ${HANDS_ANY_WINDOW_TEXT}`,
        canStop: false,
      };
    case HandsChatState.Unavailable:
      if (status.reason === HandsEndReason.Busy) {
        return {
          tone: 'warning',
          text: 'Руки заняты другим ходом на этом устройстве',
          short: 'Заняты',
          title: `Руки на устройстве${onDevice(device)} держит другой ход — дождись его конца или останови его из трея. Этот ход не запущен.`,
          canStop: false,
        };
      }
      return {
        tone: 'warning',
        text: 'Руки недоступны на устройстве',
        short: 'Нет рук',
        title: `Руки включены, но устройство${onDevice(device)} их сейчас не даст: оно не на связи или руки на нём не установлены. Ход идёт без рук.`,
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
    default:
      return {
        tone: 'neutral',
        text: 'Руки включены',
        short: 'Руки',
        title: `Руки включены в проекте: ИИ сможет открывать программы на устройстве${onDevice(device)}. ${HANDS_ANY_WINDOW_TEXT}`,
        canStop: false,
      };
  }
}

// ---------- полоса «Руки» над композером ----------

// Полоса — одна строка по геометрии git-полосы (прототип полос, вариант C): статус-чип не
// сжимается никогда, у него есть короткая форма для узкой полосы; сжимается только сводка
export interface HandsStripView {
  tone: HandsBadgeTone;
  // Полный статус — строка меню переключателя полос и заголовок карточки подробностей
  text: string;
  // Чип статуса в полосе: после заголовка «Руки ▾» подлежащее не повторяем
  chip: string;
  // Чип в полосе уже порога и в свёрнутой строке
  short: string;
  // Причина остановки: коротко — в сводку, полностью — в карточку; null — причины нет
  reason: string | null;
  detail: string | null;
  canStop: boolean;
}

// Тон и «Стоп» — те же, что у бейджа
export function handsStripView(status: HandsStatusSnapshot | null, projectDeviceName: string | null): HandsStripView {
  const b = handsBadgeView(status, projectDeviceName);
  const v = (text: string, chip: string, short: string, reason: string | null = null, detail: string | null = null): HandsStripView =>
    ({ tone: b.tone, text, chip, short, reason, detail, canStop: b.canStop });
  switch (status?.state) {
    case HandsChatState.Active: return v('ИИ управляет компьютером', 'ИИ управляет компьютером', 'ИИ управляет');
    case HandsChatState.Allowed: return v('Руки готовы', 'Руки готовы', 'Готовы');
    case HandsChatState.Unavailable:
      return status.reason === HandsEndReason.Busy
        ? v('Руки заняты другим ходом на этом устройстве', 'Заняты другим ходом', 'Заняты')
        : v('Руки недоступны на устройстве', 'Недоступны на устройстве', 'Недоступны');
    case HandsChatState.Stopped:
      return v('Остановлено', 'Остановлено', 'Остановлено', stoppedReasonShort(status.reason), stoppedReasonText(status.reason));
    default: return v('Руки включены', 'Руки включены', 'Включены');
  }
}

// Сводка рядом с чипом: полная — кнопка карточки подробностей, mini — хвост свёрнутой
// строки. Устройство идёт раньше пометки о зрении: не хватит места — многоточие съест зрение
export function handsStripSummary(
  status: HandsStatusSnapshot | null,
  device: string | null,
  provider: HandsProviderVision | null,
  mini = false,
): string {
  const view = handsStripView(status, device);
  if (view.reason) return [view.reason, device].filter(Boolean).join(' · ');
  if (mini) return device ?? '';
  const vision = provider ? `${provider.name} · ${provider.vision ? 'видит снимки окон' : 'только текст окон'}` : null;
  switch (status?.state) {
    case HandsChatState.Unavailable:
      return [device, status.reason === HandsEndReason.Busy ? 'идёт другой ход' : null].filter(Boolean).join(' · ');
    case HandsChatState.Active:
    case HandsChatState.Allowed:
      return [device, vision].filter(Boolean).join(' · ');
    default: return device ?? '';
  }
}

export interface HandsProviderNote { key: string; caps: { displayName: string; supportsImages: boolean } }
export interface HandsProviderVision { name: string; vision: boolean }

// Провайдер чата и что он видит. Руки есть у любого провайдера (решение владельца
// 2026-09-27), единственное различие — зрение: без него мост идёт без снимков окон.
// Ключ чата не нашёлся в каталоге провайдеров — это подписка пула Claude (у пулов свои ключи)
export function handsProviderVision(
  sessionProvider: string | null | undefined,
  providers: readonly HandsProviderNote[],
): HandsProviderVision | null {
  const key = (sessionProvider || 'claude').toLowerCase();
  const o = providers.find(x => x.key.toLowerCase() === key) ?? providers.find(x => x.key.toLowerCase() === 'claude');
  return o ? { name: o.caps.displayName, vision: o.caps.supportsImages } : null;
}

export function handsProviderLabel(
  sessionProvider: string | null | undefined,
  providers: readonly HandsProviderNote[],
): string | null {
  const o = handsProviderVision(sessionProvider, providers);
  return o && `${o.name} · ${o.vision ? 'видит снимки окон' : 'видит только текст окон'}`;
}

function stoppedReasonShort(reason: string | null | undefined): string {
  switch (reason) {
    case HandsEndReason.StoppedFromTray: return 'Нажат «Стоп» у компьютера';
    case HandsEndReason.HandsDisabled: return 'Руки выключены на устройстве';
    case HandsEndReason.AgentStopping: return 'Агент устройства остановился';
    default: return 'Руки отключились от хода';
  }
}

function stoppedReasonText(reason: string | null | undefined): string {
  switch (reason) {
    case HandsEndReason.StoppedFromTray:
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
// провайдер — они живут в полосе «Руки» и повторялись бы каждым ходом)
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
