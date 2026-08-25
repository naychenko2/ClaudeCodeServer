// Группировка хода координатора чата-штаба в карточку ленты. Главное, что здесь
// проверяется, — жёсткий инвариант: карточка НЕ поглощает элементы, требующие внимания
// человека (эскалация, план, вопрос, разрешение, ревью плана, ошибка). Утонувший в
// свёрнутом блоке вопрос означал бы молча вставшие авто-волны.
import { describe, it, expect } from 'vitest';
import type { ChatItem, SessionTeamImplement } from '../../types';
import {
  buildCoordinatorTurns, coordinatorCollapsedSummary, coordinatorPhaseFromNote,
  coordinatorPhaseFromTools, coordinatorStatusLine, isCoordinatorTrigger,
  COORDINATOR_PHASE_ASSIGN, COORDINATOR_PHASE_CARD, COORDINATOR_PHASE_INTERVIEW,
  COORDINATOR_PHASE_REISSUE, COORDINATOR_PHASE_REPORT,
  STAFF_NOTE_BACK_TO_INTERVIEW, STAFF_NOTE_CARD_ANSWER, STAFF_NOTE_TASK_REPORT,
  type CoordinatorTurnGroup,
} from '../coordinatorTurns';

const team: SessionTeamImplement = {
  stage: 'wave', waveNumber: 2, plannedWaves: 3, autoWaves: true, stopped: false, planVersion: 1,
  coordinatorPersonaId: 'p-coord', executorPersonaIds: [], coordinatorNoCode: true,
  budget: {
    maxTasks: 20, tasksUsed: 4, maxWaves: 4, wavesUsed: 2, maxRuns: 30, runsUsed: 6,
    maxRetries: 1, retriesUsed: 0, maxWakeups: 3, wakeupsUsed: 0,
  },
};

const note = (text: string): ChatItem => ({ kind: 'user_message', text: 'служебный ход', staffNote: text });
const human = (text = 'сделай ещё вот это'): ChatItem => ({ kind: 'user_message', text });
const think = (text = 'думаю'): ChatItem => ({ kind: 'thinking', text, expanded: false });
const tool = (name: string, id = name, input: unknown = {}): ChatItem =>
  ({ kind: 'tool_use', id, name, input, result: 'ok' });
const text = (t = 'сводка волны'): ChatItem => ({ kind: 'text', text: t });
const result = (durationMs = 12000, subtype = 'success'): ChatItem =>
  ({ kind: 'result', subtype, durationMs, numTurns: 1 });

const WAVE_NOTE = 'Волна 2 закрыта — сводка передана координатору';

function only(groups: Map<number, CoordinatorTurnGroup>): CoordinatorTurnGroup {
  expect(groups.size).toBe(1);
  return [...groups.values()][0];
}

