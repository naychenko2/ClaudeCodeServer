import { useSyncExternalStore } from 'react';
import type { VideoChannel } from '../types';

// Где сейчас смотрят видео, кроме самой панели, и занят ли центр каталогом.
//
// Три режима на выбор, и у каждого своя работа:
//   panel  — кадр живёт в панели рельсы (стор пуст);
//   center — кадр занимает ЦЕНТРАЛЬНУЮ область страницы — своим островом рядом
//            с чатом, ровно как открытый файл;
//   float  — плавающее окно поверх интерфейса, его двигают и тянут за угол.
//
// Каталог каналов живёт ТАМ ЖЕ, в центральном острове: выбирают канал глазами,
// по обложкам, а в узкой панели рельсы их не разглядеть. Отсюда инвариант ниже:
// центр занимает РОВНО ОДИН обитатель — либо кадр, либо каталог.
//
// Стор общий, а не состояние страницы: переключает режим панель (она в рельсе), а рисуют
// центр (ChatsPage / DesktopWorkspace) и плавающее окно (App, над всеми страницами).
//
// ВАЖНО: живой кадр в продукте ровно ОДИН. Панель снимает свой плеер, когда канал ушёл
// в центр или в окно, — два живых iframe одного эфира дают два звука внахлёст.

export type VideoStageMode = 'center' | 'float';

/** Панель «Видео» должна показаться: канал выбрали снаружи, а панель могла быть закрыта. */
export const VIDEO_PANEL_EVENT = 'cc-video-reveal-panel';

/** Что показывает центральный остров: кадр или каталог. */
export type VideoCenterView = 'player' | 'picker';

/** Геометрия плавающего окна в пикселях вьюпорта. */
export interface FloatRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface VideoStageState {
  channel: VideoChannel;
  mode: VideoStageMode;
}

const RECT_KEY = 'cc_video_float_rect';

// Шапка окна (за неё таскают) не входит в кадр: высота окна = кадр 16:9 + шапка
export const FLOAT_HEADER_H = 32;

// Размер по умолчанию: ширина, на которой ещё читается бегущая строка новостей
const DEFAULT_W = 420;
const DEFAULT_H = Math.round((DEFAULT_W * 9) / 16) + FLOAT_HEADER_H;

export const FLOAT_MIN_W = 240;
export const FLOAT_MAX_W = 1280;

let state: VideoStageState | null = null;
// Каталог в центре. Отдельный флаг, а не режим VideoStageState: каталог живёт
// БЕЗ канала (его как раз ещё выбирают) и переживает уход кадра в окно — тогда
// в центре листают ленту, а эфир идёт рядом в плавающем окне.
let picker = false;
// Канал БОКОВОЙ ПАНЕЛИ. Живёт в сторе, а не внутри самой панели, потому что выбирают
// его в каталоге — тот стоит в центре и до состояния панели дотянуться иначе не может.
// Панель тоже пишет сюда, когда канал переключают её собственной полосой.
let panelChannel: VideoChannel | null = null;
// Центральный остров занят ЧУЖИМ режимом (файл, задача, доска, граф…). Флаг
// публикует страница, а знать его обязан стор: иначе кнопка «развернуть в центре»
// уводила бы кадр в занятое место — панель снимала свой плеер, центр рисовал файл,
// и эфир пропадал целиком, без единого сообщения о том, куда он делся.
let blocked = false;
let rect: FloatRect = loadRect();
const listeners = new Set<() => void>();

function emit() { for (const cb of listeners) cb(); }

function loadRect(): FloatRect {
  // Правый нижний угол — место, где окно меньше всего мешает: там нет ни рельсы, ни композера
  const fallback: FloatRect = {
    x: Math.max(16, (typeof window === 'undefined' ? 1280 : window.innerWidth) - DEFAULT_W - 24),
    y: Math.max(16, (typeof window === 'undefined' ? 800 : window.innerHeight) - DEFAULT_H - 24),
    w: DEFAULT_W,
    h: DEFAULT_H,
  };
  try {
    const raw = localStorage.getItem(RECT_KEY);
    if (!raw) return fallback;
    const parsed = JSON.parse(raw) as Partial<FloatRect>;
    if (typeof parsed.x !== 'number' || typeof parsed.y !== 'number'
      || typeof parsed.w !== 'number' || typeof parsed.h !== 'number') return fallback;
    return clampRect(parsed as FloatRect);
  } catch {
    return fallback;
  }
}

/**
 * Держим окно в пределах экрана. Окно пережило смену разрешения или переезд на другой
 * монитор — без этого оно оказалось бы за краем и стало недоступно совсем.
 */
