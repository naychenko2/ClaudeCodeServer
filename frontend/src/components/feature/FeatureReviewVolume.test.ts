// Тесты секции «Объём» вкладки «Разбор» (FS-FR2 + FS-FRv; FP-R3): строки stepProgress (7 числовых +
// «Состояние плана») и строки плана («Исходный план», «Расширение», «Доработки после мержа») —
// только у открытой, как прежде; итоги закрытой — ровно три строки: «Шагов» с подписью-пояснением
// из линий («бэк N · фронт M», при линии 'other' с total > 0 — «· прочее K»), «Слияний»,
// «Доработок после мержа» (прежнее «Хвостов после мержа»). Убраны из секции строки по линиям,
// медианы (по проекту и этой фичи) и смены знаменателя (их перенёс FP-R2: в «Время» и в шкалу
// срока), «Задач всего» и «Пачек ревью» (решение Андрея 11.11, feature-review-returns-v1.md).
// Порядок пар «ключ — значение» проверяется целиком: (у открытой) шаги → план; (у закрытой)
// ровно три строки. Окружение node: renderToStaticMarkup.
//
// Мутации (краснеющие тесты — по фактическим прогонам в копии дерева, 11.11):
// (1) вернуть строку «Пачек ревью» в итоги закрытой — краснеет ровно «volume: ровно 3 строки…»;
// (2) в stepsSub убрать часть «прочее» — краснеет ровно «volume: lanes back 33 / front 45 →
//     подпись «бэк 33 · фронт 45»; с other 2 → «· прочее 2»»;
// (3) «Доработок после мержа» → «Хвостов после мержа» — краснеет ровно «volume: ровно 3 строки…».
// После отката каждой мутации — зелёные.

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, it, expect } from 'vitest';
import { FeatureReviewVolume } from './FeatureReviewVolume';
import type { StepProgress } from '../../lib/featureProgress';
import type { FeatureRun, FeatureMergeMark } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

function mkRun(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f-1',
    state: 'running',
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0,
    ready: false,
    ...over,
  };
}

function mkTotals(over: Partial<FeatureStats['timeline']['totals']> = {}): FeatureStats['timeline']['totals'] {
  return {
    elapsedMinutes: 0,
    workWallMinutes: 0,
    agentsSumMinutes: 0,
    notWorkingMinutes: 0,
    waitingMinutes: null,
    idleMinutes: null,
    waitingCount: null,
    longestWaitMinutes: null,
    errorCount: null,
    errorMinutes: null,
    ...over,
  };
}

function mkStats(over: Partial<FeatureStats> = {}): FeatureStats {
  return {
    source: 'journal',
    truncated: false,
    timeline: { from: '2026-10-20T10:00:00Z', to: '2026-10-20T16:00:00Z', stages: [], segments: [], current: null, totals: mkTotals() },
    breakdown: { personas: [], models: [], roles: [] },
    longestNow: null,
    spend: null,
    ...over,
  };
}

function mkProgress(over: Partial<StepProgress> = {}): StepProgress {
  return {
    planState: 'partial', total: 44, accepted: 30, review: 3, work: 2, notCreated: 6,
    withdrawn: 3, unlinkedReworks: 0, inMaster: 20, ...over,
  };
}

const html = (run: FeatureRun, stats: FeatureStats = mkStats()): string =>
  renderToStaticMarkup(createElement(FeatureReviewVolume, { run, stats }));

// Пары «ключ — значение» всех строк ReviewKv в порядке html (строки с value=null
// в html отсутствуют — ReviewKv их не рендерит). Значение с подстрокой («Шагов»
// с пояснением) — весь текст значения, подстрока через '\n' (подстрока — отдельная строка).
function kvPairs(h: string): [string, string][] {
  return [...h.matchAll(/<div data-testid="review-kv-row"[^>]*>([\s\S]*?)<\/div>/g)]
    .map((m) => {
      const parts = [...m[1].matchAll(/<span[^>]*>([\s\S]*?)<\/span>/g)]
        .map((s) => s[1].replace(/<span[^>]*>/g, '\n').replace(/<[^>]*>/g, ''));
      return [parts[0], parts.slice(1).join('\n')] as [string, string];
    });
}