describe('границы хода координатора', () => {
  it('служебный ход собирается в одну группу с якорем на плашке штаба', () => {
    const items = [note(WAVE_NOTE), think(), tool('Read'), text(), result()];
    const { at, suppressed } = buildCoordinatorTurns(items, team);
    const g = only(at);
    expect(g.startIdx).toBe(0);
    expect(g.endIdx).toBe(4);
    // Лента не схлопывается: гасятся все индексы группы, кроме якоря
    expect([...suppressed].sort()).toEqual([1, 2, 3, 4]);
    expect(g.activity.map(e => e.idx)).toEqual([1, 2]);
    expect(g.answerIdx).toBe(3);
    expect(g.metrics).toEqual({ tools: 1, durationMs: 12000, tokens: undefined });
    expect(g.running).toBe(false);
    expect(g.isError).toBe(false);
  });

  it('ход, начатый живым сообщением человека, не группируется', () => {
    const items = [human(), tool('Read'), text(), result()];
    const { at, suppressed } = buildCoordinatorTurns(items, team);
    expect(at.size).toBe(0);
    expect(suppressed.size).toBe(0);
    expect(isCoordinatorTrigger(human())).toBe(false);
    expect(isCoordinatorTrigger(note(WAVE_NOTE))).toBe(true);
  });

  it('доклад делегированной задачи — тоже служебный триггер', () => {
    const report: ChatItem = { kind: 'user_message', text: 'итог по задаче', delegationTaskId: 't-1' };
    const items = [report, tool('Read'), text(), result()];
    expect(only(buildCoordinatorTurns(items, team).at).startIdx).toBe(0);
    expect(isCoordinatorTrigger({ kind: 'user_message', text: '↩ Отчёт по делегированной задаче: Кнопка\n\nготово' })).toBe(true);
  });

  it('следующий служебный ход — отдельная группа, result закрывает предыдущую', () => {
    const items = [note(WAVE_NOTE), tool('Read'), text(), result(), note(STAFF_NOTE_CARD_ANSWER), tool('Bash'), text('ок')];
    const { at } = buildCoordinatorTurns(items, team);
    expect([...at.keys()]).toEqual([0, 4]);
    expect(at.get(0)!.endIdx).toBe(3);
    expect(at.get(4)!.endIdx).toBe(6);
    // Хвост ленты без result — ход ещё идёт
    expect(at.get(4)!.running).toBe(true);
    expect(at.get(0)!.running).toBe(false);
  });

  it('вне режима штаба (teamState нет) группировки нет вовсе', () => {
    const items = [note(WAVE_NOTE), tool('Read'), text(), result()];
    expect(buildCoordinatorTurns(items, null).at.size).toBe(0);
    expect(buildCoordinatorTurns(items, undefined).suppressed.size).toBe(0);
  });

  it('незнакомый элемент ленты внутрь карточки не прячется', () => {
    const items = [note(WAVE_NOTE), tool('Read'), { kind: 'model_switched', model: 'b', previousModel: 'a' } as ChatItem, text(), result()];
    const { at, suppressed } = buildCoordinatorTurns(items, team);
    expect([...at.keys()]).toEqual([0, 3]);
    expect(suppressed.has(2)).toBe(false);
  });
});

// ЖЁСТКИЙ ИНВАРИАНТ: элементы, требующие внимания человека, остаются самостоятельными
describe('инвариант: карточка не поглощает элементы для человека', () => {
  const attention: ChatItem[] = [
    { kind: 'team_escalation', escalationId: 'e1', escalation: { id: 'e1', kind: 'blocker', title: 'Блокер', details: '', actions: [], wave: 2, resolved: false } },
    { kind: 'team_plan', planId: 'pl1', plan: { id: 'pl1', request: '', summary: '', subtasks: [], waveCount: 1, executorCount: 1, version: 1, assumptions: [], changes: [], createdAt: '' }, resolved: false },
    { kind: 'ask_question', toolUseId: 'q1', input: {}, resolved: false },
    { kind: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: {}, resolved: false },
    { kind: 'plan_review', requestId: 'pr1', plan: 'план', resolved: false },
    { kind: 'error', text: 'упало' },
    { kind: 'error_group', date: 0, items: [{ kind: 'error', text: 'упало' }] },
  ];

  for (const item of attention) {
    it(`${item.kind} режет группу и не попадает в activity`, () => {
      const items = [note(WAVE_NOTE), tool('Read'), item, tool('Bash'), text(), result()];
      const { at, suppressed } = buildCoordinatorTurns(items, team);
      // Элемент виден в ленте сам по себе
      expect(suppressed.has(2)).toBe(false);
      for (const g of at.values()) {
        expect(g.activity.some(e => e.item === item)).toBe(false);
        expect(g.startIdx <= 2 && 2 <= g.endIdx).toBe(false);
      }
      // Остаток хода — новая группа с якорем на первом элементе остатка
      expect([...at.keys()]).toEqual([0, 3]);
      expect(at.get(0)!.endIdx).toBe(1);
      expect(at.get(3)!.endIdx).toBe(5);
      expect(at.get(3)!.statusRunning).toBe(at.get(0)!.statusRunning);
    });
  }

  it('карточка, оборвавшая ход, гасит признак «работает»', () => {
    const items = [note(WAVE_NOTE), tool('Read'), attention[0]];
    const { at } = buildCoordinatorTurns(items, team);
    expect(at.get(0)!.running).toBe(false);
  });

  it('ошибка рядом с группой красит её как неудавшийся ход', () => {
    const before = buildCoordinatorTurns([note(WAVE_NOTE), tool('Read'), { kind: 'error', text: 'упало' }, result()], team);
    expect(before.at.get(3)!.isError).toBe(true);
    const after = buildCoordinatorTurns([note(WAVE_NOTE), tool('Read'), { kind: 'error', text: 'упало' }], team);
    expect(after.at.get(0)!.isError).toBe(true);
  });

  it('result с чужим subtype — тоже ошибка хода', () => {
    const items = [note(WAVE_NOTE), tool('Read'), text(), result(3000, 'error_during_execution')];
    expect(only(buildCoordinatorTurns(items, team).at).isError).toBe(true);
  });
});

