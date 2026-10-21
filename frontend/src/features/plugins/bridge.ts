// Мост панели плагина v1 (ADR-031 §3) — сторона ХОСТА. Чистые функции:
// валидация сообщений фрейма, сборка srcdoc, конструкторы сообщений хоста,
// кламп высоты и разбор отказов по праву. React и DOM сюда не лезут —
// жизненный цикл (таймер ready, postMessage, состояние) живёт в PluginFrame.
//
// Песочница: iframe sandbox="allow-scripts" БЕЗ allow-same-origin — origin фрейма
// непрозрачный ('null'), куки/JWT/хранилища хоста панель не видит. Навигация
// iframe не несёт Authorization, поэтому хост сам забирает panel/index.html по
// REST (user JWT) и монтирует через srcdoc: вставляет CSP, шим plugin-kit и
// текущую тему. JWT и стор хоста во фрейм не передаются (ADR-031, инвариант 1).

// Шим plugin-kit: мост + CSS-токены тем (ADR-031 §3.1). ?raw — содержимое строкой;
// файл одновременно доступен как статика (PWA precache), но в srcdoc нужен текстом.
// buildPanelSrcdoc (panelSrcdoc.ts) встраивает его сам; bridge.ts подставляет ?raw.
import shimJs from '../../../public/plugin-kit/plugin-kit.js?raw';
import shimCss from '../../../public/plugin-kit/theme.css?raw';
import { buildPanelSrcdoc as buildPanelSrcdocRaw, PANEL_CSP as _PANEL_CSP } from './panelSrcdoc';

// === Протокол ===

export const BRIDGE_VERSION = 1;
// Метка конверта: от фрейма 'ccs-plugin', от хоста 'ccs-host' (ADR-031 §3.3)
export const FRAME_SOURCE = 'ccs-plugin';
export const HOST_SOURCE = 'ccs-host';
// Origin sandbox-iframe без allow-same-origin — строка 'null' (не null)
export const OPAQUE_ORIGIN = 'null';

// CSP srcdoc (ADR-031 §3.1) — из panelSrcdoc.ts (единая константа: meta оболочки + meta панели).
export const PANEL_CSP = _PANEL_CSP;

// Таймаут ожидания ready (макет 3: «Скелет 3 с, затем — состояние «не отвечает»»)
export const READY_TIMEOUT_MS = 3000;

// Коды ошибок моста (макет 3, «ОШИБКА МОСТА»): причина в тексте, код — моно
export type BridgeErrorCode = 'bridge_timeout' | 'bridge_bad_message' | 'bridge_version';

const BRIDGE_ERROR_TEXT: Record<BridgeErrorCode, string> = {
  bridge_timeout: 'Мост не получил ответа за 3 с.',
  bridge_bad_message: 'Панель отправила неверное сообщение',
  bridge_version: 'Версия моста не поддерживается',
};

export function bridgeErrorText(code: BridgeErrorCode): string {
  return BRIDGE_ERROR_TEXT[code];
}

// Коды ошибок операций (ADR-031 §3.4): denied/not_found/invalid/unavailable/
// upstream/timeout/limit. Источник правды — сервер: панель, совравшая себе про
// права, получит denied.
export const CALL_ERROR_CODES = ['denied', 'not_found', 'invalid', 'unavailable', 'upstream', 'timeout', 'limit'] as const;
export type CallErrorCode = (typeof CALL_ERROR_CODES)[number];

// === Сообщения фрейма → хост ===

export type FrameMessage =
  | { source: 'ccs-plugin'; v: 1; type: 'ready' }
  | { source: 'ccs-plugin'; v: 1; type: 'call'; id: string; op: string; args: Record<string, unknown> }
  | { source: 'ccs-plugin'; v: 1; type: 'resize'; height: number };

// Сырое событие, как оно приходит на window: source/origin/data. Минимальная
// форма — чтобы валидацию можно было проверять без живого MessageEvent.
export interface RawMessageEvent {
  source: unknown;
  origin: string;
  data: unknown;
}

