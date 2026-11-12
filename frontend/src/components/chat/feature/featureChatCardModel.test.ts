// FS-FCm: чистая модель карточки фичи в шапке чата (без DOM). Состояния и инварианты —
// постановка шага + макет v5 §3. Мок FeatureIndexFeature/FeatureRun — как в FeatureBanner.test.

import { describe, expect, it, vi } from 'vitest';
import { chatCardStateWord, featureChatCardModel } from './featureChatCardModel';
import { featureEstimateView } from './featureTruthModel';
// Инвариант FS-FCb: подпись карточки — тот же словарь, что у плашки страницы (FeatureAskBlock).
import { ASK_LABEL } from '../../feature/FeatureAskBlock';
import type { FeatureIndexFeature } from '../../../types/featureIndex';
import type { FeatureRun } from '../../../types/featureRun';

const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>([['p-kira', 'Кира'], ['p-gleb', 'Глеб'], ['p-alex', 'Александр']]) }));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Правда о прогрессе', projectId: 'p1',
    bucket: 'running', state: 'running', attention: null,
    rail: [], chips: ['open'],
    stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  } as FeatureIndexFeature;
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1', state: 'running',
    epic: { id: 'f1', title: 'Фича', status: 'todo' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

const sp = (accepted: number, total: number) => ({
  planState: 'cut' as const, total, accepted, review: 0, work: 0, notCreated: 0, withdrawn: 0, unlinkedReworks: 0,
});
// nowMs фиксирован: дата мержа 12:00Z — тот же календарный день в любой TZ теста (как в FeatureClosedSummary.test).
const NOW = Date.parse('2026-11-10T12:00:00Z');

describe('featureChatCardModel: состояния карточки (макет v5 §3)', () => {
  it('идёт: «Сейчас Кира — Ш8 · Вёрстка шапки +1», тег «идёт», глиф info, действия нет', () => {
    const r = run({
      stepProgress: sp(6, 44),
      now: [
        { taskId: 't-8', stage: 'prod', personaId: 'p-kira', stepRef: 'Ш8', title: 'Вёрстка шапки', startedAt: null, linkedSessionId: null },
        { taskId: 't-9', stage: 'prod', personaId: 'p-gleb', stepRef: 'Ш9', title: 'E2E', startedAt: null, linkedSessionId: null },
      ],
    });
    const m = featureChatCardModel(feat(), r, 's-me', false, NOW);
    expect(m.title).toBe('Правда о прогрессе');
    expect(m.glyph).toBe('info');
    expect(m.tag?.label).toBe('идёт');
    expect(m.count).toBe('6 из 44');
    expect(m.eta).toBeNull(); // оценки нет — срока нет
    expect(m.phrase).toEqual({ label: null, text: 'Сейчас Кира — Ш8 · Вёрстка шапки +1', tone: 'muted', name: 'Кира' });
    expect(m.action).toBeNull();
    expect(m.showTracker).toBe(true);
    expect(m.isKeyChat).toBe(false);
  });

  it('ждёт тебя: фраза warn с причиной, глиф warning, действие → чат тревоги', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'Полина спрашивает про отступы', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.glyph).toBe('warning');
    expect(m.tag?.label).toBe('в работе'); // тот же тег, что шапка страницы (featurePageStatusTag)
    expect(m.phrase).toEqual({ label: null, text: 'Ждёт вас · Полина спрашивает про отступы', tone: 'warn', name: null });
    expect(m.action).toEqual({ label: 'Ответить', sessionId: 's-alex', toMerge: false });
  });

  it('стоит: фраза danger, глиф danger, тег «стоит», действие «Открыть чат»', () => {
    const f = feat({
      state: 'stopped',
      attention: { kind: 'stopped', since: null, reason: null, personaId: 'p-gleb', sessionId: 's-gleb', taskId: 't-2' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.glyph).toBe('danger');
    expect(m.tag?.label).toBe('стоит');
    expect(m.phrase.tone).toBe('danger');
    expect(m.phrase.text).toBe('Стоит · Глеб');
    expect(m.action).toEqual({ label: 'Открыть чат', sessionId: 's-gleb', toMerge: false });
  });

  it('закрыта: «12 шагов · 15 ч 13 мин», «Смержена 19 октября», полосы и действия нет', () => {
    const r = run({
      state: 'merged',
      summary: { mergedAt: '2026-10-19T12:00:00Z', durationMinutes: 913, tasks: 14, steps: 12, loops: 1 },
    });
    const m = featureChatCardModel(feat({ state: 'merged', bucket: 'closed' }), r, 's-me', false, NOW);
    expect(m.glyph).toBe('success');
    expect(m.tag?.label).toBe('смержена');
    expect(m.count).toBe('12 шагов');
    expect(m.eta).toBe('15 ч 13 мин');
    expect(m.phrase).toEqual({ label: null, text: 'Смержена 19 октября', tone: 'muted', name: null });
    expect(m.action).toBeNull();
    expect(m.showTracker).toBe(false);
  });

  it('закрыта без итога (старый ответ): слово состояния вместо выдуманной даты, счёт по корзинам', () => {
    const m = featureChatCardModel(feat({ state: 'closed', bucket: 'closed', stepsAccepted: 12, stepsTotal: 12, planState: 'done' }), null, 's-me', false, NOW);
    expect(m.phrase.text).toBe('закрыта без мержа');
    expect(m.count).toBe('12 из 12');
    expect(m.eta).toBeNull();
    expect(m.showTracker).toBe(false);
  });

  it('run=null: счёт из строки индекса, eta=null, «Сейчас {Имя}» без шага (честная деградация)', () => {
    const f = feat({ stepsAccepted: 6, stepsTotal: 44, stepsReview: 2, stepsWork: 3, stepsNotCreated: 33, planState: 'cut', nowPersonaIds: ['p-kira'] });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.count).toBe('6 из 44');
    expect(m.eta).toBeNull();
    expect(m.phrase).toEqual({ label: null, text: 'Сейчас Кира', tone: 'muted', name: 'Кира' });
  });

  it('оценка есть: eta — срок общего источника rowEta (featureEstimateView), не своя арифметика', () => {
    const r = run({
      stepProgress: sp(6, 44),
      epic: { id: 'f1', title: 'Фича', status: 'todo', note: { text: 'голос', at: '2026-11-10T10:00:00Z', byUser: false, stageAtWrite: 'prod', stateAtWrite: 'running', estimate: { etaMinutes: 160 } } },
    });
    const m = featureChatCardModel(feat(), r, 's-me', false, NOW);
    // rowEta — часы (в UTC «≈ 12:40»), не etaMain («≈ 2 ч 40 мин»): сверяем с общим источником, TZ-независимо.
    expect(m.eta).not.toBeNull();
    expect(m.eta).toBe(featureEstimateView(r, NOW)?.rowEta);
  });

  it('никто не идёт: слово состояния (очередь), tone muted', () => {
    const f = feat({ state: 'paused', holdReason: 'queue', queue: { position: 2, length: 3 } });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.phrase).toEqual({ label: null, text: 'В очереди · 2 из 3', tone: 'muted', name: null });
    expect(m.tag?.label).toBe('в очереди · 2 из 3'); // общий featurePageStatusTag с hold
  });
});