describe('подписи фаз', () => {
  it('закрытая волна — «Разбирает доклады волны N»', () => {
    expect(coordinatorPhaseFromNote(WAVE_NOTE)).toEqual({
      running: 'Разбирает доклады волны 2', done: 'разобрал доклады волны 2',
    });
    expect(coordinatorPhaseFromNote('Волна 11 закрыта — сводка передана координатору').done)
      .toBe('разобрал доклады волны 11');
  });

  it('ответ на карточку, возврат в интервью и доклад исполнителя — свои формулировки', () => {
    expect(coordinatorPhaseFromNote(STAFF_NOTE_CARD_ANSWER)).toEqual(COORDINATOR_PHASE_CARD);
    expect(coordinatorPhaseFromNote(STAFF_NOTE_BACK_TO_INTERVIEW)).toEqual(COORDINATOR_PHASE_INTERVIEW);
    expect(coordinatorPhaseFromNote(STAFF_NOTE_TASK_REPORT)).toEqual(COORDINATOR_PHASE_REPORT);
    // Формулировки макета: короче и понятнее прежних кодовых
    expect(COORDINATOR_PHASE_CARD).toEqual({ running: 'Учитывает ваш ответ', done: 'учёл ответ' });
    expect(COORDINATOR_PHASE_INTERVIEW).toEqual({ running: 'Готовит вопросы', done: 'подготовил вопросы' });
  });

  it('незнакомая заметка штаба показывается как есть', () => {
    expect(coordinatorPhaseFromNote('Напоминание исполнителю: задача не закрыта')).toEqual({
      running: 'Напоминание исполнителю: задача не закрыта',
      done: 'напоминание исполнителю: задача не закрыта',
    });
  });

  it('хвостовая пунктуация незнакомой заметки срезается — строка разделена точками', () => {
    expect(coordinatorPhaseFromNote('Задача снята с исполнителя.').done)
      .toBe('задача снята с исполнителя');
    expect(coordinatorPhaseFromNote('Ждём ответа…').done).toBe('ждём ответа');
    expect(coordinatorPhaseFromNote('Волна сорвалась!').done).toBe('волна сорвалась');
    // Вопрос остаётся вопросом
    expect(coordinatorPhaseFromNote('Что дальше?').done).toBe('что дальше?');
    const items = [note('Задача снята с исполнителя.'), tool('Read'), text(), result()];
    expect(coordinatorCollapsedSummary(only(buildCoordinatorTurns(items, team).at)))
      .toBe('Координатор · задача снята с исполнителя · 12с · 1 действие');
  });

  it('триггер без заметки (доклад делегированной задачи) — разбор доклада', () => {
    expect(coordinatorPhaseFromNote(undefined)).toEqual(COORDINATOR_PHASE_REPORT);
  });

  it('раздача задач видна по вызовам и перебивает заметку', () => {
    expect(coordinatorPhaseFromTools(['mcp__tasks__tasks_create'], false)).toEqual(COORDINATOR_PHASE_ASSIGN);
    expect(coordinatorPhaseFromTools(['mcp__tasks__tasks_run_executor'], false)).toEqual(COORDINATOR_PHASE_ASSIGN);
    expect(coordinatorPhaseFromTools(['Read', 'Bash'], false)).toBeNull();
    const items = [note(WAVE_NOTE), tool('mcp__tasks__tasks_create', 'c1'), text(), result()];
    expect(only(buildCoordinatorTurns(items, team).at).statusRunning).toBe('Ставит задачи исполнителям');
  });

  it('повторный запуск по той же задаче — перевыдача', () => {
    expect(coordinatorPhaseFromTools(['mcp__tasks__tasks_run_executor'], true)).toEqual(COORDINATOR_PHASE_REISSUE);
    const run = (id: string, taskId: string) => tool('mcp__tasks__tasks_run_executor', id, { taskId });
    const items = [
      note(WAVE_NOTE), run('r1', 't-7'), text(), result(),
      note(STAFF_NOTE_TASK_REPORT), run('r2', 't-7'), text('перевыдал'), result(),
    ];
    const { at } = buildCoordinatorTurns(items, team);
    expect(at.get(0)!.statusRunning).toBe('Ставит задачи исполнителям');
    expect(at.get(4)!.statusRunning).toBe('Перевыдаёт задачу');
    expect(at.get(4)!.statusDone).toBe('перевыдал задачу');
  });
});

