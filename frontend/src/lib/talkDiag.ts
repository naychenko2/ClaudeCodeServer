// Диагностика режима разговора: кольцевой буфер событий + живой вывод в консоль.
//
// Задача — расследование «свет дышит под голос, а распознавание глухое» на реальных
// устройствах: какие события двигка приходят, открывается ли наш getUserMedia-поток,
// что видит детектор конфликта. Лог ограничен (MAX записей), пишется всегда —
// это дёшево; шум в консоли промаркирован тегом [talk].
//
// Как вытащить лог с телефона:
//   1. При остановке петли дамп автоматически пишется в localStorage ('talkDiagLast')
//      и пытается попасть в буфер обмена (если браузер дал) — тост подтвердит.
//   2. window.__talkDiag() — полный дамп текстом (удобно из chrome://inspect).
//   3. Живой поток — консоль с тегом [talk].

const MAX = 400;

export interface DiagEntry { t: number; msg: string }

let entries: DiagEntry[] = [];

function fmt(v: unknown): string {
  if (typeof v === 'string') return v.length > 200 ? `${v.slice(0, 200)}…` : v;
  if (typeof v === 'number' || typeof v === 'boolean' || v == null) return String(v);
  try {
    const s = JSON.stringify(v);
    return s && s.length > 200 ? `${s.slice(0, 200)}…` : (s ?? String(v));
  } catch { return String(v); }
}

export function talkDiag(msg: string, ...args: unknown[]): void {
  const line = args.length ? `${msg} ${args.map(fmt).join(' ')}` : msg;
  entries.push({ t: Date.now(), msg: line });
  if (entries.length > MAX) entries.splice(0, entries.length - MAX);
  console.log('[talk]', line);
}

// Дамп текстом: относительное время от первой записи (мс) + заголовок с окружением
export function talkDiagDump(): string {
  const head = [
    `ua: ${typeof navigator !== 'undefined' ? navigator.userAgent : 'n/a'}`,
    `time: ${new Date().toISOString()}`,
  ];
  const t0 = entries.length ? entries[0].t : Date.now();
  const body = entries.map(e => `+${String(e.t - t0).padStart(6, ' ')}ms  ${e.msg}`);
  return [`=== talk diag ===`, ...head, ...body].join('\n');
}

// Сохранить дамп в localStorage: переживает перезагрузку вкладки. Вызывается
// при каждой остановке петли разговора
export function talkDiagSave(): void {
  try { localStorage.setItem('talkDiagLast', talkDiagDump()); } catch { /* недоступен — не критично */ }
}

// Прочитать последний сохранённый дамп (для window.__talkDiag после перезагрузки)
export function talkDiagSaved(): string | null {
  try { return localStorage.getItem('talkDiagLast'); } catch { return null; }
}

// Точка доступа из консоли/remote debugging
if (typeof window !== 'undefined') {
  (window as Window & { __talkDiag?: () => string }).__talkDiag = () =>
    entries.length ? talkDiagDump() : (talkDiagSaved() ?? '(пусто)');
}
