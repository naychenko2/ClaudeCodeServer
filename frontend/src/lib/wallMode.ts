// «Работаю на стене» — навигационная память режима (фича wall).
//
// Ставится, пока экран стены открыт; снимается ЯВНЫМ выходом («К проектам» на самой
// стене). Пока флаг стоит, вкладка «Проекты» ведёт обратно на стену: уход в «Чаты»,
// «Заметки» и прочие разделы не должен выкидывать из рабочего режима — ровно та же
// логика, по которой открытый проект «спит» и возвращается (App.switchHubTab).
const WALL_ACTIVE_KEY = 'cc_wall_active';

export function isWallActive(): boolean {
  try { return localStorage.getItem(WALL_ACTIVE_KEY) === '1'; } catch { return false; }
}

export function setWallActive(on: boolean): void {
  try {
    if (on) localStorage.setItem(WALL_ACTIVE_KEY, '1');
    else localStorage.removeItem(WALL_ACTIVE_KEY);
  } catch { /* приватный режим — режим просто не запомнится */ }
}