describe('featureChatCardModel: инвариант §3 и переходы', () => {
  it('ждут В ЭТОМ чате: «Ответить здесь» (тот же sessionId, фокус поля — дело компонента)', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-kira', sessionId: 's-me', taskId: 't-1' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.action).toEqual({ label: 'Ответить здесь', sessionId: 's-me', toMerge: false });
  });

  it('стоит В ЭТОМ чате: действия нет вовсе (в чат стоящего кнопки перехода нет)', () => {
    const f = feat({
      state: 'stopped',
      attention: { kind: 'stopped', since: null, reason: 'auth_failed', personaId: 'p-gleb', sessionId: 's-me', taskId: 't-2' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.action).toBeNull();
    // фраза при этом остаётся — исчезает кнопка, не тревога
    expect(m.phrase.tone).toBe('danger');
    expect(m.phrase.text).toBe('Стоит · Глеб');
  });

  it('merge_pending: «К мержу» ведёт на страницу (toMerge), остаётся и в своём чате', () => {
    const f = feat({
      state: 'merge_pending',
      attention: { kind: 'merge', since: null, reason: null, personaId: null, sessionId: 's-me', taskId: 't-3' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.action).toEqual({ label: 'К мержу', sessionId: 's-me', toMerge: true });
  });

  it('тревога без чата (sessionId=null): действие есть, цель null (маршрут — за модели)', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-kira', sessionId: null, taskId: 't-1' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.action).toEqual({ label: 'Ответить', sessionId: null, toMerge: false });
  });

  it('ключевой чат: isKeyChat=true только в своём; keySessionId проходит всегда', () => {
    const f = feat({ keySessionId: 's-key' });
    expect(featureChatCardModel(f, null, 's-key', false, NOW).isKeyChat).toBe(true);
    const other = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(other.isKeyChat).toBe(false);
    expect(other.keySessionId).toBe('s-key');
  });
});

describe('featureChatCardModel: мобила (360)', () => {
  it('тега нет; «Сейчас {Имя} +N» без шага', () => {
    const r = run({
      stepProgress: sp(6, 44),
      now: [
        { taskId: 't-8', stage: 'prod', personaId: 'p-kira', stepRef: 'Ш8', title: 'Вёрстка шапки', startedAt: null, linkedSessionId: null },
        { taskId: 't-9', stage: 'prod', personaId: 'p-gleb', stepRef: 'Ш9', title: 'E2E', startedAt: null, linkedSessionId: null },
      ],
    });
    const m = featureChatCardModel(feat(), r, 's-me', true, NOW);
    expect(m.tag).toBeNull();
    expect(m.phrase.text).toBe('Сейчас Кира +1');
  });

  it('тревога на мобиле: короткая фраза с именем (alarmView), «Ответить» без «здесь» для чужого чата', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
    });
    const m = featureChatCardModel(f, null, 's-me', true, NOW);
    expect(m.tag).toBeNull();
    expect(m.phrase.text).toBe('Ждёт вас · Кира');
    expect(m.action).toEqual({ label: 'Ответить', sessionId: 's-alex', toMerge: false });
  });
});

