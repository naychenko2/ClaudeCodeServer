// «Работаю на стене» — навигационная память режима «Стены».
//
// WALL_ACTIVE_KEY ставится, пока экран стены открыт; снимается ЯВНЫМ выходом
// («К проектам» на самой стене). WALL_RETURN_KEY — режим зоны проектов в момент
// ухода из неё в другой раздел (стена/воркспейс/список): клик «Проекты» из
// другого раздела возвращает именно туда, где были до ухода, — та же логика,
// по которой открытый проект «спит» и возвращается (App.switchHubTab).
const WALL_ACTIVE_KEY = 'cc_wall_active';
const WALL_RETURN_KEY = 'cc_wall_return';

export function isWallActive(): boolean {
  try { return localStorage.getItem(WALL_ACTIVE_KEY) === '1'; } catch { return false; }
}

// Режим зоны проектов в момент ухода из неё: 'wall' | 'workspace' | 'list'
export type WallReturn = 'wall' | 'workspace' | 'list';

export function getWallReturn(): WallReturn | null {
  try {
    const v = localStorage.getItem(WALL_RETURN_KEY);
    return v === 'wall' || v === 'workspace' || v === 'list' ? v : null;
  } catch { return null; }
}

export function setWallReturn(to: WallReturn): void {
  try { localStorage.setItem(WALL_RETURN_KEY, to); } catch { /* не запомнится — и ладно */ }
}

export function setWallActive(on: boolean): void {
  try {
    if (on) localStorage.setItem(WALL_ACTIVE_KEY, '1');
    else {
      localStorage.removeItem(WALL_ACTIVE_KEY);
      // Явный выход из режима стирает и точку возврата: следующий уход из зоны
      // проектов запишет её заново
      localStorage.removeItem(WALL_RETURN_KEY);
    }
  } catch { /* приватный режим — режим просто не запомнится */ }
}