// Валидация сообщения фрейма (ADR-031 §3.2): принимается, только если выполнены
// ВСЕ условия — источник = окно нашего фрейма, origin = 'null', конверт
// source='ccs-plugin' + v=1, и поля конкретного type. Всё невалидное — null
// (тихий игнор: панель не обязана угадывать, что хост уже не слушает).
//
// frameWindow — contentWindow нашего iframe; null/undefined (iframe ещё не
// смонтирован) — сообщение не от нас.
export function validateFrameMessage(e: RawMessageEvent, frameWindow: unknown): FrameMessage | null {
  if (e.source !== frameWindow || frameWindow === null || frameWindow === undefined) return null;
  if (e.origin !== OPAQUE_ORIGIN) return null;
  const d = e.data;
  if (d === null || typeof d !== 'object') return null;
  const m = d as Record<string, unknown>;
  if (m.source !== FRAME_SOURCE) return null;
  if (m.v !== BRIDGE_VERSION) return null;
  switch (m.type) {
    case 'ready':
      return { source: 'ccs-plugin', v: 1, type: 'ready' };
    case 'call': {
      if (typeof m.id !== 'string' || m.id === '') return null;
      if (typeof m.op !== 'string' || m.op === '') return null;
      if (m.args === null || typeof m.args !== 'object' || Array.isArray(m.args)) return null;
      return { source: 'ccs-plugin', v: 1, type: 'call', id: m.id, op: m.op, args: m.args as Record<string, unknown> };
    }
    case 'resize': {
      if (typeof m.height !== 'number' || !Number.isFinite(m.height) || m.height < 0) return null;
      return { source: 'ccs-plugin', v: 1, type: 'resize', height: m.height };
    }
    // Неизвестный type — аддитивное расширение протокола (ADR-031 §3.5):
    // обе стороны неизвестные игнорируют
    default:
      return null;
  }
}

// Окно панели (ADR-031 §3.6): панель — frames[0] оболочки (внешний iframe хоста).
// Индексный доступ к кросс-доменному WindowProxy разрешён. Приём и отправка сообщений
// моста идут именно в это окно (не в оболочку): e.source сверяется с ним, и
// postMessage(init/result/theme/context) уходит в него. null — iframe ещё не смонтирован.
export function panelWindowOf(outerIframe: HTMLIFrameElement | null | undefined): Window | null {
  return outerIframe?.contentWindow?.frames[0] ?? null;
}

// === Сообщения хоста → фрейм ===

export type BridgeTheme = 'light' | 'dark';

export interface PluginRef {
  id: string;
  version: string;
}

// Контекст слота: только то, что слот явно отдаёт (ADR-031 §3.3). Список чатов,
// стор, JWT и данные других владельцев во фрейм не попадают никогда.
export interface SlotContext {
  projectId: string | null;
  objectId?: string | null;
}

export type HostMessage =
  | {
      source: 'ccs-host'; v: 1; type: 'init';
      plugin: PluginRef; slot: string; context: SlotContext;
      theme: BridgeTheme; locale: string; granted: string[];
    }
  | { source: 'ccs-host'; v: 1; type: 'result'; id: string; ok: true; data: unknown }
  | { source: 'ccs-host'; v: 1; type: 'result'; id: string; ok: false; error: { code: string; message: string } }
  | { source: 'ccs-host'; v: 1; type: 'theme'; theme: BridgeTheme }
  | { source: 'ccs-host'; v: 1; type: 'context'; context: SlotContext };

// Ответ на ready: всё, что панель вправе знать (ADR-031 §3.3). granted —
// информационная копия выданных прав, чтобы панель прятала недоступное.
export function buildInitMessage(args: {
  plugin: PluginRef;
  slot: string;
  context: SlotContext;
  theme: BridgeTheme;
  locale: string;
  granted: string[];
}): HostMessage {
  return {
    source: 'ccs-host', v: 1, type: 'init',
    plugin: args.plugin,
    slot: args.slot,
    context: args.context,
    theme: args.theme,
    locale: args.locale,
    granted: args.granted,
  };
}