describe('свёрнутый вид', () => {
  it('«Координатор · разобрал доклады волны 2 · 12с · 4 действия»', () => {
    const items = [note(WAVE_NOTE), tool('Read', 'a'), tool('Grep', 'b'), tool('Read', 'c'), tool('Bash', 'd'), text(), result()];
    const g = only(buildCoordinatorTurns(items, team).at);
    expect(g.metrics.tools).toBe(4);
    expect(coordinatorCollapsedSummary(g)).toBe('Координатор · разобрал доклады волны 2 · 12с · 4 действия');
  });

  it('пока ход идёт — настоящее время, метрик результата ещё нет', () => {
    const items = [note(WAVE_NOTE), tool('Read', 'a'), text()];
    const g = only(buildCoordinatorTurns(items, team).at);
    expect(g.running).toBe(true);
    expect(coordinatorCollapsedSummary(g)).toBe('Координатор · разбирает доклады волны 2 · 1 действие');
  });

  it('токенов в свёрнутой строке нет — на узком экране обрывалась бы ровно на них', () => {
    const items: ChatItem[] = [
      note(WAVE_NOTE), tool('Read', 'a'), text(),
      { kind: 'result', subtype: 'success', durationMs: 72000, numTurns: 2, usage: { inputTokens: 12000, outputTokens: 300, cacheReadTokens: 0, cacheCreationTokens: 0 } },
    ];
    const g = only(buildCoordinatorTurns(items, team).at);
    // Метрика жива (её показывает футер развёрнутой карточки), но в строку не идёт
    expect(g.metrics.tokens).toBe(12300);
    expect(coordinatorCollapsedSummary(g)).toBe('Координатор · разобрал доклады волны 2 · 1м 12с · 1 действие');
    expect(coordinatorCollapsedSummary(g)).not.toContain('токен');
  });

  it('пустая сводка без инструментов/метрик — текст уходит в answerIdx', () => {
    // text() без parentToolUseId — ответ карточки, activity пустая, метрики нулевые
    const items: ChatItem[] = [note(WAVE_NOTE), text()];
    const g = only(buildCoordinatorTurns(items, team).at);
    expect(g.activity.length).toBe(0);
    expect(g.answerIdx).not.toBeNull();
    expect(g.metrics.tools).toBe(0);
    expect(g.metrics.durationMs).toBeUndefined();
    expect(g.metrics.tokens).toBeUndefined();
    expect(coordinatorCollapsedSummary(g)).toBe('Координатор · разбирает доклады волны 2');
  });
});

