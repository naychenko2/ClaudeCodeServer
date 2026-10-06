// Операции запуска картинок: операция по состоянию холста, причины отказа, цена низа, пометки запуска
// и выбор операции в памяти вкладки на проект. «Авто» — ровно pickOp. Чистые функции держит ops.test.ts.
// Прежняя панель «Картинки» удалена (ADR-023 §Д3): остался только общий код запуска.

import { useSyncExternalStore } from 'react';
import { AUTO_MODEL, type EditMode, type ImageEditEstimate, type ImageEditModel, type ImageEditOp } from '../api';
import { etaText, isFreeUnit, money, pickOp, priceSum, variantsWord } from '../format';
import type { OutpaintRatio, QuickAction } from '../editorInputs';
import { getPrefs, setEditChoice, type ProjectPrefs } from '../thread/prefs';
import { modeAware, type ImageMode } from '../thread/modeState';

export type PanelOp = 'auto' | ImageEditOp;

interface OpInfo {
  op: PanelOp;
  run: string;              // глагол кнопки запуска
  needImage?: boolean;
  needMask?: boolean;
  noPrompt?: boolean;       // запускается кнопкой, текст поля ввода не нужен
  one?: boolean;            // даёт один вариант
}

export const PANEL_OPS: OpInfo[] = [
  { op: 'auto', run: '' },
  { op: 'generate', run: 'Сгенерировать' },
  { op: 'edit', run: 'Изменить', needImage: true },
  { op: 'inpaint', run: 'Изменить отмеченное', needImage: true, needMask: true },
  { op: 'outpaint', run: 'Дорисовать', needImage: true, noPrompt: true },
  { op: 'removeBackground', run: 'Убрать фон', needImage: true, noPrompt: true, one: true },
  { op: 'upscale', run: 'Улучшить', needImage: true, noPrompt: true, one: true },
  { op: 'enhanceFaces', run: 'Улучшить лица', needImage: true, noPrompt: true, one: true },
];

const info = (op: PanelOp) => PANEL_OPS.find(o => o.op === op)!;

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
  // Расшифровка короткая («2 × 2 кр.», «2 × $0.04»): «2 × 2 кредита за картинку» резалась
  // многоточием уже на штатной ширине колонки
  const each = est.amount / count;
  const eachText = est.unit === 'usd' ? money(each, est.unit) : `${Math.round(each * 100) / 100} кр.`;
  return [priceSum(est.amount, est.unit, est.approx), `${count} × ${eachText}`];
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
  // Под флагом image-panel-v5 выбор живёт в префах «Править» проекта. «Авто» там нет —
  // это «Изменить» с инпейнтом по отметкам; «По тексту» — режим «Создать», а не операция
  if (modeAware()) {
    const op = patch.op === 'auto' ? 'edit' : patch.op === 'generate' ? undefined : patch.op;
    setEditChoice(projectId, {
      ...(op ? { op } : null),
      ...(patch.mode ? { editMode: patch.mode } : null),
      ...(patch.ratio ? { ratio: patch.ratio } : null),
    });
  } else {
    _choice.set(projectId, { ...getPanelChoice(projectId), ...patch });
  }
  _version++;
  _listeners.forEach(fn => fn());
}

// Выбор режима «Править» из префов проекта
export function editChoice(prefs: ProjectPrefs): PanelChoice {
  const e = prefs.edit;
  return { op: e?.op ?? 'edit', mode: e?.editMode ?? 'auto', ratio: e?.ratio ?? DEFAULT_CHOICE.ratio };
}

// Выбор, с которым пойдёт запуск. С режимом (флаг image-panel-v5): «Создать» — всегда по
// тексту, «Править» — выбор «Править» проекта
export function activeChoice(projectId: string, mode: ImageMode | null = null): PanelChoice {
  if (!mode) return getPanelChoice(projectId);
  const edit = editChoice(getPrefs(projectId));
  return mode === 'create' ? { op: 'generate', mode: 'auto', ratio: edit.ratio } : edit;
}

// Операция запуска с режимом: «Изменить» сам становится инпейнтом, если есть отметки (как
// pickOp у прежнего «Авто»); причина отказа — у выбранной операции, а не у вычисленной
export function modeOp(op: PanelOp, hasImage: boolean, hasMask: boolean): { op: ImageEditOp; reason: string } {
  if (op === 'edit' || op === 'inpaint' || op === 'auto') {
    return { op: pickOp(hasImage, hasMask), reason: opBlockReason('edit', hasImage, hasMask) };
  }
  return { op, reason: opBlockReason(op, hasImage, hasMask) };
}

export function usePanelChoiceVersion() {
  return useSyncExternalStore(
    fn => { _listeners.add(fn); return () => { _listeners.delete(fn); }; },
    () => _version, () => _version,
  );
}

export function __resetPanelChoice() {
  _choice.clear();
  _createRatio.clear();
  _version++;
}

// Пометки, которые уйдут с запуском: при «Вся картинка» закрашенное кистью (маска) не уходит,
// стрелки, рамки и подписи остаются подсказкой модели
export const launchMarks = <M extends { type: string }>(marks: M[], whole: boolean): M[] =>
  whole ? marks.filter(m => m.type !== 'mask') : marks;

// Пропорции новой картинки («Ещё настройки» режима «Создать»): в памяти вкладки на проект,
// null — как решит модель
const _createRatio = new Map<string, OutpaintRatio>();

export const getCreateRatio = (projectId: string): OutpaintRatio | null => _createRatio.get(projectId) ?? null;

export function setCreateRatio(projectId: string, ratio: OutpaintRatio | null) {
  if (ratio) _createRatio.set(projectId, ratio);
  else _createRatio.delete(projectId);
  _version++;
  _listeners.forEach(fn => fn());
}
