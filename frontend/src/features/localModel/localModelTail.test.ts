// Хвост чипа Ctx для локальной модели: все фазы, 0 ток/с (не показываем), N ждущих,
// нет данных, компактность (мобила, вариант А), k-формат (≥ 1000).
// Мутация «показать 0 ток/с» должна покрасить тест.

import { describe, expect, it } from 'vitest';
import { localModelTail, localModelQueueText, formatTps } from './localModelTail';
import type { LocalModelLiveState } from './useLocalModelLive';

const base: LocalModelLiveState = {
  seen: true, phase: 'idle', genTps: null, waitingTotal: null, lastTurn: null, lastActivityAt: null,
};
const st = (over: Partial<LocalModelLiveState>): LocalModelLiveState => ({ ...base, ...over });

describe('localModelTail: генерация (десктоп)', () => {
  it('со скоростью > 0 — «N ток/с»', () => {
    expect(localModelTail(st({ phase: 'generating', genTps: 38 }))).toEqual({ text: '38 ток/с', tone: 'normal' });
  });

  it('0 ток/с НЕ показываем (пока нет первого токена) — «читает…»', () => {
    const tail = localModelTail(st({ phase: 'generating', genTps: 0 }));
    expect(tail).toEqual({ text: 'читает…', tone: 'normal' });
    expect(tail?.text).not.toBe('0 ток/с');
  });

  it('нет скорости (null) — «читает…»', () => {
    expect(localModelTail(st({ phase: 'generating', genTps: null }))).toEqual({ text: 'читает…', tone: 'normal' });
  });
});

describe('localModelTail: чтение промпта (десктоп)', () => {
  it('prefill — «читает…»', () => {
    expect(localModelTail(st({ phase: 'prefill' }))).toEqual({ text: 'читает…', tone: 'normal' });
  });
});

describe('localModelTail: очередь (десктоп, без номера позиции)', () => {
  it('N ждущих > 0 — «в очереди · ждут N» (warn)', () => {
    expect(localModelTail(st({ phase: 'queued', waitingTotal: 3 }))).toEqual({ text: 'в очереди · ждут 3', tone: 'warn' });
  });

  it('0 ждущих — просто «в очереди»', () => {
    expect(localModelTail(st({ phase: 'queued', waitingTotal: 0 }))).toEqual({ text: 'в очереди', tone: 'warn' });
  });

  it('нет данных о ждущих (null) — просто «в очереди»', () => {
    expect(localModelTail(st({ phase: 'queued', waitingTotal: null }))).toEqual({ text: 'в очереди', tone: 'warn' });
  });
});

describe('localModelTail: после хода (idle, десктоп)', () => {
  it('скорость последнего хода — приглушённо (dim)', () => {
    expect(localModelTail(st({ phase: 'idle', lastTurn: { genTps: 38 } }))).toEqual({ text: '38 ток/с', tone: 'dim' });
  });

  it('idle без скорости последнего хода — хвоста нет (null)', () => {
    expect(localModelTail(st({ phase: 'idle', lastTurn: null }))).toBeNull();
    expect(localModelTail(st({ phase: 'idle', lastTurn: { genTps: 0 } }))).toBeNull();
  });
});

describe('localModelTail: нет данных', () => {
  it('null-состояние (чат не на локальной модели) — null', () => {
    expect(localModelTail(null)).toBeNull();
  });
});

// === Компактность (мобила, вариант А) ===

describe('localModelTail compact: генерация', () => {
  it('со скоростью > 0 — «N т/с»', () => {
    expect(localModelTail(st({ phase: 'generating', genTps: 38 }), true)).toEqual({ text: '38 т/с', tone: 'normal' });
  });

  it('0 ток/с НЕ показываем — «читает»', () => {
    const tail = localModelTail(st({ phase: 'generating', genTps: 0 }), true);
    expect(tail).toEqual({ text: 'читает', tone: 'normal' });
    expect(tail?.text).not.toBe('0 т/с');
  });

  it('нет скорости (null) — «читает»', () => {
    expect(localModelTail(st({ phase: 'generating', genTps: null }), true)).toEqual({ text: 'читает', tone: 'normal' });
  });
});

