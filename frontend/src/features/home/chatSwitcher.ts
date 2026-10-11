import type { HomeSessionInfo, HomeSummaryResponse } from '../../types';

// Сколько недавних чатов показывает мобильный переключатель (шторка «Недавние»)
export const SWITCHER_RECENT_LIMIT = 10;

export interface SwitcherSections {
  pinned: HomeSessionInfo[];
  recent: HomeSessionInfo[];
}

// Состав шторки переключателя из сводки /api/home/summary: живые и недавние — один
// список, порядок СТРОГО по времени последней активности (без подъёма живых наверх:
// строка не должна прыгать под пальцем, пока у неё меняется статус). Закреплённые —
// своей секцией сверху и в «Недавних» не повторяются; архивные не показываются вовсе
export function switcherSections(data: HomeSummaryResponse, limit = SWITCHER_RECENT_LIMIT): SwitcherSections {
  const seen = new Set<string>();
  const all: HomeSessionInfo[] = [];
  for (const s of [...data.active, ...data.recent]) {
    if (seen.has(s.id) || (s as { archived?: unknown }).archived === true) continue;
    seen.add(s.id);
    all.push(s);
  }
  // Сортировка стабильная: при равном времени сохраняется порядок сервера
  all.sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
  return {
    pinned: all.filter(s => s.isPinned),
    recent: all.filter(s => !s.isPinned).slice(0, limit),
  };
}
