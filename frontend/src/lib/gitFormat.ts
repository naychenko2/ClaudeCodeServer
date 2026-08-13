// Форматирование для git-поверхностей: разбор пути и относительная дата.
// Общий модуль, а не экспорт из панели: обеими функциями пользуются и «Изменения»
// (GitChangesRail), и вкладка «Авторы» просмотрщика (FileViewer) — держать их
// внутри одной из панелей значит связывать соседей друг с другом.

import type { ChangedBySession } from '../types';

// [папка-родитель, имя файла] из относительного пути
export function splitPath(p: string): [string, string] {
  const norm = p.replace(/\\/g, '/').replace(/\/+$/, '');
  const i = norm.lastIndexOf('/');
  return i < 0 ? ['', norm] : [norm.slice(0, i), norm.slice(i + 1)];
}

// Относительная дата: «2 ч назад», «5 мин назад», дальше — обычная дата
export function relTime(iso: string): string {
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  const diffMin = Math.floor((Date.now() - t) / 60_000);
  if (diffMin < 1) return 'только что';
  if (diffMin < 60) return `${diffMin} мин назад`;
  const h = Math.floor(diffMin / 60);
  if (h < 24) return `${h} ч назад`;
  const d = Math.floor(h / 24);
  if (d < 30) return `${d} дн назад`;
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' });
}

// Бейдж «кто менял файл» в строке файла активного скоупа (панель «Изменения»). Иконка
// кодирует КОЛИЧЕСТВО чатов, трогавших файл, а бледность и цифра — участие активного чата:
// - single (MessageSquare)  — файл менял РОВНО ОДИН чат (активный либо один чужой): значок
//                              без цифры. mine=true → контраст, mine=false → бледно.
// - multi  (MessagesSquare) — файл меняли 2+ чатов. mine=true → «+N» контраст (ты и чужие,
//                              N=число чужих); mine=false → «N» без плюса, бледно (только
//                              чужие, тебя нет).
// - outside (User)          — файл не менял НИ ОДИН чат: правка мимо чатов (руками/Bash).
// - null — sessionFiles === undefined (нет чата / worktree-чат / история грузится) И чужих
//   нет: «мой» от «ничей» не отличить. Если чужие есть — состояние определимо (как single/
//   multi с mine=false: не претендуем на знание своего вклада, показываем бледно).
export type FileChatBadge =
  | { kind: 'single'; mine: boolean; name?: string }
  | { kind: 'multi'; mine: boolean; count: number; names: string[] }
  | { kind: 'outside' };

export function fileChatBadge(
  path: string,
  changedBy: Map<string, ChangedBySession[]> | undefined,
  sessionFiles: Set<string> | undefined,
): FileChatBadge | null {
  // changedBy ключуется путём РОВНО как пришёл из git status, sessionFiles — lowercase
  const others = changedBy?.get(path) ?? [];
  const otherCount = others.length;
  const mine = sessionFiles === undefined ? undefined : sessionFiles.has(path.toLowerCase());
  const total = (mine === true ? 1 : 0) + otherCount; // всего чатов, трогавших файл

  if (total === 0) {
    // Ни одного чата: либо подтверждённый «ничей» (mine=false), либо неопределённость
    return mine === false ? { kind: 'outside' } : null;
  }
  if (total === 1) {
    // Один чат: только активный (mine=true, чужих 0) либо один чужой (mine=false/undefined)
    return { kind: 'single', mine: mine === true, name: others[0]?.name };
  }
  // 2+ чатов
  return { kind: 'multi', mine: mine === true, count: otherCount, names: others.map(s => s.name) };
}
