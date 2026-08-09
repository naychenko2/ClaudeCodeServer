// Типы документов и их свойства: общий словарь смысла для панели «Документы».
//
// Потребителей четыре — плашка в шапке превью, лента свойств документа, строка дерева и
// диалог настройки типов, — поэтому карта живёт здесь, а не в DocsPanel.tsx (он и так на 2400 строк).
// Тот же приём, что у lib/tasks.ts со статусами задач.

import type {
  DocEntry, DocDetail, DocProperty, DocPropertyColor, DocPropertyKind, DocTypeSchema,
} from '../types';
import type { BadgeTone } from '../components/ui';
import { TONE_DOT } from '../components/ui';

// Цвет из .docs — имя роли, а не цвета: тон плашки берётся отсюда, ни одного сырого
// значения ни в схеме, ни в разметке
export const PROP_TONE: Record<DocPropertyColor, BadgeTone> = {
  gray: 'neutral',
  accent: 'accent',
  success: 'success',
  warning: 'warning',
  danger: 'danger',
  info: 'info',
  plan: 'plan',
};

// Тот же смысл точкой — для строки дерева, где плашка не помещается по высоте
export const propDotColor = (color: DocPropertyColor) => TONE_DOT[PROP_TONE[color] ?? 'neutral'];

export const KIND_LABEL: Record<DocPropertyKind, string> = {
  choice: 'Выбор',
  date: 'Дата',
  text: 'Текст',
  docLink: 'Ссылка на документ',
};

export const COLOR_LABEL: Record<DocPropertyColor, string> = {
  gray: 'Серый',
  accent: 'Оранжевый',
  success: 'Зелёный',
  warning: 'Жёлтый',
  danger: 'Красный',
  info: 'Синий',
  plan: 'Фиолетовый',
};

// Палитра ВЫБОРА в редакторе типов — без accent: оранжевый в продукте занят главным
// действием и активным состоянием, а статус в оранжевом рассыпал бы второй акцент
// по всему дереву документов. Значение accent из файла при этом читается и рисуется
// (схему правят и руками) — просто не предлагается
export const COLOR_ORDER: DocPropertyColor[] =
  ['gray', 'success', 'warning', 'danger', 'info', 'plan'];

export function typeOf(
  types: DocTypeSchema[] | null | undefined,
  doc: { type?: string | null } | null | undefined,
): DocTypeSchema | null {
  if (!doc?.type || !types) return null;
  return types.find(t => t.id === doc.type) ?? null;
}

export function propValue(doc: { properties?: DocProperty[] | null } | null | undefined, key: string) {
  return doc?.properties?.find(p => p.key.toLowerCase() === key.toLowerCase());
}

// Значение для плашки: подпись и тон. known=false — значение есть в файле, но его нет
// в словаре типа. Такое НЕ прячем: опечатка в md должна быть видна серой плашкой,
// а не исчезать из интерфейса вместе со строкой, которая продолжает лежать в файле
export interface BadgeValue {
  key: string;
  value: string;              // как записано в файле
  label: string;              // как показывается человеку
  tone: BadgeTone;
  color: DocPropertyColor;
  known: boolean;
}

// Какое свойство типа показывается плашкой и точкой. Единственная точка ответа: панель
// и метка дерева обязаны согласиться, иначе точка в списке есть, а плашки в шапке нет
export function badgeKeyOf(type: DocTypeSchema | null): string | null {
  if (!type) return null;
  return type.badgeProperty ?? type.properties.find(p => p.kind === 'choice')?.key ?? null;
}

export function badgeOf(
  types: DocTypeSchema[] | null | undefined,
  doc: DocEntry | DocDetail | null | undefined,
): BadgeValue | null {
  const type = typeOf(types, doc);
  const key = badgeKeyOf(type);
  if (!type || !key) return null;

  const value = propValue(doc, key)?.value?.trim();
  if (!value) return null;

  const choice = type.properties
    .find(p => p.key.toLowerCase() === key.toLowerCase())?.choices
    ?.find(c => c.value.toLowerCase() === value.toLowerCase());

  const color: DocPropertyColor = choice?.color ?? 'gray';
  return {
    key,
    // value — то, что лежит в файле (по нему меню отмечает текущий пункт),
    // label — что показать человеку (у значения бывает своя подпись)
    value,
    label: choice?.title || choice?.value || value,
    tone: PROP_TONE[color] ?? 'neutral',
    color,
    known: !!choice,
  };
}

type ChoiceDef = { choices?: { value: string; color: DocPropertyColor; title?: string | null }[] | null };

// Тон значения выбора внутри меню и редактора
export function toneOfValue(def: ChoiceDef | undefined, value: string): BadgeTone {
  const c = def?.choices?.find(x => x.value.toLowerCase() === value.trim().toLowerCase());
  return PROP_TONE[c?.color ?? 'gray'] ?? 'neutral';
}

// Подпись значения выбора: у значения бывает свой заголовок, чтобы в файле осталось
// «Проектирование.» с точкой, а человеку показывалось «Проектирование». Незнакомое
// значение показываем как есть — это опечатка в документе, и её надо видеть
export function labelOfValue(def: ChoiceDef | undefined, value: string): string {
  const v = value.trim();
  const c = def?.choices?.find(x => x.value.toLowerCase() === v.toLowerCase());
  return c?.title || c?.value || v;
}

// Строки шапки из текста НЕ вырезаются — ни в превью, ни в центре. Причина не
// косметическая: комментарии к документу якорятся абсолютными офсетами в тексте файла
// (DocComments считает их по renderBody), и любое вырезание в середине документа увело бы
// все якоря ниже шапки. Диапазон разбора (`propsRange`) сервер по-прежнему отдаёт —
// он описывает, что именно прочитано как шапка.
