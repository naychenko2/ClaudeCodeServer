// Тесты форматтера describeLocalGpuQueue: все состояния, граничные случаи,
// короткая форма, мутации (предел 99, «старт ≈ 0 с» при null).

import { describe, expect, it } from 'vitest';
import { describeLocalGpuQueue, type QueueLine } from './localGpuQueue';
import type { LocalGpuQueueInfo } from '../types/localGpuQueue';

const NOW = 1_760_000_000_000;

// Короткий helper: вызов форматтера с заданными opts.
function fmt(info: Partial<LocalGpuQueueInfo>, compact = false): QueueLine {
  const full: LocalGpuQueueInfo = { percentExact: false, ...info };
  return describeLocalGpuQueue(full, { compact, now: NOW });
}

// === Состояние: down ===

describe('down', () => {
  it('«Локальная генерация недоступна с HH:MM»', () => {
    const r = fmt({ downSince: NOW - 3600_000 });
    expect(r.kind).toBe('down');
    // Время зависит от локального часового пояса — проверяем структуру
    expect(r.text).toMatch(/^Локальная генерация недоступна с \d{2}:\d{2}$/);
    expect(r.progress).toBeUndefined();
  });

  it('короткая: «Локально недоступно с HH:MM»', () => {
    const r = fmt({ downSince: NOW - 3600_000 }, true);
    expect(r.kind).toBe('down');
    expect(r.text).toMatch(/^Локально недоступно с \d{2}:\d{2}$/);
  });

  it('downSince в будущем — тоже down', () => {
    const r = fmt({ downSince: NOW + 999_999_999 });
    expect(r.kind).toBe('down');
  });

  it('downSince=NaN (не конечное) → вид без времени, не «NaN:NaN»', () => {
    const r = fmt({ downSince: Number.NaN });
    expect(r.kind).toBe('down');
    expect(r.text).toBe('Локальная генерация недоступна');
  });

  it('downSince=NaN, короткая → «Локально недоступно»', () => {
    const r = fmt({ downSince: Number.NaN }, true);
    expect(r.text).toBe('Локально недоступно');
  });
});

// === Состояние: busy ===

describe('busy', () => {
  it('«Карта занята видео · ≈ 12 мин» (полная, без двоеточия)', () => {
    const r = fmt({ busy: { kind: 'video', heavy: true, remainingSeconds: 720 } });
    expect(r.kind).toBe('busy');
    expect(r.text).toBe('Карта занята видео · ≈ 12 мин');
    expect(r.progress?.mode).toBe('estimate');
  });

  it('короткая: «Карта занята видео · ≈ 12 мин» (единая форма, без двоеточия)', () => {
    const r = fmt({ busy: { kind: 'video', heavy: true, remainingSeconds: 720 } }, true);
    // Мутация: вернуть двоеточие в компактной форме → toBe красит
    expect(r.text).toBe('Карта занята видео · ≈ 12 мин');
  });

  it('без ETA: «Карта занята долгой задачей: видео» (двоеточие — как в макете)', () => {
    const r = fmt({ busy: { kind: 'video', heavy: true, remainingSeconds: null } });
    expect(r.text).toBe('Карта занята долгой задачей: видео');
  });

  it('без ETA, короткая: «Карта занята долгой задачей: видео» (единая форма)', () => {
    const r = fmt({ busy: { kind: 'video', heavy: true, remainingSeconds: null } }, true);
    expect(r.text).toBe('Карта занята долгой задачей: видео');
  });

  it('kind=image → «картинка»', () => {
    const r = fmt({ busy: { kind: 'image', heavy: true, remainingSeconds: 60 } });
    expect(r.text).toContain('картинка');
  });

  it('kind=audio → «звук»', () => {
    const r = fmt({ busy: { kind: 'audio', heavy: false, remainingSeconds: 30 } });
    expect(r.text).toContain('звук');
  });

  it('kind=train → «обучение»', () => {
    const r = fmt({ busy: { kind: 'train', heavy: true, remainingSeconds: 3600 } });
    expect(r.text).toContain('обучение');
  });

  it('kind=transcribe → «транскрибация»', () => {
    const r = fmt({ busy: { kind: 'transcribe', heavy: false, remainingSeconds: 60 } });
    expect(r.text).toContain('транскрибация');
  });

  it('kind=instance → «другой инстанс AI Home»', () => {
    const r = fmt({ busy: { kind: 'instance', heavy: true, remainingSeconds: 120 } });
    expect(r.text).toContain('другой инстанс');
  });

  it('kind=external → «внешняя задача»', () => {
    const r = fmt({ busy: { kind: 'external', heavy: false, remainingSeconds: 60 } });
    expect(r.text).toContain('внешняя задача');
  });

  it('неизвестный kind → «задача»', () => {
    const r = fmt({ busy: { kind: 'mystery', heavy: false, remainingSeconds: 60 } });
    expect(r.text).toContain('задача');
  });
});