describe('featureChatCardModel: вопрос «Нужно от тебя / Что мешает» (FS-FCb, макет §6 «Б»)', () => {
  it('ask wait: подпись ASK_LABEL.need, текст вопроса как есть, имя по personaId, тон warn', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
      ask: { text: 'Согласен, что отступ 8?', source: 'wait', personaId: 'p-kira' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.phrase).toEqual({ label: ASK_LABEL.need, text: 'Согласен, что отступ 8?', tone: 'warn', name: 'Кира' });
  });

  it('ask stopped: ASK_LABEL.blocker, тон danger — ask важнее слов alarmView', () => {
    const f = feat({
      state: 'stopped',
      attention: { kind: 'stopped', since: null, reason: 'auth_failed', personaId: 'p-gleb', sessionId: 's-gleb', taskId: 't-2' },
      ask: { text: 'Локальный движок недоступен', source: 'stopped', personaId: 'p-gleb' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.phrase).toEqual({ label: ASK_LABEL.blocker, text: 'Локальный движок недоступен', tone: 'danger', name: 'Глеб' });
  });

  it('ask merge: та же подпись «Нужно от тебя» (согласие на мерж — не «Что мешает»)', () => {
    const f = feat({
      state: 'merge_pending',
      attention: { kind: 'merge', since: null, reason: null, personaId: null, sessionId: 's-me', taskId: 't-3' },
      ask: { text: 'Подтвердишь мерж?', source: 'merge' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.phrase).toEqual({ label: ASK_LABEL.need, text: 'Подтвердишь мерж?', tone: 'warn', name: null });
  });

  it('run пришёл, индекс ещё старый без ask: фраза по alarmView, подписи нет', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
    });
    const m = featureChatCardModel(f, run({ state: 'waiting' }), 's-me', false, NOW);
    expect(m.phrase).toEqual({ label: null, text: 'Ждёт вас · про отступы', tone: 'warn', name: null });
  });

  it('мобила с ask: подписи и вопроса нет — короткая фраза FS-FCm', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
      ask: { text: 'Согласен, что отступ 8?', source: 'wait', personaId: 'p-kira' },
    });
    const m = featureChatCardModel(f, null, 's-me', true, NOW);
    expect(m.phrase).toEqual({ label: null, text: 'Ждёт вас · Кира', tone: 'warn', name: null });
  });
});

