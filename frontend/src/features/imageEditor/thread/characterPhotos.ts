// Блок «Фото персонажа» в карточке результата и окно «Какие фото взять» (ADR-027, шаг 18).
// Тексты дословно из заметки «Умный подбор фото — тексты»; вид — спека
// docs/mockups/character-smart-photos-spec.md. Логика — чистые функции: тесты с мутациями
// в characterPhotos.test.ts.

import type { ImageEditCharacter, ImageEditJobInput } from '../api';
import { ORIGIN } from './model';
import type { ImageThreadCharacter, ImageThreadLaunch } from './threadsApi';

// Тексты
export const CPS = {
  title: 'Фото персонажа',
  pick: {
    auto: { text: 'Подобраны под сюжет', tone: 'info' as const },
    manual: { text: 'Выбраны вами', tone: 'neutral' as const },
    fallback: { text: 'Обычный порядок', tone: 'neutral' as const, title: 'Подходящих под сюжет фото не нашлось — взяты как обычно' },
  },
  scenePrefix: 'Под сюжет: ',
  photoGone: 'Этого фото больше нет у персонажа',
  characterGone: 'Персонажа больше нет в проекте',
  singleHint: 'Добавьте фото в разных ракурсах — AI Home будет подбирать подходящее под сюжет.',
  pickerTitle: 'Какие фото взять',
  pickerSubtitle: 'Первое фото важнее остальных. Локальная модель берёт только одно.',
  reset: 'Сбросить выбор',
  loading: 'Загружаем фото…',
  redraw: 'Перерисовать',
  cancel: 'Отмена',
  invalid: 'Части фото больше нет у персонажа. Выберите заново.',
  main: 'Основное',
} as const;

// Счётчик и ошибки окна выбора; лимит — из DTO персонажа, не число в коде
export const counterText = (n: number, limit: number) => `Выбрано ${n} из ${limit}`;
export const overLimitText = (limit: number) => `Можно взять не больше ${limit} фото`;
export const emptySelectionText = 'Выберите хотя бы одно фото';
// Строка над кнопками окна: модель — текущий выбор «Чем»
export const newRunText = (model: string) => `Новый запуск: ${model} · исходная версия останется`;

// «Под сюжет: …»: признаки в порядке «ракурс, наклон, эмоция», null пропускаются.
// Наклон «level» («прямо») ничего человеку не говорит — не выводится; признаков не осталось — строки нет
const SCENE_ANGLE: Record<string, string> = { front: 'анфас', 'three-quarter': 'вполоборота', profile: 'в профиль' };
const SCENE_PITCH: Record<string, string> = { up: 'сверху', down: 'снизу' };
const SCENE_EMOTION: Record<string, string> = { neutral: 'спокойное', smile: 'улыбка', laugh: 'смех', serious: 'серьёзное', sad: 'грустное', surprised: 'удивление' };

export function sceneLine(scene: ImageThreadCharacter['scene']): string | null {
  if (!scene) return null;
  const parts = [
    scene.angle ? SCENE_ANGLE[scene.angle] : undefined,
    scene.pitch ? SCENE_PITCH[scene.pitch] : undefined,
    scene.emotion ? SCENE_EMOTION[scene.emotion] : undefined,
  ].filter((p): p is string => !!p);
  return parts.length ? CPS.scenePrefix + parts.join(', ') : null;
}

// Видимость блока: флаг включён, версия не исходник и не обрезка без ИИ, у запуска есть персонаж
export function stripVisible(a: { smart: boolean; versionId: string; cropNote: string | null; character: ImageThreadCharacter | null }): boolean {
  return a.smart && a.versionId !== ORIGIN && !a.cropNote && !!a.character;
}

// pick → бейдж: тон и текст. Акцент не берём: в шапке карточки он уже занят бейджем «В работе»
export function pickBadge(pick: ImageThreadCharacter['pick']): { tone: 'info' | 'neutral'; text: string; title?: string } {
  return CPS.pick[pick];
}

// Кнопка «Заменить»: нет у запуска без промпта (при перерисовке промпт теряется),
// при удалённом персонаже, когда у персонажа одно фото (тогда — подсказка empty-state)
// и у запуска с маской: сервер сам превращает edit + маска в inpaint, у других операций маски нет
export function showReplace(launch: Pick<ImageThreadLaunch, 'prompt' | 'op'>, card: ImageEditCharacter | null): boolean {
  if (!launch.prompt || !card) return false;
  if (launch.op === 'inpaint') return false;
  return card.photos.length >= 2;
}

// Лимит окна выбора — из DTO персонажа (бэкенд отдаёт его только при включённом флаге)
export function pickLimit(card: ImageEditCharacter | null): number {
  return card?.photosPerRequest ?? 3;
}

// Начальный выбор: фото этого запуска в их порядке; имён, которых уже нет в манифесте, отбрасываем
export function initialSelection(
  launchPhotos: string[], manifest: { file: string }[],
): string[] {
  const have = new Set(manifest.map(p => p.file));
  return launchPhotos.filter(f => have.has(f));
}

// Нажатие по ячейке: выбранное снимается (номера остальных сдвигаются), невыбранное
// добавляется в конец — порядок = порядок нажатий. Лимит: при полном выборе нажатие не делает ничего
export function togglePick(order: string[], file: string, limit: number): string[] {
  const i = order.indexOf(file);
  if (i >= 0) return order.filter(f => f !== file);
  if (order.length >= limit) return order;
  return [...order, file];
}

// Тело запуска «Перерисовать» (ручка POST …/jobs, StartJobForm): тот же промпт и та же база,
// что у запуска версии, плюс явный список фото. Свежая котировка (quoteId) — у вызывающего:
// провайдер, модель и формат берутся из текущего «Чем»/«Формата» и уходят в неё
export function redrawBody(a: {
  prompt: string;
  characterSlug: string;
  characterPhotos: string[];
  threadId: string;
  sessionId: string;
  versionId: string | null;
  baseStepId: string | null;
  contextRevision?: number | null;
}): Omit<ImageEditJobInput, 'quoteId'> {
  return {
    prompt: a.prompt,
    characterSlug: a.characterSlug,
    characterPhotos: a.characterPhotos,
    sessionId: a.sessionId,
    threadId: a.threadId,
    ...(a.versionId ? { versionId: a.versionId } : null),
    ...(a.baseStepId ? { baseStepId: a.baseStepId } : null),
    ...(a.contextRevision != null ? { contextRevision: a.contextRevision } : null),
  };
}