export function clampRect(r: FloatRect): FloatRect {
  const vw = typeof window === 'undefined' ? 1280 : window.innerWidth;
  const vh = typeof window === 'undefined' ? 800 : window.innerHeight;

  const w = Math.min(Math.max(r.w, FLOAT_MIN_W), Math.min(FLOAT_MAX_W, vw));
  const h = Math.round((w * 9) / 16) + FLOAT_HEADER_H;
  // Кромку в 24 пикселя оставляем всегда: за неё окно можно поймать мышью
  const x = Math.min(Math.max(r.x, -w + 24), vw - 24);
  const y = Math.min(Math.max(r.y, 0), vh - 24);
  return { x, y, w, h };
}

/**
 * ГЕОМЕТРИЯ МЕСТА ПОД КАДР («слот»).
 *
 * Живой кадр рисует не панель и не остров, а один оверлей в App — иначе эфир
 * обрывался бы на каждом переходе между проектами: страница перемонтируется, а
 * вместе с ней умирает iframe. Панель и центр вместо кадра держат ПУСТОЕ место и
 * сообщают сюда его прямоугольник, оверлей же кладётся поверх.
 *
 * frame — куда встаёт кадр, clip — чем его обрезать: тело панели ниже кадра быть
 * не обязано, узкая панель режет его своим краем, и fixed-оверлей без клипа вылез
 * бы поверх соседей.
 */
export type VideoSlotKind = 'panel' | 'center';

export interface SlotBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface VideoSlot {
  frame: SlotBox;
  clip: SlotBox;
}

let slots: Record<VideoSlotKind, VideoSlot | null> = { panel: null, center: null };

function sameBox(a: SlotBox, b: SlotBox): boolean {
  return a.x === b.x && a.y === b.y && a.w === b.w && a.h === b.h;
}

function sameSlot(a: VideoSlot | null, b: VideoSlot | null): boolean {
  if (a === b) return true;
  if (!a || !b) return false;
  return sameBox(a.frame, b.frame) && sameBox(a.clip, b.clip);
}

/**
 * Сообщить (или снять) прямоугольник места. Зовётся из петли измерения на каждом
 * кадре, поэтому равные значения не публикуются: иначе оверлей перерисовывался бы
 * шестьдесят раз в секунду впустую.
 */
export function setVideoSlot(kind: VideoSlotKind, slot: VideoSlot | null): void {
  if (sameSlot(slots[kind], slot)) return;
  slots = { ...slots, [kind]: slot };
  emit();
}

export function getVideoSlots(): Record<VideoSlotKind, VideoSlot | null> {
  return slots;
}

/**
 * Где сейчас место живого кадра: панель, центральный остров — или нигде.
 *
 * null означает две разные вещи, и для оверлея они одинаковы: канала нет вовсе либо
 * кадр показывают в плавающем окне (у того свой кадр — он и так переживает переходы).
 * Оверлей в этом случае снимается НЕМЕДЛЕННО, без отсрочки: два живых iframe одного
 * эфира дают два звука внахлёст.
 */
export function videoFramePlace(
  stage: VideoStageState | null,
  panel: VideoChannel | null,
): VideoSlotKind | null {
  if (stage) return stage.mode === 'center' ? 'center' : null;
  return panel ? 'panel' : null;
}

export function getVideoStage(): VideoStageState | null {
  return state;
}

export function getFloatRect(): FloatRect {
  return rect;
}

/** Показать канал в выбранном режиме; null — вернуть кадр в панель. */
export function setVideoStage(channel: VideoChannel | null, mode: VideoStageMode = 'center'): void {
  // В занятый центр не пускаем: кнопки на это время выключены, но защита нужна и
  // здесь — путей в центр несколько (панель, каталог, плавающее окно)
  if (channel && mode === 'center' && blocked) return;
  // Кадр разворачивают в центре — каталог уступает: остров там один
  if (channel && mode === 'center' && picker) picker = false;
  if (!channel) {
    if (state === null) return;
    // «Вернуть кадр в панель» должно ВЕРНУТЬ его: канал, который смотрели в центре
    // или в окне, переезжает в панель и продолжает идти там. Иначе кнопка гасила бы
    // эфир совсем — панель показала бы прошлый канал или пустоту.
    panelChannel = state.channel;
    state = null;
  } else {
    if (state?.channel.id === channel.id && state.channel.provider === channel.provider
      && state.mode === mode) return;
    state = { channel, mode };
  }
  emit();
}

export function getVideoPicker(): boolean {
  return picker;
}

/**
 * Открыть/закрыть каталог в центральном острове.
 *
 * Открытие возвращает развёрнутый в центре кадр обратно в панель — там он и
 * продолжит идти, пока выбирают следующий. Плавающее окно не трогаем: оно не
 * в центре и каталогу не мешает.
 */
export function setVideoPicker(open: boolean): void {
  if (open && blocked) return;
  if (picker === open) return;
  picker = open;
  // Каталог занял центр — развёрнутый там кадр уезжает в панель и идёт дальше,
  // пока выбирают следующий
  if (open && state?.mode === 'center') { panelChannel = state.channel; state = null; }
  emit();
}