// === Состояние: queued ===

describe('queued', () => {
  it('«В очереди · 2-я · старт ≈ через 4 мин»', () => {
    const r = fmt({ ahead: 1, startInSeconds: 240 });
    expect(r.kind).toBe('queued');
    expect(r.text).toBe('В очереди · 2-я · старт ≈ через 4 мин');
    expect(r.progress?.mode).toBe('estimate');
  });

  it('короткая: «В очереди · 2-я · ≈ 4 мин»', () => {
    const r = fmt({ ahead: 1, startInSeconds: 240 }, true);
    expect(r.text).toBe('В очереди · 2-я · ≈ 4 мин');
  });

  it('ahead=0: «Следующая · старт ≈ через 40 с»', () => {
    const r = fmt({ ahead: 0, startInSeconds: 40 });
    expect(r.text).toBe('Следующая · старт ≈ через 40 с');
  });

  it('ahead=0, короткая: «Следующая · ≈ 40 с»', () => {
    const r = fmt({ ahead: 0, startInSeconds: 40 }, true);
    expect(r.text).toBe('Следующая · ≈ 40 с');
  });

  it('startInSeconds=null: «В очереди · 2-я» БЕЗ обещания времени', () => {
    const r = fmt({ ahead: 1, startInSeconds: null });
    expect(r.text).toBe('В очереди · 2-я');
    // Не должно быть «≈ 0 с» или «старт ≈»
    expect(r.text).not.toContain('старт');
    expect(r.text).not.toContain('0 с');
  });

  it('ahead=null: «В очереди» без номеров', () => {
    const r = fmt({ ahead: null, startInSeconds: null });
    expect(r.text).toBe('В очереди');
  });

  it('ahead=null, короткая: тоже без номеров', () => {
    const r = fmt({ ahead: null, startInSeconds: null }, true);
    expect(r.text).toBe('В очереди');
  });

  it('ahead=3: «В очереди · 4-я»', () => {
    const r = fmt({ ahead: 3, startInSeconds: 600 });
    expect(r.text).toContain('4-я');
  });

  it('ahead=0.5 (дробное) → clamp в 0 → «Следующая»', () => {
    const r = fmt({ ahead: 0.5, startInSeconds: 40 });
    expect(r.text).toBe('Следующая · старт ≈ через 40 с');
  });

  it('ahead=-1 (отрицательное) → clamp в 0 → «Следующая»', () => {
    const r = fmt({ ahead: -1, startInSeconds: 40 });
    expect(r.text).toBe('Следующая · старт ≈ через 40 с');
  });

  it('ahead=1.5 (дробное) → floor в 1 → «2-я»', () => {
    const r = fmt({ ahead: 1.5, startInSeconds: 240 });
    expect(r.text).toContain('2-я');
  });

  it('ahead=null + известный startInSeconds → показываем ETA (решение п.6)', () => {
    const r = fmt({ ahead: null, startInSeconds: 240 });
    expect(r.text).toBe('В очереди · старт ≈ через 4 мин');
  });

  it('ahead=null + известный startInSeconds, короткая → «В очереди · ≈ 4 мин»', () => {
    const r = fmt({ ahead: null, startInSeconds: 240 }, true);
    expect(r.text).toBe('В очереди · ≈ 4 мин');
  });
});

