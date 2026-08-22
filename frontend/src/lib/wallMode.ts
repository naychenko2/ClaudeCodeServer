// «Работаю на стене» — навигационная память режима «Стены».
//
// WALL_ACTIVE_KEY ставится, пока экран стены открыт; снимается ЯВНЫМ выходом
// («К проектам» на самой стене). WALL_RETURN_KEY — режим зоны проектов в момент
// ухода из неё в другой раздел (стена/воркспейс/список): клик «Проекты» из
// другого раздела возвращает именно туда, где были до ухода, — та же логика,
// по которой открытый проект «спит» и возвращается (App.switchHubTab).
const WALL_ACTIVE_KEY = 'cc_wall_active';
const WALL_RETURN_KEY = 'cc_wall_return';
// Раздел, ИЗ КОТОРОГО вошли на стену. Отдельный ключ, а не значение WallReturn:
// тот отвечает на другой вопрос — «в каком режиме была зона проектов», и его читает
// пилюля «Проекты», которая обязана вернуть в зону проектов, а не на дашборд.
const WALL_ENTRY_KEY = 'cc_wall_entry';

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

// Откуда вошли на стену: 'home' — с дашборда, 'projects' — из зоны проектов.
// По этой метке выход со стены (App.exitWall) возвращает туда же, откуда пришли.
export type WallEntry = 'home' | 'projects';

export function getWallEntry(): WallEntry | null {
  try {
    const v = localStorage.getItem(WALL_ENTRY_KEY);
    return v === 'home' || v === 'projects' ? v : null;
  } catch { return null; }
}

export function setWallEntry(from: WallEntry): void {
  try { localStorage.setItem(WALL_ENTRY_KEY, from); } catch { /* не запомнится — и ладно */ }
}

export function setWallActive(on: boolean): void {
  try {
    if (on) localStorage.setItem(WALL_ACTIVE_KEY, '1');
    else {
      localStorage.removeItem(WALL_ACTIVE_KEY);
      // Явный выход из режима стирает и точку возврата: следующий уход из зоны
      // проектов запишет её заново
      localStorage.removeItem(WALL_RETURN_KEY);
      // ...и точку входа: следующий вход на стену запишет свою. ВНИМАНИЕ: exitWall
      // обязан прочитать метку ДО вызова setWallActive(false) — иначе ветка возврата
      // на главную мертва
      localStorage.removeItem(WALL_ENTRY_KEY);
    }
  } catch { /* приватный режим — режим просто не запомнится */ }
}
