// Короткие подписи LoRA для выбора и строк: имя файла и дата вместо длинного пути.
// React здесь нет, тестируется в node. Значения (`value`) везде остаются полными путями.

import { formatTrainedDate } from './loraTrainView';

const EXT = /\.safetensors$/i;

// Последний сегмент пути без расширения
export function loraDisplayName(name: string): string {
  const last = name.split(/[\\/]/).filter(Boolean).pop() ?? name;
  return last.replace(EXT, '');
}

const timeOf = (iso: string | undefined) => {
  const t = iso ? Date.parse(iso) : NaN;
  return Number.isNaN(t) ? '' : new Date(t).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
};

// Путь без служебного префикса `ccs/<владелец>/`
const inner = (name: string) => {
  const parts = name.split(/[\\/]/).filter(Boolean);
  return (parts[0] === 'ccs' && parts.length > 2 ? parts.slice(2) : parts);
};

const folderOf = (name: string) => {
  const parts = inner(name);
  return parts.length > 1 ? parts[parts.length - 2] : '';
};

const innerPath = (name: string) => inner(name).join('/').replace(EXT, '');

// Подписи для набора имён: `имя · 10 октября`. Совпали — добавляем время, потом папку, потом путь внутри префикса.
export function loraLabels(names: string[], trained?: Record<string, string>): Map<string, string> {
  const dateOf = (n: string) => formatTrainedDate(trained?.[n]);
  const steps: Array<(n: string) => string> = [
    n => [loraDisplayName(n), dateOf(n)].filter(Boolean).join(' · '),
    n => {
      const d = dateOf(n);
      return d ? `${loraDisplayName(n)} · ${d}, ${timeOf(trained?.[n])}` : loraDisplayName(n);
    },
    n => [steps[1](n), folderOf(n)].filter(Boolean).join(' · '),
    n => [steps[1](n), innerPath(n)].filter(Boolean).join(' · '),
  ];
  const result = new Map<string, string>();
  let pending = [...new Set(names)];
  for (const step of steps) {
    const groups = new Map<string, string[]>();
    for (const n of pending) {
      const l = step(n);
      groups.set(l, [...(groups.get(l) ?? []), n]);
    }
    pending = [];
    for (const [label, group] of groups) {
      if (group.length === 1) result.set(group[0], label);
      else pending.push(...group);
    }
    if (!pending.length) break;
  }
  // совпало на всех шагах — полное имя уникально по определению
  for (const n of pending) result.set(n, n);
  return result;
}

// Подпись подключённой «своей» LoRA — та же, что в селекте: считается по полному списку.
// Список недоступен — по одному имени, без пути.
export function ownLoraLabel(
  own: string, ownTrainedAt: string | null | undefined, list: { available: boolean; items: string[]; trained?: Record<string, string> } | null,
): string {
  const trained = { ...(list?.trained ?? {}) };
  if (ownTrainedAt && !trained[own]) trained[own] = ownTrainedAt;
  const names = list?.available ? [own, ...list.items.filter(i => i !== own)] : [own];
  return loraLabels(names, trained).get(own) ?? own;
}
