import { describe, expect, it } from 'vitest';
import {
  clamp01, formatGpuMem, formatPerc, formatPower, formatSince, formatTps,
  gpuLabel, gpuOrdinal, whoRows,
} from './format';

describe('formatTps: ток/с', () => {
  it('целое значение — «38 ток/с»', () => {
    expect(formatTps(38)).toBe('38 ток/с');
  });
  it('дробное округляется до целого', () => {
    expect(formatTps(38.4)).toBe('38 ток/с');
    expect(formatTps(38.6)).toBe('39 ток/с');
  });
  it('движок не отдал значение — «—» (нули не рисуем)', () => {
    expect(formatTps(undefined)).toBe('—');
    expect(formatTps(null)).toBe('—');
  });
});

describe('formatPerc: процент из доли 0..1 (контракт бэкенда)', () => {
  it('0.61 → «61 %»', () => {
    expect(formatPerc(0.61)).toBe('61 %');
  });
  it('границы: 0 → «0 %», 0.5 → «50 %», 1 → «100 %»', () => {
    expect(formatPerc(0)).toBe('0 %');
    expect(formatPerc(0.5)).toBe('50 %');
    expect(formatPerc(1)).toBe('100 %');
  });
  it('вне диапазона — зажим: 1.2 → «100 %», -0.4 → «0 %»', () => {
    expect(formatPerc(1.2)).toBe('100 %');
    expect(formatPerc(-0.4)).toBe('0 %');
  });
  it('без значения — «—»', () => {
    expect(formatPerc(undefined)).toBe('—');
    expect(formatPerc(null)).toBe('—');
  });
});

describe('clamp01: зажим доли', () => {
  it('внутри диапазона — без изменений, на границах — граница', () => {
    expect(clamp01(0.37)).toBe(0.37);
    expect(clamp01(0)).toBe(0);
    expect(clamp01(1)).toBe(1);
  });
  it('вне диапазона — зажимает', () => {
    expect(clamp01(1.5)).toBe(1);
    expect(clamp01(-0.2)).toBe(0);
  });
});

describe('formatSince: «не отвечает с HH:MM» (unix-мс)', () => {
  it('unix-мс — локальное время HH:MM', () => {
    const ms = new Date(2026, 9, 13, 14, 32, 0).getTime();
    expect(formatSince(ms)).toBe('14:32');
  });
  it('минуты дополняются до двух цифр', () => {
    const ms = new Date(2026, 9, 13, 9, 5, 0).getTime();
    expect(formatSince(ms)).toBe('09:05');
  });
  it('без момента — пусто', () => {
    expect(formatSince(undefined)).toBe('');
    expect(formatSince(null)).toBe('');
  });
});

describe('gpuLabel / gpuOrdinal: подписи карт', () => {
  it('имя из nvidia-smi плюс место работы', () => {
    expect(gpuLabel('RTX 3090', 'text')).toBe('RTX 3090 · текст');
    expect(gpuLabel('RTX 3090', 'media')).toBe('RTX 3090 · медиа');
  });
  it('«карта N из M»', () => {
    expect(gpuOrdinal(0, 2)).toBe('карта 0 из 2');
    expect(gpuOrdinal(1, 2)).toBe('карта 1 из 2');
  });
});

describe('formatGpuMem: память карты', () => {
  it('«21,4/24 ГБ» — десятичная запятая, целое без хвоста', () => {
    expect(formatGpuMem(21934, 24576)).toBe('21,4/24 ГБ');
  });
  it('без данных — «—»', () => {
    expect(formatGpuMem(undefined, 24576)).toBe('—');
    expect(formatGpuMem(21934, undefined)).toBe('—');
  });
});

describe('formatPower: мощность', () => {
  it('«212/220 Вт» — факт и лимит', () => {
    expect(formatPower(212, 220)).toBe('212/220 Вт');
  });
  it('без лимита — «212 Вт»', () => {
    expect(formatPower(212, undefined)).toBe('212 Вт');
  });
  it('без факта — «—»', () => {
    expect(formatPower(undefined, 220)).toBe('—');
  });
});

