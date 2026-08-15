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
// - outside (User)          — файл не менял НИ ОДИН чат: правка мимо чатов (руками/Bash),
//                              либо правки всех чатов уже зафиксированы в git.
// - null — sessionFiles === undefined (нет чата / worktree-контекст / changed-by не
//   загрузился) И чужих нет: «мой» от «ничей» не отличить. Если чужие есть — состояние
//   определимо (как single/multi с mine=false: не претендуем на знание своего вклада,
//   показываем бледно). sessionFiles = myChangedPaths из git-стора (lib/git.ts).
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

// === Фильтр списка «Изменений» по авторству правки ===
// Ось у фильтра одна — КТО менял файл, и каждому режиму отвечает свой значок бейджа
// в строке файла (fileChatBadge выше), чтобы фильтр и список говорили одним языком.
// Корзины взаимоисключающие только по смыслу вопроса («покажи мне это»), но не по
// данным: файл, который трогали и активный чат, и чужой, попадает и в 'mine', и в
// 'others', и в 'shared' — это пересечение, а не ошибка.
// - 'shared'  — файл трогали 2+ чатов (значок MessagesSquare): за него «дерутся»,
//               там вероятны конфликты и затирание чужих правок;
// - 'outside' — файла не касался ни один чат (правка руками/Bash) либо правки чатов
//               уже зафиксированы в git.
// Неопределённость (sessionFiles === undefined: нет активного чата / worktree /
// changed-by не загрузился) — фильтр в UI недоступен целиком, поэтому здесь такие
// файлы просто не попадают ни в одну корзину, кроме 'all'.
export type ChatFilterMode = 'all' | 'mine' | 'others' | 'shared' | 'outside';

export function fileMatchesChatFilter(
  path: string,
  changedBy: Map<string, ChangedBySession[]> | undefined,
  sessionFiles: Set<string> | undefined,
  mode: ChatFilterMode,
): boolean {
  if (mode === 'all') return true;
  const otherCount = changedBy?.get(path)?.length ?? 0;
  const mine = sessionFiles === undefined ? undefined : sessionFiles.has(path.toLowerCase());
  if (mode === 'mine') return mine === true;
  if (mode === 'others') return otherCount > 0;
  // «Несколько чатов» — ровно то же условие, по которому строка получает MessagesSquare:
  // всего чатов 2+, считая активный
  if (mode === 'shared') return (mine === true ? 1 : 0) + otherCount >= 2;
  return mine === false && otherCount === 0;   // outside
}

// Счётчики пунктов меню фильтра — по ПОЛНОМУ списку скоупа, а не по видимому:
// цифра отвечает на вопрос «сколько получу, если нажму», поэтому не должна зависеть
// от того, что выбрано сейчас
export function countChatFilter(
  paths: string[],
  changedBy: Map<string, ChangedBySession[]> | undefined,
  sessionFiles: Set<string> | undefined,
): Record<ChatFilterMode, number> {
  const out: Record<ChatFilterMode, number> = { all: paths.length, mine: 0, others: 0, shared: 0, outside: 0 };
  for (const p of paths) {
    if (fileMatchesChatFilter(p, changedBy, sessionFiles, 'mine')) out.mine++;
    if (fileMatchesChatFilter(p, changedBy, sessionFiles, 'others')) out.others++;
    if (fileMatchesChatFilter(p, changedBy, sessionFiles, 'shared')) out.shared++;
    if (fileMatchesChatFilter(p, changedBy, sessionFiles, 'outside')) out.outside++;
  }
  return out;
}
