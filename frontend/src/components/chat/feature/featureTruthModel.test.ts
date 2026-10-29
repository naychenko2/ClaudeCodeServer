// Модель «правды о прогрессе» для трёх поверхностей чата (F2): полоса трекера,
// слова тревоги, статус шага, цепочка шага, оценка, «Сейчас», расхождения трекера.
// Окружение node (без DOM): чистые функции, стабильный nowMs.
//
// Что защищается в первую очередь (постановка F2): состояния идёт/ждёт вас/стоит,
// «нет мержей / есть мержи» (подсветка «в master»), «фича без трекера» (полосы и
// процента нет), отсутствие оценки (срока нет), ортогональность inMaster (в процент не
// входит, «принято» не уменьшается, сумма сегментов == total, для рисования — не больше
// принятой части).

import { describe, expect, it } from 'vitest';
import {
  alarmView, chatRoleFromRun, featureEstimateView, featureNowRows, featureStepChainRows,
  featureTrackerIssues, featureTruthBar, findChatChain, stepStatusParts, stepStatusText, trackerPercent,
} from './featureTruthModel';
import { trackerBarAria } from './FeatureTrackerBar';
import { trackerPercent as tp } from '../../../lib/featureProgress';
import { formatClock } from '../../feature/FeatureStatsBits';
import type { FeatureRun, FeatureStepChain } from '../../../types/featureRun';
import type { StepProgress } from '../../../lib/featureProgress';

const NOW = Date.parse('2026-10-29T12:00:00Z');

function sp(over: Partial<StepProgress> = {}): StepProgress {
  return { planState: 'cut', total: 9, accepted: 5, review: 1, work: 2, notCreated: 1, withdrawn: 0, unlinkedReworks: 0, ...over };
}