// === Состояние: running ===

describe('running', () => {
  it('«Шаг 12 из 25 · осталось ≈ 30 с»', () => {
    const r = fmt({ steps: { step: 12, total: 25 }, remainingSeconds: 30, percent: 48, percentExact: true });
    expect(r.kind).toBe('running');
    expect(r.text).toBe('Шаг 12 из 25 · осталось ≈ 30 с');
    expect(r.progress?.mode).toBe('exact');
    expect(r.progress?.fraction).toBe(48); // 12/25 = 48%
  });

  it('короткая: «12 из 25 · ≈ 30 с»', () => {
    const r = fmt({ steps: { step: 12, total: 25 }, remainingSeconds: 30, percent: 48, percentExact: true }, true);
    expect(r.text).toBe('12 из 25 · ≈ 30 с');
  });

  it('stage=2: «этап 2 · шаг 3 из 8»', () => {
    const r = fmt({ steps: { step: 3, total: 8, stage: 2 }, remainingSeconds: 15, percent: 38, percentExact: true });
    expect(r.text).toContain('этап 2');
    expect(r.text).toContain('шаг 3 из 8');
  });

  it('stage=2, короткая: «2 · 3/8»', () => {
    const r = fmt({ steps: { step: 3, total: 8, stage: 2 }, remainingSeconds: 15, percent: 38, percentExact: true }, true);
    expect(r.text).toContain('2 · 3/8');
  });

  it('ETA не замерено (шаги есть): «Шаг 12 из 25 · время не замерено»', () => {
    const r = fmt({ steps: { step: 12, total: 25 }, remainingSeconds: null, percent: 48, percentExact: true });
    expect(r.text).toBe('Шаг 12 из 25 · время не замерено');
    expect(r.progress?.mode).toBe('exact');
  });

  it('ETA не замерено, короткая: «12 из 25 · без оценки»', () => {
    const r = fmt({ steps: { step: 12, total: 25 }, remainingSeconds: null, percent: 48, percentExact: true }, true);
    expect(r.text).toBe('12 из 25 · без оценки');
  });

  it('нет ни шагов, ни ETA: ровно «Идёт · время не замерено» (percent=40, percentExact=false)', () => {
    // percent=40 — это 40% ноды, НЕ «40 мин»: точный текст ловит ложный elapsed (мутация п.1)
    const r = fmt({ steps: null, remainingSeconds: null, percent: 40, percentExact: false });
    expect(r.kind).toBe('running');
    expect(r.text).toBe('Идёт · время не замерено');
    // percent есть, percentExact=false → estimate, fraction=40
    expect(r.progress?.mode).toBe('estimate');
    expect(r.progress?.fraction).toBe(40);
  });

  it('нет ни шагов, ни ETA, короткая: ровно «Идёт · без оценки»', () => {
    const r = fmt({ steps: null, remainingSeconds: null, percent: 40, percentExact: false }, true);
    expect(r.text).toBe('Идёт · без оценки');
  });

  it('percent=40, percentExact=true, без шагов → mode=exact, fraction=40', () => {
    const r = fmt({ steps: null, remainingSeconds: null, percent: 40, percentExact: true });
    expect(r.kind).toBe('running');
    expect(r.progress?.mode).toBe('exact');
    expect(r.progress?.fraction).toBe(40);
  });

  it('percent=100 → предел 99', () => {
    const r = fmt({ steps: { step: 25, total: 25 }, remainingSeconds: 5, percent: 100, percentExact: true });
    expect(r.progress?.fraction).toBe(99);
  });

  it('remainingSeconds=0: не пишет «0 с»', () => {
    const r = fmt({ steps: { step: 24, total: 25 }, remainingSeconds: 0, percent: 96, percentExact: true });
    // 24/25 = 96%, но текст не должен содержать «0 с»
    expect(r.text).not.toContain('0 с');
  });

  it('отрицательные значения: steps.step=-1 → clamp', () => {
    const r = fmt({ steps: { step: -1, total: 10 }, remainingSeconds: 30, percent: 10, percentExact: true });
    // fraction = max(0, -1/10*100) = 0
    expect(r.progress?.fraction).toBe(0);
  });

  it('steps.total=0: не делим на ноль → estimate (нет точных шагов)', () => {
    // total=0 → не входим в ветку steps; percentExact=false → estimate (не exact по percent)
    const r = fmt({ steps: { step: 0, total: 0 }, remainingSeconds: 30, percent: 50, percentExact: false });
    expect(r.progress?.mode).toBe('estimate');
  });
});