/**
 * Кто сейчас занимает центральный остров. Страницы спрашивают ОДНО значение, а не
 * складывают два состояния сами: правило «каталог поверх кадра» должно жить в одном
 * месте, иначе две страницы разойдутся в трактовке.
 */
export function getVideoCenter(): VideoCenterView | null {
  if (picker) return 'picker';
  return state?.mode === 'center' ? 'player' : null;
}

/**
 * Освободить центральный остров: кадр уходит обратно в панель, каталог закрывается.
 * Зовут и крестиком в шапке, и страницы — когда центр понадобился файлу или задаче.
 */
export function closeVideoCenter(): void {
  const hadStage = state?.mode === 'center';
  if (!hadStage && !picker) return;
  if (hadStage) {
    // Кадр из центра уезжает в панель и продолжает идти там — см. setVideoStage(null)
    panelChannel = state!.channel;
    state = null;
  }
  picker = false;
  emit();
}

export function getPanelChannel(): VideoChannel | null {
  return panelChannel;
}

/**
 * Показать канал в боковой панели. Сюда ведёт выбор в каталоге: эфир идёт сбоку,
 * а каталог остаётся открытым — человек листает дальше и переключает, не выходя.
 *
 * Кадр, развёрнутый в ЦЕНТРЕ, при этом возвращается в панель: живой кадр в продукте
 * ровно один, и оставить оба значило бы дать два звука внахлёст.
 */
export function setPanelChannel(c: VideoChannel | null): void {
  panelChannel = c;
  if (c && state?.mode === 'center') state = null;
  emit();
  // Панель может быть закрыта или лежать в ящике рельсы — тогда выбранный канал
  // ушёл бы в невидимое место. Страница, услышав это, являет панель у себя:
  // раскладка — её епархия, стор про зоны и рельсы ничего не знает.
  if (c && typeof window !== 'undefined') window.dispatchEvent(new Event(VIDEO_PANEL_EVENT));
}

export function getVideoCenterBlocked(): boolean {
  return blocked;
}

/**
 * Сообщить, занят ли центральный остров чужим режимом. Зовёт страница — она одна
 * знает свою раскладку.
 *
 * Занятие центра ОСВОБОЖДАЕТ его от видео: кадр уходит обратно в панель, каталог
 * закрывается. Раньше тем же занимался эффект страницы по признаку «центр свободен»,
 * и у него была дыра: центр, занятый ЗАРАНЕЕ, признак не менял — эффект не срабатывал
 * вовсе, а кадр, отправленный в такой центр, пропадал с экрана насовсем.
 */
export function setVideoCenterBlocked(v: boolean): void {
  if (blocked === v) return;
  blocked = v;
  if (v) {
    // Центр занял чужой режим — кадр не гасим, а возвращаем в панель
    if (state?.mode === 'center') { panelChannel = state.channel; state = null; }
    picker = false;
  }
  emit();
}

export function setFloatRect(next: FloatRect): void {
  rect = clampRect(next);
  try { localStorage.setItem(RECT_KEY, JSON.stringify(rect)); } catch { /* приватный режим */ }
  emit();
}

function subscribe(cb: () => void): () => void {
  listeners.add(cb);
  return () => listeners.delete(cb);
}

/** Что показывают вне панели: канал и режим; null — всё в панели. */
export function useVideoStage(): VideoStageState | null {
  return useSyncExternalStore(subscribe, getVideoStage, getVideoStage);
}

/** Открыт ли каталог в центральном острове. */
export function useVideoPicker(): boolean {
  return useSyncExternalStore(subscribe, getVideoPicker, getVideoPicker);
}

/** Кто занимает центральный остров; null — центр свободен. */
export function useVideoCenter(): VideoCenterView | null {
  return useSyncExternalStore(subscribe, getVideoCenter, getVideoCenter);
}

/** Занят ли центральный остров чужим режимом: кнопки «в центр» на это время гаснут. */
/** Канал, играющий в боковой панели. */
export function usePanelChannel(): VideoChannel | null {
  return useSyncExternalStore(subscribe, getPanelChannel, getPanelChannel);
}

export function useVideoCenterBlocked(): boolean {
  return useSyncExternalStore(subscribe, getVideoCenterBlocked, getVideoCenterBlocked);
}

export function useFloatRect(): FloatRect {
  return useSyncExternalStore(subscribe, getFloatRect, getFloatRect);
}

/** Прямоугольники мест под кадр: их публикуют панель и центральный остров. */
export function useVideoSlots(): Record<VideoSlotKind, VideoSlot | null> {
  return useSyncExternalStore(subscribe, getVideoSlots, getVideoSlots);
}