// Фикстура c1: stepProgress {total 44, accepted 30, review 3, work 2, notCreated 6,
// withdrawn 3, inMaster 20} + lanes из 2 элементов (прежняя — тесты порядка и слов линий).
const C1_RUN = mkRun({ stepProgress: mkProgress(), lanes: [
  { lane: 'back', personaIds: ['p-1'], total: 24, accepted: 3, returns: 1 },
  { lane: 'front', personaIds: ['p-2'], total: 17, accepted: 3, returns: 0 },
] });

describe('шаги (c1)', () => {
  it('полные пары stepProgress: 7 числовых строк + «Состояние плана», в порядке', () => {
    // Без линий: ровно 8 строк — 7 числовых + состояние плана.
    expect(kvPairs(html(mkRun({ stepProgress: mkProgress() })))).toEqual([
      ['Всего шагов', '44'],
      ['Приняты', '30'],
      ['На ревью', '3'],
      ['В работе', '2'],
      ['Не заведены', '6'],
      ['Сняты', '3'],
      ['Уже в master', '20'],
      ['Состояние плана', 'частично'],
    ]);
  });

  it('нулевые корзины рисуются (0 — значение, не «нет данных»)', () => {
    const h = html(mkRun({ stepProgress: mkProgress({ withdrawn: 0 }) }));
    expect(kvPairs(h)).toContainEqual(['Сняты', '0']);
  });

  it('inMaster: поля нет — строки нет (аддитивное поле)', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: mkProgress({ inMaster: undefined }) })));
    expect(pairs.some((p) => p[0] === 'Уже в master')).toBe(false);
  });

  it('stepProgress нет (старый ответ) — строк шагов нет', () => {
    const h = html(mkRun());
    expect(h).not.toContain('Всего шагов');
    expect(kvPairs(h).length).toBe(0);
  });
});

// Фикстура c1: план вырос с 12 до 15, после мержа 1 доработка из 3 принята.
const PLAN_GROWN = mkProgress({
  total: 15, accepted: 10, review: 2, work: 2, notCreated: 1,
  baselineTotal: 12, followUpTotal: 3, followUpAccepted: 1,
});

describe('план и доработки (c1)', () => {
  it('рост знаменателя и доработки: «Исходный план» 12 шагов, «Расширение» +3, «Доработки» 1 из 3 — после строк шагов', () => {
    // Полный toEqual фиксирует порядок: блок плана — сразу после группы шагов (прежний
    // порядок «…до линий» снят: строк по линиям в секции больше нет).
    const pairs = kvPairs(html(mkRun({ stepProgress: PLAN_GROWN, lanes: [
      { lane: 'back', personaIds: ['p-1'], total: 24, accepted: 3, returns: 1 },
      { lane: 'front', personaIds: ['p-2'], total: 17, accepted: 3, returns: 0 },
    ] })));
    expect(pairs).toEqual([
      ['Всего шагов', '15'],
      ['Приняты', '10'],
      ['На ревью', '2'],
      ['В работе', '2'],
      ['Не заведены', '1'],
      ['Сняты', '3'],
      ['Уже в master', '20'],
      ['Состояние плана', 'частично'],
      ['Исходный план', '12 шагов'],
      ['Расширение', '+3'],
      ['Доработки после мержа', '1 из 3'],
    ]);
  });

  it('baselineTotal null или нет (не зафиксирована, старый ответ) и followUpTotal 0 — этих строк нет', () => {
    for (const p of [
      mkProgress({ total: 15, accepted: 10, review: 2, work: 2, notCreated: 1, baselineTotal: null, followUpTotal: 0 }),
      mkProgress({ total: 15, accepted: 10, review: 2, work: 2, notCreated: 1 }),
    ]) {
      const pairs = kvPairs(html(mkRun({ stepProgress: p })));
      for (const label of ['Исходный план', 'Расширение', 'Доработки после мержа']) {
        expect(pairs.some((r) => r[0] === label)).toBe(false);
      }
    }
  });

  it('знаменатель не рос (total == baselineTotal) — «Расширения» нет, «Исходный план» есть', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: mkProgress({ baselineTotal: 44, followUpTotal: 0 }) })));
    expect(pairs).toContainEqual(['Исходный план', '44 шага']);
    expect(pairs.some((r) => r[0] === 'Расширение')).toBe(false);
  });

  it('baselineTotal больше total (знаменатель сжимался) — «Расширения» нет (никакого «−N» и «+0»)', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: mkProgress({ total: 10, accepted: 6, review: 1, work: 1, notCreated: 2, baselineTotal: 12 }) })));
    expect(pairs.some((r) => r[0] === 'Расширение')).toBe(false);
  });

  it('доработки без принятого счётчика: followUpAccepted нет → «0 из N» (0 — значение)', () => {
    const pairs = kvPairs(html(mkRun({ stepProgress: mkProgress({ followUpTotal: 2 }) })));
    expect(pairs).toContainEqual(['Доработки после мержа', '0 из 2']);
  });

  it('закрытая с полными полями плана — строк плана нет (группа stepProgress не рисуется вовсе)', () => {
    const pairs = kvPairs(html(mkRun({ state: 'closed', stepProgress: PLAN_GROWN })));
    for (const label of ['Исходный план', 'Расширение', 'Доработки после мержа', 'Всего шагов']) {
      expect(pairs.some((r) => r[0] === label)).toBe(false);
    }
  });
});