describe('localModelTail compact: чтение промпта', () => {
  it('prefill — «читает»', () => {
    expect(localModelTail(st({ phase: 'prefill' }), true)).toEqual({ text: 'читает', tone: 'normal' });
  });
});

describe('localModelTail compact: очередь', () => {
  it('очередь — «очередь» (без числа)', () => {
    expect(localModelTail(st({ phase: 'queued', waitingTotal: 3 }), true)).toEqual({ text: 'очередь', tone: 'warn' });
  });

  it('0 ждущих — «очередь»', () => {
    expect(localModelTail(st({ phase: 'queued', waitingTotal: 0 }), true)).toEqual({ text: 'очередь', tone: 'warn' });
  });
});

describe('localModelTail compact: после хода (idle)', () => {
  it('idle — хвоста нет (null), лицо возвращается к «Ctx 61k»', () => {
    expect(localModelTail(st({ phase: 'idle', lastTurn: { genTps: 38 } }), true)).toBeNull();
  });
});

// === k-формат: скорость ≥ 1000 ===

describe('formatTps: k-формат', () => {
  it('999 — «999 ток/с» (ещё не k)', () => {
    expect(formatTps(999)).toBe('999 ток/с');
  });

  it('1000 — «1,0k ток/с»', () => {
    expect(formatTps(1000)).toBe('1,0k ток/с');
  });

  it('1240 — «1,2k ток/с»', () => {
    expect(formatTps(1240)).toBe('1,2k ток/с');
  });

  it('compact: 999 — «999 т/с»', () => {
    expect(formatTps(999, true)).toBe('999 т/с');
  });

  it('compact: 1000 — «1,0k т/с»', () => {
    expect(formatTps(1000, true)).toBe('1,0k т/с');
  });

  it('compact: 1240 — «1,2k т/с»', () => {
    expect(formatTps(1240, true)).toBe('1,2k т/с');
  });
});

describe('localModelTail: k-формат в хвосте', () => {
  it('генерация 1240 — «1,2k ток/с» (десктоп)', () => {
    expect(localModelTail(st({ phase: 'generating', genTps: 1240 }))).toEqual({ text: '1,2k ток/с', tone: 'normal' });
  });

  it('генерация 1240 compact — «1,2k т/с» (мобила)', () => {
    expect(localModelTail(st({ phase: 'generating', genTps: 1240 }), true)).toEqual({ text: '1,2k т/с', tone: 'normal' });
  });

  it('idle 999 — «999 ток/с» (dim)', () => {
    expect(localModelTail(st({ phase: 'idle', lastTurn: { genTps: 999 } }))).toEqual({ text: '999 ток/с', tone: 'dim' });
  });
});

// === Ожидание в очереди: округление «≈ N с» ===
// Мутация Math.max(1, Math.round(…)) → Math.round(…) должна покрасить 0 мс и 499 мс
// (без Math.max они дают «≈ 0 с», а не «≈ 1 с»).

describe('localModelQueueText: округление', () => {
  it('0 мс — «≈ 1 с» (не «≈ 0 с»)', () => {
    expect(localModelQueueText(0)).toBe('≈ 1 с');
  });

  it('499 мс — «≈ 1 с»', () => {
    expect(localModelQueueText(499)).toBe('≈ 1 с');
  });

  it('500 мс — «≈ 1 с»', () => {
    expect(localModelQueueText(500)).toBe('≈ 1 с');
  });

  it('1500 мс — «≈ 2 с»', () => {
    expect(localModelQueueText(1500)).toBe('≈ 2 с');
  });

  it('null / нет данных — «—» (как в поповере)', () => {
    expect(localModelQueueText(null)).toBe('—');
  });
});
