// Группировка версий макетов, главный файл и маппинг статусов (волна 3, вкладка
// «Планы и макеты»). Чистая логика: UI только показывает. Источник правды — БЭК
// (plans/mockups в ответе `GET /api/tasks/features/{id}`); фронт не пересчитывает
// статусы, а лишь делит макеты на «живые» и «Прошлые версии» по версии имени файла.
//
// Правило версий (макет features-overview-3-plans-2, «Откуда берётся макет»):
//   • корень — имя главного html БЕЗ хвоста версии (`-1`, `-2`, …, `-v2`, `-final`)
//     и без расширения; макеты с одним корнем — одна группа (один «макет» с версиями);
//   • «текущая» версия группы — `-final`, если он есть, иначе наибольший номер;
//   • макет «живой», если он — текущая версия группы И не отклонён (status ≠ rejected);
//     заменённые версии и отклонённые уходят в «Прошлые версии».

import type { FeatureMockup, FeatureMockupStatus } from '../types/featurePlans';
import { C } from './design';

// === Статусы макета ===
// draft — «на утверждении», approved — «утверждён», rejected — «отклонён».
export const MOCKUP_STATUS_LABEL: Record<FeatureMockupStatus, string> = {
  draft: 'на утверждении',
  approved: 'утверждён',
  rejected: 'отклонён',
};

// Цвета статус-чипа (макет §состояния): approved — success, draft — warning,
// rejected — приглушённый (bgSelected/textMuted).
export const MOCKUP_STATUS_STYLE: Record<FeatureMockupStatus, { bg: string; fg: string }> = {
  draft: { bg: C.warningBg, fg: C.warningText },
  approved: { bg: C.successBg, fg: C.successText },
  rejected: { bg: C.bgSelected, fg: C.textMuted },
};

// === Версии ===
// Разбор имени файла макета: корень (без хвоста версии и без расширения) + версия.
export interface MockupVersion {
  // Корень имени: без хвоста версии (`-1`/`-v2`/`-final`) и без расширения.
  root: string;
  // Версия: число (1, 2, …), 'final' или null (без хвоста версии).
  version: number | 'final' | null;
}

// Базовое имя файла (без каталога): путь макета — от корня проекта.
function basename(p: string): string {
  const i = p.lastIndexOf('/');
  return i >= 0 ? p.slice(i + 1) : p;
}

// Разобрать путь макета на корень + версию. `-final` старше любого номера;
// `-v2` и `-2` — один и тот же номер (2); без хвоста — версия null.
export function parseMockupVersion(path: string): MockupVersion {
  let base = basename(path);
  const dot = base.lastIndexOf('.');
  if (dot > 0) base = base.slice(0, dot);
  if (/-final$/.test(base)) return { root: base.slice(0, -'-final'.length), version: 'final' };
  const v = /-v(\d+)$/.exec(base);
  if (v) return { root: base.slice(0, v.index), version: parseInt(v[1], 10) };
  const n = /-(\d+)$/.exec(base);
  if (n) return { root: base.slice(0, n.index), version: parseInt(n[1], 10) };
  return { root: base, version: null };
}

// «Текущая» версия группы: `-final` старше любого номера; иначе наибольший номер;
// иначе null (группа из одного файла без хвоста).
export function currentVersion(versions: (number | 'final' | null)[]): number | 'final' | null {
  if (versions.includes('final')) return 'final';
  const nums = versions.filter((v): v is number => typeof v === 'number');
  if (nums.length === 0) return null;
  return Math.max(...nums);
}

// Главный файл для «Открыть макет»: первый `files[]` с `.html` и `exists`.
// null — открыть нечего (все html «не в master» или html нет) → кнопка disabled.
export function mainHtmlFile(m: FeatureMockup): string | null {
  for (const f of m.files) {
    if (f.path.endsWith('.html') && f.exists) return f.path;
  }
  return null;
}

// Число версий в группе каждого макета (для строки «дата · N версий»): один корень —
// одна группа, N = сколько макетов этой группы.
export function versionCounts(mockups: FeatureMockup[]): Map<string, number> {
  const groups = new Map<string, string[]>();
  for (const m of mockups) {
    const { root } = parseMockupVersion(m.path);
    const arr = groups.get(root) ?? [];
    arr.push(m.path);
    groups.set(root, arr);
  }
  const counts = new Map<string, number>();
  for (const paths of groups.values()) {
    for (const p of paths) counts.set(p, paths.length);
  }
  return counts;
}

// === Группировка ===
// Раздел макетов на «живые» (основной список) и «Прошлые версии» (свёрнутый блок).
export interface MockupSplit {
  // Живые макеты: текущая версия группы, не отклонённые.
  live: FeatureMockup[];
  // Прошлые версии: заменённые и отклонённые.
  past: FeatureMockup[];
}

// Разделить макеты на живые и прошлые. Порядок внутри каждого списка — стабильный:
// новее (modifiedAt) первыми, фолбэк — по имени; без modifiedAt — по имени.
export function splitMockups(mockups: FeatureMockup[]): MockupSplit {
  // Группы по корню имени: один корень — одна группа версий.
  const groups = new Map<string, FeatureMockup[]>();
  for (const m of mockups) {
    const { root } = parseMockupVersion(m.path);
    const arr = groups.get(root) ?? [];
    arr.push(m);
    groups.set(root, arr);
  }
  const live: FeatureMockup[] = [];
  const past: FeatureMockup[] = [];
  for (const items of groups.values()) {
    const current = currentVersion(items.map(m => parseMockupVersion(m.path).version));
    for (const m of items) {
      const isCurrent = parseMockupVersion(m.path).version === current;
      // Живой = текущая версия группы И не отклонён.
      if (isCurrent && m.status !== 'rejected') live.push(m);
      else past.push(m);
    }
  }
  const byNewest = (a: FeatureMockup, b: FeatureMockup): number => {
    const ta = a.modifiedAt ? Date.parse(a.modifiedAt) : 0;
    const tb = b.modifiedAt ? Date.parse(b.modifiedAt) : 0;
    if (ta !== tb) return tb - ta;
    return a.path.localeCompare(b.path);
  };
  live.sort(byNewest);
  past.sort(byNewest);
  return { live, past };
}