describe('строка состояния в шапке', () => {
  it('завершённый ход говорит в прошедшем времени, с заглавной', () => {
    const items = [note(WAVE_NOTE), tool('Read'), text(), result()];
    const g = only(buildCoordinatorTurns(items, team).at);
    expect(g.running).toBe(false);
    expect(coordinatorStatusLine(g)).toBe('Разобрал доклады волны 2');
  });

  it('живой ход — настоящее время как есть', () => {
    const items = [note(WAVE_NOTE), tool('Read')];
    const g = only(buildCoordinatorTurns(items, team).at);
    expect(g.running).toBe(true);
    expect(coordinatorStatusLine(g)).toBe('Разбирает доклады волны 2');
  });
});

describe('прерывание хода человеком', () => {
  it('«Стоп» сразу за группой помечает её как прерванную, ошибкой не считая', () => {
    const items: ChatItem[] = [note(WAVE_NOTE), tool('Read'), text(), { kind: 'interrupted' }];
    const { at, suppressed } = buildCoordinatorTurns(items, team);
    const g = at.get(0)!;
    expect(g.endIdx).toBe(2);
    expect(g.aborted).toBe(true);
    expect(g.isError).toBe(false);
    expect(g.running).toBe(false);
    // Сам маркер прерывания остаётся в ленте
    expect(suppressed.has(3)).toBe(false);
  });

  it('обычный завершённый ход прерванным не считается', () => {
    const items = [note(WAVE_NOTE), tool('Read'), text(), result()];
    expect(only(buildCoordinatorTurns(items, team).at).aborted).toBe(false);
  });

  it('ошибка следом за группой прерыванием не становится', () => {
    const items: ChatItem[] = [note(WAVE_NOTE), tool('Read'), { kind: 'error', text: 'упало' }];
    const g = buildCoordinatorTurns(items, team).at.get(0)!;
    expect(g.isError).toBe(true);
    expect(g.aborted).toBe(false);
  });
});

describe('прочее покрытие kinds', () => {
  it('file_changed проходит в группу как содержимое', () => {
    const fc: ChatItem = { kind: 'file_changed', path: 'x.cs', added: 1, removed: 0 };
    const items = [note(WAVE_NOTE), fc, text(), result()];
    const { at, suppressed } = buildCoordinatorTurns(items, team);
    const g = only(at);
    expect(g.activity.some(e => e.item === fc)).toBe(true);
    expect(suppressed.has(1)).toBe(true);
  });

  it('redacted_thinking проходит в группу', () => {
    const rt: ChatItem = { kind: 'redacted_thinking' };
    const items = [note(WAVE_NOTE), rt, text(), result()];
    const g = only(buildCoordinatorTurns(items, team).at);
    expect(g.activity.some(e => e.item === rt)).toBe(true);
  });

  it('неожиданный kind завершает группу (остановка на «другом»)', () => {
    // unknown.kind не из CONTENT_KINDS → группа обрезается на index=1
    // j++ после break переносит якорь новой группы на index=3 (text)
    const unknown = { kind: 'voice_delta' } as unknown as ChatItem;
    const items = [note(WAVE_NOTE), tool('Read'), unknown, text(), result()];
    const { at, suppressed } = buildCoordinatorTurns(items, team);
    expect([...at.keys()]).toEqual([0, 3]);
    // Неожиданный элемент остаётся в ленте сам по себе
    expect(suppressed.has(2)).toBe(false);
  });

  it('группа без answerIdx (только tool_use) — корректный startIdx/endIdx', () => {
    const items = [note(WAVE_NOTE), tool('Read', 'r'), tool('Bash', 'b'), result()];
    const g = only(buildCoordinatorTurns(items, team).at);
    expect(g.answerIdx).toBeNull();
    expect(g.startIdx).toBe(0);
    expect(g.endIdx).toBe(3);
    expect(g.activity.map(e => e.item.kind)).toEqual(['tool_use', 'tool_use']);
  });
});