function chain(over: Partial<FeatureStepChain> = {}): FeatureStepChain {
  return {
    stepRef: 'Ш8', title: 'Вёрстка шапки', lane: 'front', personaId: 'p-kira',
    taskIds: ['t-8'], outcome: 'running', status: 'идёт', durationMinutes: 24,
    rounds: [], loops: 0, chatId: null, ...over,
  } as FeatureStepChain;
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'epic-1', state: 'running',
    epic: { id: 'epic-1', title: 'Правда о прогрессе', status: 'todo' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

const nameOf = (id: string) => ({ 'p-kira': 'Кира', 'p-gleb': 'Глеб' }[id] ?? null);

describe('featureTruthBar: полосы и процент', () => {
  it('корзины — бэковые числа без пересчёта: сумма сегментов == total; «в master» — не корзина', () => {
    const bar = featureTruthBar(sp(), 3)!;
    expect(bar.percent).toBe(56); // 5 из 9 — 55.6 → 56
    expect(bar.segments.map(s => s.key)).toEqual(['accepted', 'review', 'work', 'notCreated']);
    const counts = bar.segments.map(s => s.count);
    expect(counts).toEqual([5, 1, 2, 1]); // accepted не уменьшается из-за inMaster
    expect(counts.reduce((a, c) => a + c, 0)).toBe(9); // сумма == total
    // «в master» — подсветка принятой части: реальное число и длина для рисования.
    expect(bar.inMaster).toBe(3);
    expect(bar.masterDraw).toBe(3);
  });

  it('нет мержей: inMaster 0, подсветки нет, accepted полной', () => {
    const bar = featureTruthBar(sp(), undefined)!;
    expect(bar.segments.map(s => s.key)).toEqual(['accepted', 'review', 'work', 'notCreated']);
    expect(bar.segments[0].count).toBe(5);
    expect(bar.inMaster).toBe(0);
    expect(bar.masterDraw).toBe(0);
    expect(bar.percent).toBe(56);
  });

  it('inMaster > accepted (шаг сдан в master без витка принятия): числа бэковые, «принято» не вычитается', () => {
    // Мутация «убери min(inMaster, accepted) в masterDraw» краснеет: подсветка вышла бы
    // за принятую часть (4 при accepted 2). Мутация «accepted - master» (старый вычет) —
    // краснеет по сумме сегментов и по «принято».
    const bar = featureTruthBar(sp({ accepted: 2, review: 4, work: 2, notCreated: 1, total: 9 }), 4)!;
    const counts = bar.segments.map(s => s.count);
    expect(bar.segments.map(s => s.key)).toEqual(['accepted', 'review', 'work', 'notCreated']);
    expect(counts).toEqual([2, 4, 2, 1]); // «принято» = 2, не 0
    expect(counts.reduce((a, c) => a + c, 0)).toBe(9); // сумма сегментов == total
    expect(bar.inMaster).toBe(4); // реальное число бэка
    expect(bar.masterDraw).toBe(2); // для рисования — не больше принятой части
    expect(bar.percent).toBe(22); // 2 из 9: inMaster в числитель и знаменатель не входит
    // Текст называет факт как есть: 4 в master, 2 принято.
    expect(trackerBarAria(bar)).toContain('4 в master');
    expect(trackerBarAria(bar)).toContain('2 принято');
  });

  it('фича без трекера: stepProgress null → бара нет (ни процента, ни полос)', () => {
    expect(featureTruthBar(null, null)).toBeNull();
  });

  it('нулевые корзины не попадают в сегменты; пустой знаменатель — процент 0', () => {
    const bar = featureTruthBar(sp({ total: 0, accepted: 0, review: 0, work: 0, notCreated: 0 }), 0)!;
    expect(bar.segments).toHaveLength(0);
    expect(bar.percent).toBe(0);
  });

  it('trackerPercent: одна арифметика (accepted/total), битые знаменатели — 0', () => {
    expect(tp(5, 9)).toBe(56);
    expect(tp(0, 0)).toBe(0);
    expect(tp(-1, 5)).toBe(0);
    expect(tp(5, 0)).toBe(0);
  });
});

describe('alarmView: одни слова для строки В2 и попапа П2', () => {
  it('waiting десктоп — «Ждёт вас · {причина}», действие «Ответить {Имя}» (warn)', () => {
    const a = alarmView('waiting', 'Полина спрашивает про отступы', 'Полина', false);
    expect(a.tone).toBe('warn');
    expect(a.text).toBe('Ждёт вас · Полина спрашивает про отступы');
    expect(a.actionLabel).toBe('Ответить Полина');
  });

  it('waiting мобила — «Ждёт вас · {Имя}» (причина на 360 не помещается), действие «Ответить»', () => {
    const a = alarmView('waiting', 'Полина спрашивает про отступы', 'Полина', true);
    expect(a.text).toBe('Ждёт вас · Полина');
    expect(a.actionLabel).toBe('Ответить');
  });

  it('merge_pending — «Ждёт согласия на мерж», действие «К мержу»', () => {
    const a = alarmView('merge_pending', null, null, false);
    expect(a.tone).toBe('warn');
    expect(a.text).toBe('Ждёт согласия на мерж');
    expect(a.actionLabel).toBe('К мержу');
  });

  it('stopped — «Стоит · {Имя}», действие «Чат {Имя}» / на мобиле «Чат» (danger)', () => {
    expect(alarmView('stopped', null, 'Тимур', false)).toEqual({ tone: 'stop', text: 'Стоит · Тимур', actionLabel: 'Чат Тимур' });
    expect(alarmView('stopped', null, 'Тимур', true).actionLabel).toBe('Чат');
  });
});

describe('шаг этого чата: цепочка, статус, роль', () => {
  it('findChatChain: цепочка находится по задаче-владельцу чата; без задачи/не в цепочке — null', () => {
    const r = run({ stepChains: [chain()] });
    expect(findChatChain(r, 't-8')?.stepRef).toBe('Ш8');
    expect(findChatChain(r, null)).toBeNull();
    expect(findChatChain(r, 't-другая')).toBeNull();
    expect(findChatChain(run(), 't-8')).toBeNull();
  });

  it('статус идущего: «идёт · 24 мин» (длительность бэка)', () => {
    expect(stepStatusText(chain(), NOW)).toBe('идёт · 24 мин');
  });

  it('статус ожидания: «ждёт ревью · 8 мин» по waitingSince', () => {
    const c = chain({ outcome: 'pending', status: 'ждёт ревью', waitingSince: '2026-10-29T11:52:00Z', durationMinutes: 0 });
    expect(stepStatusText(c, NOW)).toBe('ждёт ревью · 8 мин');
  });

  it('принятый: «принят 13:40» (без разделителя — один факт)', () => {
    const c = chain({ outcome: 'accepted', status: 'принят', acceptedAt: '2026-10-29T13:40:00Z' });
    expect(stepStatusText(c, NOW)).toBe(`принят ${formatClock('2026-10-29T13:40:00Z')}`);
  });

  it('parts — те же части для пилюли (word/sep/time)', () => {
    const p = stepStatusParts(chain(), NOW);
    expect(p).toEqual({ word: 'идёт', time: '24 мин', sep: ' · ' });
  });

  it('цепочка П2: свой шаг → витки (кто, исход, когда) → незакрытое ревью, ждущее этот шаг; закрытое ревью — нет', () => {
    const r = run({
      stepChains: [chain({
        rounds: [
          { n: 1, verdict: 'returned', reviewTaskId: 'rv-1', personaId: 'p-gleb', startedAt: null, completedAt: '2026-10-29T11:00:00Z', reason: null, reviewChatId: null },
        ],
      })],
      reviews: [
        { id: 'rv-2', title: 'Ревью шапки', role: 'review', status: 'todo', reviewOf: ['t-8'], personaId: 'p-gleb' },
        { id: 'rv-3', title: 'Старое ревью', role: 'review', status: 'done', reviewOf: ['t-8'], personaId: 'p-gleb' },
        { id: 'rv-4', title: 'Чужое ревью', role: 'review', status: 'todo', reviewOf: ['t-9'], personaId: null },
      ],
    });
    const rows = featureStepChainRows(r, r.stepChains![0], nameOf);
    expect(rows[0].text).toBe('Ш8 · Вёрстка шапки — идёт');
    expect(rows[1].text).toBe('виток 1 — Глеб, вернул');
    expect(rows[1].time).toBe(formatClock('2026-10-29T11:00:00Z'));
    const waitingRow = rows.find(x => x.id === 'rv-2')!;
    expect(waitingRow.text).toBe('ревью — Глеб, после сдачи');
    expect(rows.some(x => x.id === 'rv-3')).toBe(false); // закрытое не «ждёт»
    expect(rows.some(x => x.id === 'rv-4')).toBe(false); // чужой шаг
  });

  it('chatRoleFromRun: роль по задаче чата (roleLabel); чужой id — пустая строка', () => {
    const r = run({ steps: [{ id: 't-8', title: 'Ш8', role: 'step', stepRef: 'Ш8', lane: 'front', status: 'inProgress' }] });
    expect(chatRoleFromRun(r, 't-8')).toBe('Ш8 · фронт');
    expect(chatRoleFromRun(r, 'нет')).toBe('');
    expect(chatRoleFromRun(r, null)).toBe('');
  });
});

describe('оценка и «Сейчас»', () => {
  it('оценка с percent и eta: «≈ 18:30» в строку, «≈ 2 ч 40 · к 18:30» в плитку', () => {
    const r = run({
      epic: {
        id: 'epic-1', title: 'Фича', status: 'todo',
        note: { text: 'идём по плану', at: '2026-10-29T12:00:00Z', byUser: false, stageAtWrite: 'prod', stateAtWrite: 'running', estimate: { done: 4, total: 9, inProgress: 3, percent: 60, etaMinutes: 160 } },
      },
    });
    const e = featureEstimateView(r, NOW)!;
    expect(e.percent).toBe(60);
    expect(e.etaMain).toBe('≈ 2 ч 40 мин');
    expect(e.readyAt).toBe(`к ${formatClock('2026-10-29T14:40:00Z')}`);
    expect(e.rowEta).toBe(`≈ ${formatClock('2026-10-29T14:40:00Z')}`);
    expect(e.sourceLabel).toBe('оценка оркестратора');
    expect(e.ago).toBe('только что');
  });

  it('оценка без percent — доля done/total; голос человека помечен как человека', () => {
    const r = run({
      epic: {
        id: 'epic-1', title: 'Фича', status: 'todo',
        note: { text: 'ручной голос', at: '2026-10-29T11:00:00Z', byUser: true, stageAtWrite: 'prod', stateAtWrite: 'running', estimate: { done: 3, total: 9, inProgress: 1, percent: null } },
      },
    });
    const e = featureEstimateView(r, NOW)!;
    expect(e.percent).toBe(33);
    expect(e.etaMain).toBeNull(); // eta не оценивался — срока нет
    expect(e.sourceLabel).toBe('оценка человека');
  });

  it('нет голоса/оценки — view null (строка без срока)', () => {
    expect(featureEstimateView(run(), NOW)).toBeNull();
  });

  it('«Сейчас»: строки из run.now (имя, «{stepRef} · задача», длительность, метка «этот чат»)', () => {
    const r = run({
      now: [
        { taskId: 't-8', stage: 'prod', personaId: 'p-kira', stepRef: 'Ш8', title: 'Вёрстка шапки', startedAt: '2026-10-29T11:36:00Z', linkedSessionId: 'sess-me' },
        { taskId: 't-9', stage: 'review', personaId: 'p-gleb', stepRef: null, title: 'Ревью шапки', startedAt: null, linkedSessionId: 'sess-other' },
      ],
    });
    const rows = featureNowRows(r, 'sess-me', nameOf, NOW);
    expect(rows[0]).toEqual({ taskId: 't-8', name: 'Кира', what: 'Ш8 · Вёрстка шапки', time: '24 мин', isThisChat: true });
    expect(rows[1].what).toBe('Ревью шапки'); // без stepRef — заголовок задачи
    expect(rows[1].time).toBeNull(); // нет старта — нет времени
    expect(rows[1].isThisChat).toBe(false);
  });
});

describe('расхождения трекера: перевод видов FeatureTracker', () => {
  it('известные виды — русские строки с шагом; неизвестный (более новый бэк) — слово как есть', () => {
    const r = run({
      tracker: [
        { kind: 'unreviewed', taskId: 't-5', stepRef: 'Ш5', since: null },
        { kind: 'epic_open_after_merge', taskId: 'm-1', stepRef: null, since: null },
        { kind: 'future_kind', taskId: 'x', stepRef: 'Ш7', since: null },
      ],
    });
    const issues = featureTrackerIssues(r);
    expect(issues[0].text).toBe('сдан без ревью — шаг Ш5');
    expect(issues[1].text).toBe('мерж закрыт, эпик открыт');
    expect(issues[2].text).toBe('future_kind — шаг Ш7');
  });

  it('поля нет (старые ответы) — пустой список, не падение', () => {
    expect(featureTrackerIssues(run())).toEqual([]);
  });
});