// === M2: running (новое поле) ===

describe('M2: running', () => {
  it('running=true + ahead=0 → kind=running (не queued)', () => {
    const r = fmt({ running: true, ahead: 0, startInSeconds: 40 });
    expect(r.kind).toBe('running');
  });

  it('running=true, без шагов и ETA → «Идёт · время не замерено»', () => {
    const r = fmt({ running: true, steps: null, remainingSeconds: null, percent: null, percentExact: null });
    expect(r.kind).toBe('running');
    expect(r.text).toBe('Идёт · время не замерено');
  });

  it('running=true, без шагов и ETA, короткая → «Идёт · без оценки»', () => {
    const r = fmt({ running: true, steps: null, remainingSeconds: null, percent: null, percentExact: null }, true);
    expect(r.text).toBe('Идёт · без оценки');
  });

  it('running=true + шаги → «Шаг 12 из 25 · осталось ≈ 30 с»', () => {
    const r = fmt({ running: true, steps: { step: 12, total: 25 }, remainingSeconds: 30, percent: 48, percentExact: true });
    expect(r.kind).toBe('running');
    expect(r.text).toBe('Шаг 12 из 25 · осталось ≈ 30 с');
  });

  it('running=false + ahead=0 → kind=queued (старая логика)', () => {
    const r = fmt({ running: false, ahead: 0, startInSeconds: 40 });
    expect(r.kind).toBe('queued');
  });

  it('running=null (нет поля) + ahead=0 → kind=queued (обратная совместимость)', () => {
    const r = fmt({ running: null, ahead: 0, startInSeconds: 40 });
    expect(r.kind).toBe('queued');
  });

  it('running=undefined (нет поля) + ahead=0 → kind=queued (обратная совместимость)', () => {
    const r = fmt({ ahead: 0, startInSeconds: 40 });
    expect(r.kind).toBe('queued');
  });
});

// === Состояние: unknown ===

describe('unknown', () => {
  it('пустой info → «Идёт»', () => {
    const r = fmt({});
    expect(r.kind).toBe('unknown');
    expect(r.text).toBe('Идёт');
  });
});

// === Мутации ===

describe('мутации', () => {
  it('снять предел 99 → percent=100 даёт 100, тест краснеет', () => {
    // Текущий код: Math.min(99, ...) → 99. Если убрать min → 100.
    const r = fmt({ steps: { step: 25, total: 25 }, remainingSeconds: 5, percent: 100, percentExact: true });
    // С пределом: 99. Без: 100.
    expect(r.progress?.fraction).toBe(99);
  });

  it('писать «старт ≈ 0 с» при null → тест краснеет', () => {
    // При startInSeconds=null текст НЕ должен содержать «старт ≈ 0 с»
    const r = fmt({ ahead: 1, startInSeconds: null });
    expect(r.text).not.toMatch(/старт ≈ 0 с/);
  });
});