// Итог call: тот же id, что пришёл в вызове.
export function buildResultMessage(id: string, ok: true, data: unknown): HostMessage;
export function buildResultMessage(id: string, ok: false, error: { code: string; message: string }): HostMessage;
export function buildResultMessage(
  id: string,
  ok: boolean,
  dataOrError: unknown | { code: string; message: string },
): HostMessage {
  if (ok) return { source: 'ccs-host', v: 1, type: 'result', id, ok: true, data: dataOrError };
  return { source: 'ccs-host', v: 1, type: 'result', id, ok: false, error: dataOrError as { code: string; message: string } };
}

// Смена темы без перемонтирования (ADR-031 §3.3).
export function buildThemeMessage(theme: BridgeTheme): HostMessage {
  return { source: 'ccs-host', v: 1, type: 'theme', theme };
}

// Смена контекста слота (например, другой проект).
export function buildContextMessage(context: SlotContext): HostMessage {
  return { source: 'ccs-host', v: 1, type: 'context', context };
}

// === srcdoc (ADR-031 §3.6: сборка документа панели) ===

// Потолок размера файла панели (ADR-031 §3.6). Дублирует
// PluginPackageValidator.MaxPanelFileBytes (256 КиБ): панель больше — не монтируем,
// показываем «Панель слишком большая». Начали с малого намеренно: поднять можно,
// опустить — нет.
export const MAX_PANEL_FILE_BYTES = 256 * 1024;

// ЕДИНАЯ сборка документа панели: для установленной панели (PluginFrame) и для
// предпросмотра черновика (DraftCard) — одна функция, одна PANEL_CSP. Тело — в
// panelSrcdoc.ts (без ?raw); здесь — обёртка, подставляющая ?raw-импорты шима.
// theme — тема в первом кадре (ADR-031 §3.3): data-cc-theme на ВНУТРЕННЕМ <html>.
export function buildPanelSrcdoc(panelHtml: string, theme: BridgeTheme = 'light'): string {
  return buildPanelSrcdocRaw(panelHtml, theme, shimJs, shimCss);
}

// === Высота ===

// Потолок высоты острова (px): панель не может вырасти выше слота, снизу —
// скролл. Мобильный потолок ниже: панель на телефоне — на весь экран, но
// под шапку и композер.
export const PANEL_MAX_HEIGHT = 800;
export const PANEL_MAX_HEIGHT_MOBILE = 560;
export const PANEL_MIN_HEIGHT = 48;

// Кламп желаемой высоты (resize) в пределы слота.
export function clampPanelHeight(h: number, mobile = false): number {
  const max = mobile ? PANEL_MAX_HEIGHT_MOBILE : PANEL_MAX_HEIGHT;
  return Math.min(max, Math.max(PANEL_MIN_HEIGHT, Math.round(h)));
}

// Потолок высоты КОНТЕНТА (px): страховочный, не «размер слота». Установленная
// панель (PluginFrame) растёт под контент: resize = высота контента, хост ставит её
// фрейму (порог снизу PANEL_MIN_HEIGHT, этот потолок — от аномально длинных панелей).
// Карточка ленты (DraftCard) остаётся на PANEL_MAX_HEIGHT (800).
export const PANEL_CONTENT_MAX_HEIGHT = 20_000;

// Кламп высоты контента (resize) установленной панели: порог снизу, страховочный
// потолок. В отличие от clampPanelHeight (потолок слота 800/560) — без mobile-ветки:
// контент на мобиле тоже растёт, прокрутку держит обёртка карточки.
export function clampContentHeight(h: number): number {
  return Math.min(PANEL_CONTENT_MAX_HEIGHT, Math.max(PANEL_MIN_HEIGHT, Math.round(h)));
}

// === Отказ по праву ===

// Ключ права из ошибки `denied`: сервер шлёт «Нет права: {key}»
// (PluginBridge.DispatchAsync). Формат не совпал — null (хост покажет сырой
// текст, не додумывая ключ).
export function deniedKeyOf(message: string): string | null {
  const prefix = 'Нет права: ';
  const i = message.indexOf(prefix);
  if (i < 0) return null;
  const key = message.slice(i + prefix.length).trim();
  return key || null;
}