describe('featureChatCardModel: «часы стоят» и «обещали ≈ N» (FS-FCb, макет §3/§6 «Б»)', () => {
  it('eta.clockStopped: «часы стоят» и etaPaused=true (цвет — дело компонента)', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
      eta: { minutes: 120, source: 'note', clockStopped: 'wait' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.eta).toBe('часы стоят');
    expect(m.etaPaused).toBe(true);
  });

  it('clockStopped нет: срок как в FS-FCm (rowEta общего источника), etaPaused=false', () => {
    const r = run({
      stepProgress: sp(6, 44),
      epic: { id: 'f1', title: 'Фича', status: 'todo', note: { text: 'голос', at: '2026-11-10T10:00:00Z', byUser: false, stageAtWrite: 'prod', stateAtWrite: 'running', estimate: { etaMinutes: 160 } } },
    });
    const m = featureChatCardModel(feat({ eta: { minutes: 120, source: 'note' } }), r, 's-me', false, NOW);
    expect(m.eta).toBe(featureEstimateView(r, NOW)?.rowEta);
    expect(m.etaPaused).toBe(false);
  });

  it('закрытая с первой оценкой: «Смержена 19 октября · обещали ≈ 10 ч»', () => {
    const r = run({
      state: 'merged',
      summary: { mergedAt: '2026-10-19T12:00:00Z', durationMinutes: 913, tasks: 14, steps: 12, loops: 1 },
      epic: { id: 'f1', title: 'Фича', status: 'done', initialEtaMinutes: 600 },
    });
    const m = featureChatCardModel(feat({ state: 'merged', bucket: 'closed' }), r, 's-me', false, NOW);
    expect(m.phrase.text).toBe('Смержена 19 октября · обещали ≈ 10 ч');
  });

  it('без initialEtaMinutes (старые задачи): хвоста нет — та же фраза FS-FCm', () => {
    const r = run({
      state: 'merged',
      summary: { mergedAt: '2026-10-19T12:00:00Z', durationMinutes: 913, tasks: 14, steps: 12, loops: 1 },
    });
    const m = featureChatCardModel(feat({ state: 'merged', bucket: 'closed' }), r, 's-me', false, NOW);
    expect(m.phrase.text).toBe('Смержена 19 октября');
  });

  it('оценка есть, даты мержа нет: хвоста нет — слово состояния, не выдуманный мерж', () => {
    const r = run({ state: 'closed', epic: { id: 'f1', title: 'Фича', status: 'done', initialEtaMinutes: 600 } });
    const m = featureChatCardModel(feat({ state: 'closed', bucket: 'closed', stepsAccepted: 12, stepsTotal: 12, planState: 'done' }), r, 's-me', false, NOW);
    expect(m.phrase.text).toBe('закрыта без мержа');
  });
});

