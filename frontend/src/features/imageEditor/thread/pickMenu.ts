// Меню «Что править?» (макет image-panel-v5, вариант 1): его открывает приглушённый сегмент
// «Править», пока картинка не выбрана. Картинки чата свежими сверху, последняя правленая
// отмечена; разделитель, «С компьютера…» и «Из файлов проекта…» (только в проекте: в личном
// чате файлов проекта нет), подвал про «Работать с этой». Чистые функции — рисует их общий
// GenerationPickMenu.

import { pickRows } from 'aihome_shell/kit';
import { focusLabel, threadHasImage } from './model';
import type { ImageThread } from './threadsApi';

export interface ImagePickRow {
  id: string;
  name: string;
  // «вы · 5 мин назад» / «агент · 2 ч назад»
  sub: string;
  last: boolean;
}

export type ImagePickExtraKey = 'upload' | 'project';

export interface ImagePickMenuModel {
  rows: ImagePickRow[];
  extras: ImagePickExtraKey[];
  footer: string;
}

export const IMAGE_PICK_TITLE = 'Что править?';
export const IMAGE_PICK_FOOTER = 'Или «Работать с этой» на карточке в ленте';
export const IMAGE_PICK_MARK = 'правили последней';

const ms = (iso: string | null | undefined) => {
  const t = iso ? Date.parse(iso) : NaN;
  return Number.isFinite(t) ? t : 0;
};

// Последнее изменение нити — по нему «свежие сверху»
function touchedAt(t: ImageThread): number {
  return Math.max(ms(t.createdAt), ...(t.versions ?? []).map(v => ms(v.createdAt)), ...(t.launches ?? []).map(l => ms(l.at)));
}

// Кто сделал текущую картинку: последний запуск нити; файл без запусков — без автора
function author(t: ImageThread): string | null {
  const ls = t.launches ?? [];
  if (!ls.length) return null;
  const l = ls.reduce((a, b) => (ms(b.at) >= ms(a.at) ? b : a));
  return l.initiator === 'agent' ? 'агент' : 'вы';
}

// Черновик без картинки в меню не попадает: править в нём нечего. Выбранная — тоже.
// ago — «5 мин назад» по ISO-времени
export function imagePickMenu(
  threads: readonly ImageThread[], opts: { ago: (iso: string) => string; focusId: string | null; lastEditedId: string | null; personal: boolean },
): ImagePickMenuModel {
  const cands = threads.map(t => ({ id: t.id, at: touchedAt(t), hidden: !threadHasImage(t), thread: t }));
  const rows = pickRows(cands, { excludeId: opts.focusId, lastId: opts.lastEditedId }).map(({ thread: t, at, last }) => ({
    id: t.id,
    name: focusLabel(t, false, opts.personal),
    sub: [author(t), at ? opts.ago(new Date(at).toISOString()) : null].filter(Boolean).join(' · '),
    last,
  }));
  return { rows, extras: opts.personal ? [] : ['upload', 'project'], footer: IMAGE_PICK_FOOTER };
}
