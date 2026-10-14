// Чистые функции вкладки «Медиа»: юнит-тесты без React и DOM.
// Мутации, которые обязаны покрасить:
//  - «Занят» по слотам/тяжёлой (isComfyBusy) — тон статуса;
//  - факт против ETA с порогом 20 % (isEtaOverdue) — смена порога красит;
//  - «В памяти» (loadedModelLine) — gpus==null → «узел не сообщает», не «загружена»;
//    без loadedModels идущий прогон подписан «работает сейчас», не «загружена»;
//  - «ещё N» (inMemoryLine) — показ не более 3 имён;
//  - чужой прогон без sessionId (isOwnJob) — без перехода.

import { describe, expect, it } from 'vitest';
import {
  comfyStatusInfo, isComfyBusy, loadedModelLine, modelFromOp, truncateName, inMemoryLine,
  formatClock, formatClockTime, queueStartLine, isEtaOverdue, doneOutcomeLine,
  lastHourSummary, runningProgress, remainingLine, isOwnJob, jobOwnerLabel,
  slotsLine, queueJobLabel, LAST_HOUR_VISIBLE,
} from './mediaFormat';
import type { LocalModelMedia, MediaDone, MediaRunning } from './api';

// === Статус ComfyUI ===

describe('comfyStatusInfo: статус → подпись + тон', () => {
  it('working → «Работает»/ok', () => {
    expect(comfyStatusInfo('working')).toEqual({ label: 'Работает', tone: 'ok' });
  });
  it('busy → «Занят»/busy', () => {
    expect(comfyStatusInfo('busy')).toEqual({ label: 'Занят', tone: 'busy' });
  });
  it('down → «Не отвечает»/down', () => {
    expect(comfyStatusInfo('down')).toEqual({ label: 'Не отвечает', tone: 'down' });
  });
  it('disabled → «Выключено»/disabled', () => {
    expect(comfyStatusInfo('disabled')).toEqual({ label: 'Выключено', tone: 'disabled' });
  });
});

// === «Занят»: 4 из 4 мест ИЛИ тяжёлая задача ===

describe('isComfyBusy: «Занят» по слотам и тяжёлой', () => {
  it('все места заняты → true', () => {
    expect(isComfyBusy({ used: 4, limit: 4 }, [])).toBe(true);
  });
  it('свободные места, но идёт тяжёлая → true', () => {
    expect(isComfyBusy({ used: 1, limit: 4 }, [{ heavy: true } as MediaRunning])).toBe(true);
  });
  it('свободные места, нет тяжёлой → false', () => {
    expect(isComfyBusy({ used: 1, limit: 4 }, [{ heavy: false } as MediaRunning])).toBe(false);
  });
  it('limit 0 (неизвестно) и нет тяжёлой → false', () => {
    expect(isComfyBusy({ used: 0, limit: 0 }, [])).toBe(false);
  });
});

// === «Модель в памяти» ===

describe('modelFromOp: имя модели из операции', () => {
  it('«Видео 5 с · MiniMax H3» → «MiniMax H3»', () => {
    expect(modelFromOp('Видео 5 с · MiniMax H3')).toBe('MiniMax H3');
  });
  it('«Картинка · Qwen-Image 2.1» → «Qwen-Image 2.1»', () => {
    expect(modelFromOp('Картинка · Qwen-Image 2.1')).toBe('Qwen-Image 2.1');
  });
  it('без «·» — вся строка', () => {
    expect(modelFromOp('MiniMax H3')).toBe('MiniMax H3');
  });
  it('пусто — пусто', () => {
    expect(modelFromOp('')).toBe('');
    expect(modelFromOp(null)).toBe('');
  });
});

describe('truncateName: длинные имена — с многоточием', () => {
  it('короткое имя — как есть', () => {
    expect(truncateName('MiniMax H3')).toBe('MiniMax H3');
  });
  it('ровно 24 символа — без обрезки', () => {
    const n = 'a'.repeat(24);
    expect(truncateName(n)).toBe(n);
  });
  it('длинное — обрезка с «…» (24 символа итого)', () => {
    const n = 'ACE-Step-v10-large.safetensors'; // 31 символ
    const t = truncateName(n);
    expect(t.endsWith('…')).toBe(true);
    expect(t.length).toBe(24);
    expect(t).toBe(`${n.slice(0, 23)}…`);
  });
});

