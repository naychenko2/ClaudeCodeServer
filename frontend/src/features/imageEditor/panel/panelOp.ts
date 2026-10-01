// Операция и режим подбора панели «Картинки» (ADR-021 §3, записка image-editor-v4-panel-proposal.md,
// «Вкладка «Настройки»»). «Авто» — ровно нынешнее поведение pickOp. Выбор живёт в памяти вкладки
// на проект и действует только при флаге image-editor-panel: без флага запуск идёт как раньше.
// Тексты, причины и цена низа — чистые функции, их держит panelOp.test.ts.

import { useSyncExternalStore } from 'react';
import { FLAGS, getFlag } from 'aihome_shell/kit';
import { AUTO_MODEL, type EditMode, type ImageEditEstimate, type ImageEditModel, type ImageEditOp } from '../api';
import { etaText, isFreeUnit, money, pickOp, priceSum, variantsWord } from '../format';
import type { OutpaintRatio, QuickAction } from '../editorInputs';

export type PanelOp = 'auto' | ImageEditOp;

interface OpInfo {
  op: PanelOp;
  label: string;
  run: string;              // глагол кнопки запуска
  needImage?: boolean;
  needMask?: boolean;
  noPrompt?: boolean;       // запускается кнопкой, текст поля ввода не нужен
  one?: boolean;            // даёт один вариант
}

export const PANEL_OPS: OpInfo[] = [
  { op: 'auto', label: 'Авто', run: '' },
  { op: 'generate', label: 'По тексту', run: 'Сгенерировать' },
  { op: 'edit', label: 'Правка', run: 'Изменить', needImage: true },
  { op: 'inpaint', label: 'По отмеченному', run: 'Изменить отмеченное', needImage: true, needMask: true },
  { op: 'outpaint', label: 'Дорисовать за края', run: 'Дорисовать', needImage: true, noPrompt: true },
  { op: 'removeBackground', label: 'Убрать фон', run: 'Убрать фон', needImage: true, noPrompt: true, one: true },
  { op: 'upscale', label: 'Улучшить качество', run: 'Улучшить', needImage: true, noPrompt: true, one: true },
  { op: 'enhanceFaces', label: 'Улучшить лица', run: 'Улучшить лица', needImage: true, noPrompt: true, one: true },
];

const info = (op: PanelOp) => PANEL_OPS.find(o => o.op === op)!;

// [режим, название, подсказка]
export const EDIT_MODES: [EditMode, string, string][] = [
  ['auto', 'Авто', 'модель решает сама'],
  ['fast', 'Быстро', 'дешевле и быстрее'],
  ['precise', 'Точно', 'точнее следует правке'],
  ['photoreal', 'Фотореализм', 'для фото людей и мест'],
];

export const ONE_VARIANT_HINT = 'Эта операция даёт один вариант';

// Операция, которой пойдёт запуск: «Авто» — по состоянию холста, как полоса всегда
export const resolveOp = (op: PanelOp, hasImage: boolean, hasMask: boolean): ImageEditOp =>
  op === 'auto' ? pickOp(hasImage, hasMask) : op;

// Операции без промпта запускаются тем же путём, что быстрые действия редактора
export function quickOf(op: ImageEditOp): QuickAction | null {
  return op === 'outpaint' || op === 'removeBackground' || op === 'upscale' || op === 'enhanceFaces' ? op : null;
}

export const isOneVariant = (op: ImageEditOp) => !!info(op).one;
export const runVerb = (op: ImageEditOp) => info(op).run;

// Почему операция сейчас недоступна (пусто — доступна)
export function opBlockReason(op: PanelOp, hasImage: boolean, hasMask: boolean): string {
  const o = info(op);
  if (o.needImage && !hasImage) return 'Сначала выберите картинку в ленте или загрузите её';
  if (o.needMask && !hasMask) return 'Отметьте место кистью в редакторе картинки';
  return '';
}

// Строка под пилюлями: [начало, выделенное слово, конец]
export function opHint(op: PanelOp, hasImage: boolean, hasMask: boolean): [string, string, string] {
  if (op === 'auto') {
    const eff = info(pickOp(hasImage, hasMask)).label.toLowerCase();
    const why = !hasImage ? 'картинки ещё нет' : hasMask ? 'в редакторе отмечено место' : 'картинка выбрана, отметок нет';
    return ['Сейчас это ', eff, `: ${why}. Так ведёт себя полоса сейчас.`];
  }
  return info(op).noPrompt
    ? ['Текст в поле ввода не нужен — достаточно нажать кнопку внизу.', '', '']
    : ['Что сделать — напишите в поле ввода.', '', ''];
}

// Режим подбора есть только у модели «Авто»: явная модель сама и есть выбор
export const effectiveMode = (model: ImageEditModel | null, mode: EditMode): EditMode =>
  model?.id === AUTO_MODEL ? mode : 'auto';

// Цена низа в две строки: итог и расшифровка
export function footPrice(est: Pick<ImageEditEstimate, 'amount' | 'unit' | 'approx' | 'etaSeconds' | 'queueLength'> | null, count: number): [string, string] {
  if (!est) return ['Цена уточняется', variantsWord(count)];
  if (isFreeUnit(est.unit)) {
    const eta = est.etaSeconds != null && est.etaSeconds > 0 ? etaText(est.etaSeconds) : 'время уточняется';
    return ['Бесплатно', `${eta} · очередь GPU: ${est.queueLength ?? 0}`];
  }
  if (est.amount == null) return [priceSum(null, est.unit, est.approx), variantsWord(count)];
  return [priceSum(est.amount, est.unit, est.approx), `${count} × ${money(est.amount / count, est.unit)} за картинку`];
}

// Очередь общей GPU у локальных моделей; пустая — строки нет
export function queueText(est: Pick<ImageEditEstimate, 'unit' | 'queueLength'> | null): string | undefined {
  const q = est?.queueLength;
  return est && isFreeUnit(est.unit) && q ? `GPU: перед вами ${q} в очереди` : undefined;
}

// ── Выбор в памяти вкладки ──

export interface PanelChoice { op: PanelOp; mode: EditMode; ratio: OutpaintRatio }

export const DEFAULT_CHOICE: PanelChoice = { op: 'auto', mode: 'auto', ratio: '16:9' };

const _choice = new Map<string, PanelChoice>();
let _version = 0;
const _listeners = new Set<() => void>();

export const getPanelChoice = (projectId: string): PanelChoice => _choice.get(projectId) ?? DEFAULT_CHOICE;

export function setPanelChoice(projectId: string, patch: Partial<PanelChoice>) {
  _choice.set(projectId, { ...getPanelChoice(projectId), ...patch });
  _version++;
  _listeners.forEach(fn => fn());
}

// Выбор, с которым пойдёт запуск: без флага панели — всегда «Авто», как раньше
export const activeChoice = (projectId: string): PanelChoice =>
  getFlag(FLAGS.imageEditorPanel) ? getPanelChoice(projectId) : DEFAULT_CHOICE;

export function usePanelChoiceVersion() {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
}

export function __resetPanelChoice() {
  _choice.clear();
  _version++;
}