describe('whoRows: строки «Кто сейчас на модели»', () => {
  // Резолвер имени проекта по projectId — как в сторе проектов
  const projectStore = (id?: string) => (id === 'proj-ai-home' ? 'AI Home' : id === 'proj-x' ? 'Личное' : undefined);

  it('свой чат: название, проект (из стора по projectId) и скорость генерации', () => {
    const rows = whoRows(
      [{ own: true, title: 'Рефакторинг сторожа', projectId: 'proj-ai-home', phase: 'generating', genTps: 38 }],
      undefined,
      projectStore,
    );
    expect(rows).toHaveLength(1);
    const row = rows[0];
    expect(row.kind).toBe('chat');
    if (row.kind !== 'chat') throw new Error('не может быть');
    expect(row.own).toBe(true);
    expect(row.title).toBe('Рефакторинг сторожа');
    expect(row.project).toBe('AI Home');
    expect(row.speed).toBe('38 ток/с');
    expect(row.tone).toBe('gen');
  });

  it('свой чат без projectId — проекта нет', () => {
    const rows = whoRows([{ own: true, title: 'Чат', phase: 'generating', genTps: 10 }], 0, projectStore);
    expect(rows).toHaveLength(1);
    const row = rows[0];
    if (row.kind !== 'chat') throw new Error('не может быть');
    expect(row.project).toBeUndefined();
  });

  it('свой чат, но резолвера нет (стор не прогрет) — проекта нет, не выдумываем', () => {
    const rows = whoRows([{ own: true, title: 'Чат', projectId: 'proj-ai-home', phase: 'generating', genTps: 10 }]);
    expect(rows).toHaveLength(1);
    const row = rows[0];
    if (row.kind !== 'chat') throw new Error('не может быть');
    expect(row.project).toBeUndefined();
  });

  // Правило «чужой чат без названия»: даже если движок отдаст название,
  // строка показывает заглушку — мутация (подставить c.title) обязана покраснить тест.
  // И без проекта: даже если резолвер знает имя, чужому чату подстроки не полагается
  it('чужой чат: «Чат другого пользователя», без названия, перехода и проекта', () => {
    const rows = whoRows(
      [{ own: false, title: 'Секретное название', projectId: 'proj-x', phase: 'waiting' }],
      undefined,
      projectStore,
    );
    expect(rows).toHaveLength(1);
    const row = rows[0];
    expect(row.kind).toBe('chat');
    if (row.kind !== 'chat') throw new Error('не может быть');
    expect(row.own).toBe(false);
    expect(row.title).toBe('Чат другого пользователя');
    expect(row.title).not.toBe('Секретное название');
    expect(row.project).toBeUndefined();
    expect(row.sessionId).toBeUndefined();
    expect(row.speed).toBe('ждёт');
    expect(row.tone).toBe('wait');
  });

  it('фазы: чтение промпта — «читает», ожидание — «ждёт»', () => {
    const rows = whoRows([
      { own: false, title: 'А', phase: 'reading' },
      { own: false, title: 'Б', phase: 'waiting' },
      { own: false, title: 'В', phase: 'generating' },
    ]);
    expect(rows.map(r => (r.kind === 'chat' ? r.speed : '?'))).toEqual(['читает', 'ждёт', 'читает']);
    expect(rows.map(r => (r.kind === 'chat' ? r.tone : '?'))).toEqual(['read', 'wait', 'read']);
  });

  it('«Прочие запросы: N» — только когда othersCount > 0', () => {
    const withOthers = whoRows([], 2);
    expect(withOthers).toHaveLength(1);
    expect(withOthers[0]).toEqual({ kind: 'others', count: 2, key: 'others' });

    const noOthers = whoRows([{ own: true, title: 'Чат', phase: 'generating', genTps: 10 }], 0);
    expect(noOthers).toHaveLength(1);
    expect(noOthers[0].kind).toBe('chat');
  });

  it('пустой список и othersCount 0 — строк нет', () => {
    expect(whoRows(undefined, undefined)).toEqual([]);
    expect(whoRows([], 0)).toEqual([]);
  });
});