describe('inMemoryLine: «В памяти: <имена>», не более 3 + «ещё N»', () => {
  it('одно имя — без «ещё»', () => {
    expect(inMemoryLine(['MiniMax H3'])).toBe('В памяти: MiniMax H3');
  });
  it('три имени — все, без «ещё»', () => {
    expect(inMemoryLine(['a', 'b', 'c'])).toBe('В памяти: a, b, c');
  });
  it('больше 3 — три + «ещё N»', () => {
    expect(inMemoryLine(['a', 'b', 'c', 'd', 'e'])).toBe('В памяти: a, b, c, ещё 2');
  });
  it('длинные имена — обрезаны с многоточием', () => {
    const line = inMemoryLine(['MiniMax H3', 'qwen_image.safetensors', 'ACE-Step-v10-large.safetensors']);
    expect(line).toBe(`В памяти: MiniMax H3, qwen_image.safetensors, ${truncateName('ACE-Step-v10-large.safetensors')}`);
  });
});

describe('loadedModelLine: «В памяти»', () => {
  const media = (over: Partial<LocalModelMedia>): LocalModelMedia => ({
    configured: true,
    comfy: { status: 'working', downSince: null },
    slots: { used: 0, limit: 4 },
    gpus: [{ uuid: 'g', index: 0, name: 'RTX 3090', roles: ['media'] }],
    loadedModels: null,
    running: [],
    queue: [],
    lastHour: [],
    ...over,
  });

  it('loadedModels есть → loaded, «В памяти: <имена>» (честно, не «загружена»)', () => {
    const line = loadedModelLine(media({ loadedModels: ['MiniMax H3'] }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('В памяти: MiniMax H3');
  });
  it('loadedModels более 3 → 3 имени + «ещё N»', () => {
    const line = loadedModelLine(media({ loadedModels: ['a', 'b', 'c', 'd', 'e'] }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('В памяти: a, b, c, ещё 2');
  });
  it('loadedModels null, gpus есть, ничего не идёт → empty (не «узел не сообщает»)', () => {
    const line = loadedModelLine(media({ loadedModels: null }));
    expect(line.state).toBe('empty');
    expect(line.label).toBe('ничего, модель загрузится на первой задаче');
  });
  it('loadedModels пустой [] → empty (не выдумываем «загружена»)', () => {
    const line = loadedModelLine(media({ loadedModels: [] }));
    expect(line.state).toBe('empty');
    expect(line.label).toBe('ничего, модель загрузится на первой задаче');
  });
  it('gpus==null и loadedModels null → «узел не сообщает» (не выдумываем «загружена»)', () => {
    const line = loadedModelLine(media({ gpus: null, loadedModels: null }));
    expect(line.state).toBe('unknown');
    expect(line.label).toBe('узел не сообщает');
  });
  it('loadedModels есть, но gpus==null → loaded (честные имена приоритетнее)', () => {
    const line = loadedModelLine(media({ gpus: null, loadedModels: ['MiniMax H3'] }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('В памяти: MiniMax H3');
  });
  it('loadedModels нет, идёт прогон → «работает сейчас: <модель>» (мутация «показывать «загружена»» красит)', () => {
    const line = loadedModelLine(media({
      loadedModels: null,
      running: [{ kind: 'video', op: 'Видео 5 с · MiniMax H3', heavy: false, own: true } as MediaRunning],
    }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('работает сейчас: MiniMax H3');
  });
  it('loadedModels есть, идёт прогон → loadedModels (не модель из op)', () => {
    const line = loadedModelLine(media({
      loadedModels: ['Qwen-Image 2.1'],
      running: [{ kind: 'video', op: 'Видео 5 с · MiniMax H3', heavy: false, own: true } as MediaRunning],
    }));
    expect(line.state).toBe('loaded');
    expect(line.label).toBe('В памяти: Qwen-Image 2.1');
  });
  it('ничего не идёт, loadedModels нет → empty', () => {
    const line = loadedModelLine(media({}));
    expect(line.state).toBe('empty');
    expect(line.label).toBe('ничего, модель загрузится на первой задаче');
  });
});

// === Время ===

describe('formatClock: секунды → M:SS / H:MM:SS', () => {
  it('«5:40», «0:47», «1:12»', () => {
    expect(formatClock(340)).toBe('5:40');
    expect(formatClock(47)).toBe('0:47');
    expect(formatClock(72)).toBe('1:12');
  });
  it('час+ → H:MM:SS', () => {
    expect(formatClock(3661)).toBe('1:01:01');
  });
  it('без значения — «—»', () => {
    expect(formatClock(null)).toBe('—');
    expect(formatClock(undefined)).toBe('—');
    expect(formatClock(-1)).toBe('—');
  });
});

describe('formatClockTime: unix-мс → HH:MM', () => {
  it('локальное время', () => {
    const ms = new Date(2026, 9, 13, 14, 32, 0).getTime();
    expect(formatClockTime(ms)).toBe('14:32');
  });
  it('без момента — пусто', () => {
    expect(formatClockTime(null)).toBe('');
    expect(formatClockTime(undefined)).toBe('');
  });
});

// === Очередь: ожидаемый старт ===

describe('queueStartLine: старт очереди', () => {
  const now = new Date(2026, 9, 13, 14, 0, 0).getTime();
  it('startInSeconds → «старт ≈ HH:MM · через N мин»', () => {
    // 120 с = 2 мин; старт в 14:02
    expect(queueStartLine(120, 120, now)).toBe('старт ≈ 14:02 · через 2 мин');
  });
  it('compact → «≈ N мин»', () => {
    expect(queueStartLine(120, 120, now, true)).toBe('≈ 2 мин');
  });
  it('только etaSeconds → «старт ≈ через N мин»', () => {
    expect(queueStartLine(null, 240, now)).toBe('старт ≈ через 4 мин');
  });
  it('ничего → «время не замерено»', () => {
    expect(queueStartLine(null, null, now)).toBe('время не замерено');
  });
});

// === «Последний час»: факт против ETA, порог 20 % ===

describe('isEtaOverdue: факт дольше обещанного на 20 %+', () => {
  it('ровно 20 % → true (>=)', () => {
    // fact 120, eta 100 → (120-100)/100 = 0.2
    expect(isEtaOverdue(120, 100)).toBe(true);
  });
  it('19 % → false', () => {
    // fact 119, eta 100 → 0.19
    expect(isEtaOverdue(119, 100)).toBe(false);
  });
  it('без eta (null) → false', () => {
    expect(isEtaOverdue(120, null)).toBe(false);
  });
  it('без fact (null) → false', () => {
    expect(isEtaOverdue(null, 100)).toBe(false);
  });
  it('eta 0 → false (делить нельзя)', () => {
    expect(isEtaOverdue(120, 0)).toBe(false);
  });
  it('мутация порога: 0.19 вместо 0.2 красит «ровно 20 % → true»', () => {
    // (120-100)/100 = 0.2; при пороге 0.19 это true, при 0.21 — false
    expect(isEtaOverdue(120, 100, 0.19)).toBe(true);
    expect(isEtaOverdue(120, 100, 0.21)).toBe(false);
  });
});

describe('doneOutcomeLine: «готово в HH:MM» / «упала: …»', () => {
  const done = (over: Partial<MediaDone>): MediaDone => ({
    kind: 'video', op: 'Видео 5 с · MiniMax H3', own: true, outcome: 'completed',
    runSeconds: 340, etaSeconds: 330, finishedAt: new Date(2026, 9, 13, 14, 12, 0).getTime(),
    ...over,
  });
  it('completed → «готово в HH:MM»', () => {
    expect(doneOutcomeLine(done({}))).toBe('готово в 14:12');
  });
  it('failed → «упала: {модель}»', () => {
    expect(doneOutcomeLine(done({ outcome: 'failed' }))).toBe('упала: MiniMax H3');
  });
  it('cancelled → «отменено»', () => {
    expect(doneOutcomeLine(done({ outcome: 'cancelled' }))).toBe('отменено');
  });
});

// === Итог «последнего часа» ===

describe('lastHourSummary: итог + «и ещё N»', () => {
  const mk = (n: number): MediaDone[] =>
    Array.from({ length: n }).map((_, i) => ({
      kind: 'video', op: 'Видео · M', own: true, outcome: i % 3 === 0 ? 'failed' : 'completed',
      runSeconds: 120, etaSeconds: 100, finishedAt: Date.now() - i * 60_000,
    }));
  it('считает готово/упало и среднее превышение', () => {
    const s = lastHourSummary(mk(3)); // 3 прогона: i=0 failed, i=1,2 completed
    expect(s.done).toBe(2);
    expect(s.failed).toBe(1);
    // (120-100)/100 = 0.2 по всем трём
    expect(s.avgOverPerc).toBeCloseTo(0.2, 5);
    expect(s.more).toBe(0);
  });
  it('больше 8 → «и ещё N»', () => {
    const s = lastHourSummary(mk(10));
    expect(s.more).toBe(10 - LAST_HOUR_VISIBLE);
  });
  it('пусто → нули, avg null', () => {
    const s = lastHourSummary([]);
    expect(s).toEqual({ done: 0, failed: 0, avgOverPerc: null, more: 0 });
  });
});

// === Прогресс и «осталось» ===

describe('runningProgress: полоса по шагам/percent', () => {
  it('шаги → exact, fraction=step/total (предел 0.99)', () => {
    const p = runningProgress({ steps: { step: 12, total: 25 } } as MediaRunning);
    expect(p.mode).toBe('exact');
    expect(p.fraction).toBeCloseTo(12 / 25, 5);
  });
  it('шаги 99/100 → 0.99 (предел)', () => {
    const p = runningProgress({ steps: { step: 99, total: 100 } } as MediaRunning);
    expect(p.fraction).toBe(0.99);
  });
  it('percentExact → exact по percent', () => {
    const p = runningProgress({ percent: 40, percentExact: true } as MediaRunning);
    expect(p.mode).toBe('exact');
    expect(p.fraction).toBe(0.4);
  });
  it('percent без exact → estimate (предел 0.95)', () => {
    const p = runningProgress({ percent: 90, percentExact: false } as MediaRunning);
    expect(p.mode).toBe('estimate');
    expect(p.fraction).toBe(0.9);
  });
  it('ничего → indeterminate', () => {
    expect(runningProgress({} as MediaRunning).mode).toBe('indeterminate');
  });
});

describe('remainingLine: «осталось ≈ N» / «время не замерено»', () => {
  it('remainingSeconds → «осталось ≈ N мин»', () => {
    expect(remainingLine({ remainingSeconds: 120 } as MediaRunning)).toBe('осталось ≈ 2 мин');
  });
  it('без ETA → «время не замерено»', () => {
    expect(remainingLine({ remainingSeconds: null } as MediaRunning)).toBe('время не замерено');
  });
});

// === Чей прогон: свой (с переходом) / чужой (без) ===

describe('isOwnJob / jobOwnerLabel: анонимизация', () => {
  it('свой с sessionId → true (кнопка, переход)', () => {
    expect(isOwnJob({ own: true, sessionId: 's1' })).toBe(true);
  });
  it('свой БЕЗ sessionId → false (без перехода): мутация «показывать sessionId чужого» красит', () => {
    expect(isOwnJob({ own: true, sessionId: null })).toBe(false);
  });
  it('чужой → false', () => {
    expect(isOwnJob({ own: false, sessionId: 's1' })).toBe(false);
  });
  it('чужой → «Чат другого пользователя», свой → название', () => {
    expect(jobOwnerLabel({ own: false, title: 'Секрет' })).toBe('Чат другого пользователя');
    expect(jobOwnerLabel({ own: true, title: 'Мой чат' })).toBe('Мой чат');
    expect(jobOwnerLabel({ own: true, title: null })).toBe('—');
  });
});

// === Счётчики/подписи ===

describe('slotsLine / queueJobLabel', () => {
  it('«N из M мест занято»', () => {
    expect(slotsLine({ used: 3, limit: 4 })).toBe('3 из 4 мест занято');
  });
  it('«N. операция»', () => {
    expect(queueJobLabel({ position: 2, op: 'Картинка · Qwen-Image 2.1' } as never)).toBe('2. Картинка · Qwen-Image 2.1');
  });
});
