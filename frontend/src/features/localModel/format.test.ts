import { describe, expect, it } from 'vitest';
import {
  clamp01, formatGpuMem, formatPerc, formatPower, formatSince, formatTps, formatUpdatedAt,
  gpuCountLabel, gpuLabel, gpuOrdinal, menuStatus, standDot, whoRows,
} from './format';
import { mockBonsaiStats } from './localModel.fixtures';

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

describe('formatUpdatedAt: «обновлено HH:MM:SS» (время последней точки истории)', () => {
  it('последняя (максимальная по t) точка — локальное время HH:MM:SS', () => {
    const last = new Date(2026, 9, 13, 14, 32, 7).getTime();
    const earlier = new Date(2026, 9, 13, 14, 30, 0).getTime();
    expect(formatUpdatedAt([{ t: earlier }, { t: last }])).toBe('обновлено 14:32:07');
  });
  it('секунды/минуты дополняются до двух цифр', () => {
    const ms = new Date(2026, 9, 13, 9, 5, 3).getTime();
    expect(formatUpdatedAt([{ t: ms }])).toBe('обновлено 09:05:03');
  });
  it('без точек (null/пусто) — пусто: честного источника времени нет', () => {
    expect(formatUpdatedAt(undefined)).toBe('');
    expect(formatUpdatedAt(null)).toBe('');
    expect(formatUpdatedAt([])).toBe('');
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

describe('gpuCountLabel: склонение числа видеокарт', () => {
  it('1 → «1 карта», 2 → «2 карты», 5 → «5 карт»', () => {
    expect(gpuCountLabel(1)).toBe('1 карта');
    expect(gpuCountLabel(2)).toBe('2 карты');
    expect(gpuCountLabel(5)).toBe('5 карт');
  });
  it('русские формы: 21 → «21 карта», 22 → «22 карты», 25 → «25 карт», 11 → «11 карт»', () => {
    expect(gpuCountLabel(21)).toBe('21 карта');
    expect(gpuCountLabel(22)).toBe('22 карты');
    expect(gpuCountLabel(25)).toBe('25 карт');
    expect(gpuCountLabel(11)).toBe('11 карт');
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

// === Короткий статус пункта «Локальные модели» в мобильном меню «⋯» (Ф6, доработка 2) ===
// Колонка «Подсказка рельса / меню «⋯»» таблицы §4 макета bonsai-toolbar: в меню —
// одно слово или «через м:сс»; тон — тот же, что у точки рельса (standDot).
describe('menuStatus: короткий статус пункта «⋯» (макет §4)', () => {
  it('уступила карту — «уступила», muted', () => {
    expect(menuStatus(mockBonsaiStats('yielded'))).toEqual({ text: 'уступила', tone: 'muted' });
  });

  it('ждёт возврата — «через 2:40» (160 с, демо макета), muted', () => {
    expect(menuStatus(mockBonsaiStats('returning'))).toEqual({ text: 'через 2:40', tone: 'muted' });
  });

  it('остаток паузы 0 — «вот-вот», а не «через 0:00»', () => {
    const d = mockBonsaiStats('returning');
    const zero = { ...d, duty: { ...d.duty!, arbiter: { ...d.duty!.arbiter!, quietLeftS: 0 } } };
    expect(menuStatus(zero)).toEqual({ text: 'вот-вот', tone: 'muted' });
  });

  it('загружается — «загружается», muted', () => {
    expect(menuStatus(mockBonsaiStats('starting'))).toEqual({ text: 'загружается', tone: 'muted' });
  });

  it('ждёт память карты — «нет памяти», warning', () => {
    expect(menuStatus(mockBonsaiStats('waiting-vram'))).toEqual({ text: 'нет памяти', tone: 'warning' });
  });

  it('арбитр молчит, метрики отвечают — «нет арбитра», warning', () => {
    const d = mockBonsaiStats('ready');
    const silent = { ...d, duty: { ...d.duty!, arbiter: { ...d.duty!.arbiter!, reachable: false } } };
    expect(menuStatus(silent)).toEqual({ text: 'нет арбитра', tone: 'warning' });
  });

  it('арбитра нет вовсе (down) — «недоступна», а не «нет арбитра»', () => {
    expect(menuStatus(mockBonsaiStats('no-arbiter'))).toEqual({ text: 'недоступна', tone: 'warning' });
  });

  it('авария — «недоступна», warning', () => {
    expect(menuStatus(mockBonsaiStats('unknown'))).toEqual({ text: 'недоступна', tone: 'warning' });
  });

  it('в норме (работает) и без снимка — статуса нет', () => {
    expect(menuStatus(mockBonsaiStats('ready'))).toBeNull();
    expect(menuStatus(null)).toBeNull();
  });

  // Свой пункт в меню несёт свой ReactNode (точка OverflowItem всегда accent), но
  // тон и наличие берутся отсюда же, откуда точка рельса: два индикатора одного
  // состояния не могут разойтись.
  it('тон и наличие статуса совпадают с точкой рельса (standDot) на всех состояниях', () => {
    for (const s of ['ready', 'yielded', 'returning', 'starting', 'waiting-vram', 'unknown', 'no-arbiter'] as const) {
      const d = mockBonsaiStats(s);
      expect(menuStatus(d)?.tone ?? null, s).toBe(standDot(d));
    }
  });
});