// FP-C1 (макет chat-feature-fit-v1 §5 «Слово состояния»): слово для строки-подзаголовка шапки.
// Признак — model.glyph, не разбор текста фразы; слова тревоги — «ждёт тебя»/«стоит» (с именем
// — через двоеточие, без имени — без); «идёт»/«смержена»/«закрыта» — короткие слова тега
// (shortRunTagLabel, общий словарь узкой шапки).
describe('chatCardStateWord: слово состояния для строки FP-C1', () => {
  it('state word: wait → «ждёт тебя: Александр», tone warn', () => {
    const f = feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-alex', sessionId: 's-alex', taskId: 't-1' },
      ask: { text: 'согласен, что «ещё N» можно убрать?', source: 'wait', personaId: 'p-alex' },
    });
    const m = featureChatCardModel(f, null, 's-me', false, NOW);
    expect(m.glyph).toBe('warning'); // признак состояния — глиф модели
    expect(chatCardStateWord(m)).toEqual({ text: 'ждёт тебя: Александр', tone: 'warn' });
  });

  it('state word: без имени — «ждёт тебя» и «стоит», без двоеточия', () => {
    const wait = featureChatCardModel(feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: null, sessionId: 's-alex', taskId: 't-1' },
    }), null, 's-me', false, NOW);
    expect(chatCardStateWord(wait)).toEqual({ text: 'ждёт тебя', tone: 'warn' });
    const stop = featureChatCardModel(feat({
      state: 'stopped',
      attention: { kind: 'stopped', since: null, reason: null, personaId: 'p-gleb', sessionId: 's-gleb', taskId: 't-2' },
    }), null, 's-me', false, NOW);
    // без ask имя в текст фразы встроен (phrase.name=null) — слово без имени
    expect(stop.phrase.name).toBeNull();
    expect(chatCardStateWord(stop)).toEqual({ text: 'стоит', tone: 'danger' });
  });

  it('state word: stop с именем (ask stopped) → «стоит: Глеб», tone danger', () => {
    const m = featureChatCardModel(feat({
      state: 'stopped',
      attention: { kind: 'stopped', since: null, reason: 'auth_failed', personaId: 'p-gleb', sessionId: 's-gleb', taskId: 't-2' },
      ask: { text: 'Локальный движок недоступен', source: 'stopped', personaId: 'p-gleb' },
    }), null, 's-me', false, NOW);
    expect(chatCardStateWord(m)).toEqual({ text: 'стоит: Глеб', tone: 'danger' });
  });

  it('state word: running → короткое слово тега «идёт», tone muted; очередь — «в очереди»', () => {
    const running = featureChatCardModel(feat(), null, 's-me', false, NOW);
    expect(running.glyph).toBe('info');
    expect(chatCardStateWord(running)).toEqual({ text: 'идёт', tone: 'muted' });
    const queue = featureChatCardModel(feat({ state: 'paused', holdReason: 'queue', queue: { position: 2, length: 3 } }), null, 's-me', false, NOW);
    expect(chatCardStateWord(queue)).toEqual({ text: 'в очереди', tone: 'muted' });
  });

  it('state word: merged → «смержена», closed → «закрыта», tone success', () => {
    const merged = featureChatCardModel(feat({ state: 'merged', bucket: 'closed' }), null, 's-me', false, NOW);
    expect(merged.glyph).toBe('success');
    expect(chatCardStateWord(merged)).toEqual({ text: 'смержена', tone: 'success' });
    const closed = featureChatCardModel(feat({ state: 'closed', bucket: 'closed' }), null, 's-me', false, NOW);
    expect(chatCardStateWord(closed)).toEqual({ text: 'закрыта', tone: 'success' });
  });

  it('мобильная модель (тега нет): тревога говорит «ждёт тебя», у info/success слова нет (360 строка — счёт/срок)', () => {
    const wait = featureChatCardModel(feat({
      state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: null, sessionId: 's-alex', taskId: 't-1' },
    }), null, 's-me', true, NOW);
    expect(chatCardStateWord(wait)).toEqual({ text: 'ждёт тебя', tone: 'warn' });
    expect(chatCardStateWord(featureChatCardModel(feat(), null, 's-me', true, NOW))).toEqual({ text: '', tone: 'muted' });
  });
});