describe('словарь «Состояния плана»', () => {
  it('pending/partial/cut/done — существующие слова формата (featureProgress.ts:71-72, макет :486)', () => {
    for (const [state, label] of [
      ['pending', 'не разобран'], ['partial', 'частично'], ['cut', 'разрезан'], ['done', 'готово'],
    ] as const) {
      expect(kvPairs(html(mkRun({ stepProgress: mkProgress({ planState: state }) })))).toContainEqual(['Состояние плана', label]);
    }
  });
});

describe('итоги закрытой — ровно три строки (FP-R3)', () => {
  // Фикстура — закрытая фича образца (78 шагов, 2 мержа, 32 доработки после мержа).
  const CLOSED_RUN = mkRun({
    state: 'closed',
    closed: true,
    summary: { mergedAt: '2026-10-30T12:00:00Z', durationMinutes: 200, tasks: 21, steps: 78, loops: 1 },
    merges: [
      { taskId: 'm-1', at: '2026-10-28T10:00:00Z', stepRefs: ['Ш1'] },
      { taskId: 'm-2', at: '2026-10-30T12:00:00Z', stepRefs: ['Ш2'] },
    ] as FeatureMergeMark[],
    postMergeTails: Array.from({ length: 32 }, (_, i) => `t-${i + 1}`),
  });
  // Та же закрытая, но с stepProgress и линиями (фича с планом): группы шагов и строк по
  // линиям нет — настоящий объём закрытой дают ровно три строки итогов (макет ov.vol :513).
  const CLOSED_FULL: FeatureRun = {
    ...CLOSED_RUN,
    stepProgress: mkProgress(),
    lanes: [
      { lane: 'back', personaIds: ['p-1'], total: 33, accepted: 30, returns: 2 },
      { lane: 'front', personaIds: ['p-2'], total: 45, accepted: 40, returns: 1 },
    ],
  };

  it('volume: ровно 3 строки — Шагов, Слияний, Доработок после мержа', () => {
    // Закрытая со всем возможным (план, линии, reviews): ни одной строки сверх трёх —
    // ни группы stepProgress, ни строк по линиям, ни «Задач всего»/«Пачек ревью».
    const pairs = kvPairs(html(CLOSED_FULL));
    expect(pairs).toEqual([
      ['Шагов', '78\nбэк 33 · фронт 45'],
      ['Слияний', '2'],
      ['Доработок после мержа', '32'],
    ]);
  });

  it('volume: lanes back 33 / front 45 → подпись «бэк 33 · фронт 45»; с other 2 → «· прочее 2»', () => {
    // Без линии 'other' — только две части; с 'other' total 2 — третья часть. Строки
    // по линиям («N шагов · принято · возвратов») нет: прежняя разбивка снята полностью.
    const withoutOther = kvPairs(html(CLOSED_FULL));
    expect(withoutOther[0]).toEqual(['Шагов', '78\nбэк 33 · фронт 45']);
    const withOther = kvPairs(html({
      ...CLOSED_FULL,
      lanes: [...CLOSED_FULL.lanes!, { lane: 'other', personaIds: [], total: 2, accepted: 1, returns: 0 }],
    }));
    expect(withOther[0]).toEqual(['Шагов', '78\nбэк 33 · фронт 45 · прочее 2']);
    expect(withOther.length).toBe(3);
  });

  it('«прочее» при total 0 — нет; «бэк»/«фронт» — только по существующим линиям', () => {
    // 'other' total 0 — пояснения нет (0 — не факт, FP-R3); линии 'бэк' нет — части нет.
    const pairs = kvPairs(html({
      ...CLOSED_RUN,
      lanes: [
        { lane: 'front', personaIds: [], total: 45, accepted: 40, returns: 0 },
        { lane: 'other', personaIds: [], total: 0, accepted: 0, returns: 0 },
      ],
    }));
    expect(pairs[0]).toEqual(['Шагов', '78\nфронт 45']);
  });

  it('нет lanes (старый ответ) — только число, подстроки нет', () => {
    const h = html(CLOSED_RUN);
    expect(h).not.toContain('volume-steps-sub');
    expect(h).not.toContain('бэк');
    expect(kvPairs(h)[0]).toEqual(['Шагов', '78']);
  });

  it('линии с русскими значениями — те же слова, что у laneWord (существующая нормализация)', () => {
    const pairs = kvPairs(html({ ...CLOSED_RUN, lanes: [
      { lane: 'бэк', personaIds: [], total: 3, accepted: 2, returns: 0 },
      { lane: 'фронт', personaIds: [], total: 1, accepted: 1, returns: 0 },
    ] }));
    expect(pairs[0]).toEqual(['Шагов', '78\nбэк 3 · фронт 1']);
  });

  it('«Задач всего», «Пачек ревью», «Хвостов после мержа», медианы, смены знаменателя — сняты полностью', () => {
    const h = html(CLOSED_FULL);
    for (const s of ['Задач всего', 'Пачек ревью', 'Хвостов', 'Медиана', 'Смены знаменателя']) {
      expect(h).not.toContain(s);
    }
  });

  it('закрытая без summary/merges/tails (старый ответ): итогов по summary нет, счётчики — 0', () => {
    const pairs = kvPairs(html(mkRun({ state: 'closed' })));
    expect(pairs).toEqual([
      ['Слияний', '0'],
      ['Доработок после мержа', '0'],
    ]);
  });

  it('открытая — строк итогов нет (0 строк)', () => {
    const h = html(mkRun({ reviews: [{ id: 'r-1', title: 'ревью' }] }));
    expect(h).not.toContain('Слияний');
    expect(h).not.toContain('Доработок после мержа');
    expect(kvPairs(h).length).toBe(0);
  });

  it('признак закрытости — closed или state merged/closed', () => {
    const byState = html(mkRun({ state: 'merged' }));
    expect(byState).toContain('Слияний');
    expect(byState).toContain('Доработок после мержа');
    // closed:false при state running — не закрытая.
    expect(html(mkRun({ closed: false }))).not.toContain('Слияний');
  });

  it('closed: true при незакрытом state — тоже закрытая: итоги есть', () => {
    // Отдельный случай на `closed === true` в признаке (прежний тест проверял только
    // state merged/closed: ветка closed === true оставалась непроверенной).
    const pairs = kvPairs(html(mkRun({ closed: true })));
    expect(pairs).toEqual([
      ['Слияний', '0'],
      ['Доработок после мержа', '0'],
    ]);
  });
});

describe('карточку «Объём» не повторяю', () => {
  it('в html нет числа «N из M» и полосы корзин FeatureVolumeCard', () => {
    const h = html(C1_RUN);
    expect(h).not.toContain('30 из 44');
    expect(h).not.toContain('volume-card');
    expect(h).not.toContain('volume-seg');
  });
});
