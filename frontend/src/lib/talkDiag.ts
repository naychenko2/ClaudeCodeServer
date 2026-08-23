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

// --- Тайминги круга разговора ---
//
// Главный вопрос к режиму «сколько человек ждёт ответа» не читается из потока событий
// глазами: нужны именно интервалы. Круг открывается концом речи и закрывается первым
// реально прозвучавшим звуком; метки внутри круга пишутся ОДИН раз (первая побеждает),
// чтобы повторные события фаз не смазывали картину.

// barge-in — перебивание голосом (одно на круг: метки пишутся «первая побеждает»,
// поэтому в сводке это булево «перебит», а не счётчик)
export type TalkMark = 'speech-end' | 'send' | 'turn-start' | 'first-audio' | 'barge-in';

interface TalkCycle { t0: number; marks: Partial<Record<TalkMark, number>> }

const MAX_CYCLES = 20;
let cycles: TalkCycle[] = [];

export function talkMark(mark: TalkMark): void {
  const now = Date.now();
  if (mark === 'speech-end') {
    cycles.push({ t0: now, marks: { 'speech-end': 0 } });
    if (cycles.length > MAX_CYCLES) cycles.splice(0, cycles.length - MAX_CYCLES);
    talkDiag('mark: speech-end');
    return;
  }
  const cycle = cycles[cycles.length - 1];
  if (!cycle || cycle.marks[mark] !== undefined) return; // круга нет или метка уже стоит
  cycle.marks[mark] = now - cycle.t0;
  talkDiag(`mark: ${mark} +${now - cycle.t0}мс от конца речи`);
}

function avg(values: number[]): number | null {
  if (values.length === 0) return null;
  return Math.round(values.reduce((a, b) => a + b, 0) / values.length);
}

// Сводка по кругам: строка на круг + среднее. Пустая, пока метки не набраны
function timingSummary(): string[] {
  if (cycles.length === 0) return [];
  const rows = cycles.map((c, i) => {
    const cell = (m: TalkMark) => c.marks[m] === undefined ? '—' : `+${c.marks[m]}мс`;
    // Перебивание — не у каждого круга: хвост строки только при метке
    const barge = c.marks['barge-in'] === undefined ? '' : `  перебит +${c.marks['barge-in']}мс`;
    return `#${String(i + 1).padStart(2, ' ')}  отправка ${cell('send')}` +
      `  ход ${cell('turn-start')}  первый звук ${cell('first-audio')}${barge}`;
  });
  const audio = cycles.map(c => c.marks['first-audio']).filter((v): v is number => v !== undefined);
  const send = cycles.map(c => c.marks['send']).filter((v): v is number => v !== undefined);
  const line = `среднее: речь→отправка ${avg(send) ?? '—'}мс, ` +
    `речь→первый звук ${avg(audio) ?? '—'}мс (кругов со звуком: ${audio.length})`;
  return ['', '=== тайминги кругов (от конца речи) ===', ...rows, line];
}

// Дамп текстом: относительное время от первой записи (мс) + заголовок с окружением
export function talkDiagDump(): string {
  const head = [
    `ua: ${typeof navigator !== 'undefined' ? navigator.userAgent : 'n/a'}`,
    `time: ${new Date().toISOString()}`,
  ];
  const t0 = entries.length ? entries[0].t : Date.now();
  const body = entries.map(e => `+${String(e.t - t0).padStart(6, ' ')}ms  ${e.msg}`);
  return [`=== talk diag ===`, ...head, ...body, ...timingSummary()].join('\n');
}

// Сброс кругов (тесты; в проде буфер сам вытесняется по MAX_CYCLES)
export function talkDiagResetCycles(): void {
  cycles = [];
}

// Сохранить дамп: в localStorage (переживает перезагрузку вкладки) и на сервер
// (POST /api/tts/diag — единственный способ достать лог с телефона, где консоли
// нет). Отправка fire-and-forget: разговор уже остановлен, ждать некого
export function talkDiagSave(): void {
  try { localStorage.setItem('talkDiagLast', talkDiagDump()); } catch { /* недоступен — не критично */ }
  void talkDiagUpload();
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

// --- Отправка на сервер (телефон/планшет: консоль недоступна) ---

import { request } from './offline';

// POST /api/tts/diag: дамп пишется в серверный лог с тегом [talk-diag]. Запрос
// идёт через общий request (авторизация, офлайн-гейт, таймаут); длина ограничена
// сервером (64k), фронтовый буфер меньше
export async function talkDiagUpload(): Promise<boolean> {
  try {
    await request('/tts/diag', {
      method: 'POST',
      body: JSON.stringify({ dump: talkDiagDump(), version: 'v2' }),
    });
    return true;
  } catch {
    return false; // связи нет — дамп остаётся в localStorage (talkDiagLast)
  }
}
