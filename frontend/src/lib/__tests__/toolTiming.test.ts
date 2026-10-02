import { describe, it, expect } from 'vitest';
import { awaitsToolStart, formatClock, isQueued, isToolGroupDone, shownFor, stageCaptionOf, tickShownClock, toolClockMs, toolElapsedMs, toolLiveness, toolProgressPercent, toolProgressText } from '../toolTiming';

// Дефект Киры: карточка разрешения встаёт ПОСЛЕ группы и сворачивала её в «N действий»
// вместе с живым Bash и таймером
describe('isToolGroupDone', () => {
  const read = { kind: 'tool_use', id: 'r', result: 'ok' };
  const bash = { kind: 'tool_use', id: 'b', result: null };
  const group = [read, bash];

  it('группа с идущим вызовом не пройдена, даже если после неё видимый элемент', () => {
    const liveness = toolLiveness(group, true);
    expect(isToolGroupDone({ entries: group, liveness, hasVisibleAfter: true, busy: true, awaitingPermission: false })).toBe(false);
  });

  it('группа с вызовом, ждущим разрешения, не пройдена', () => {
    // live пуст (например, чат на миг не «занят»), но вызов не оборван и разрешение не отвечено
    const liveness = { live: new Set<string>(), dead: new Set<string>() };
    expect(isToolGroupDone({ entries: group, liveness, hasVisibleAfter: true, busy: false, awaitingPermission: true })).toBe(false);
  });

  it('оборванная группа («прервано») сворачивается как раньше', () => {
    const items = [...group, { kind: 'interrupted' }];
    const liveness = toolLiveness(items, false);
    expect(isToolGroupDone({ entries: group, liveness, hasVisibleAfter: true, busy: false, awaitingPermission: false })).toBe(true);
    // и даже при висящей карточке разрешения — мёртвый вызов группу не держит
    expect(isToolGroupDone({ entries: group, liveness, hasVisibleAfter: true, busy: false, awaitingPermission: true })).toBe(true);
  });

  it('все вызовы с результатом: пройдена по видимому элементу после или концу хода', () => {
    const done = [read, { ...bash, result: 'ok' }];
    const liveness = toolLiveness(done, true);
    expect(isToolGroupDone({ entries: done, liveness, hasVisibleAfter: true, busy: true, awaitingPermission: false })).toBe(true);
    expect(isToolGroupDone({ entries: done, liveness, hasVisibleAfter: false, busy: true, awaitingPermission: false })).toBe(false);
    expect(isToolGroupDone({ entries: done, liveness, hasVisibleAfter: false, busy: false, awaitingPermission: false })).toBe(true);
  });
});

describe('toolProgressText', () => {
  it('сабагент: инструмент по-русски со строчной, как в середине фразы, и число действий со склонением', () => {
    expect(toolProgressText({ stage: 'working', label: 'Ищу где X', lastTool: 'Bash', toolUses: 3 })).toBe('сейчас команда · 3 действия');
    expect(toolProgressText({ stage: 'working', lastTool: 'Read', toolUses: 1 })).toBe('сейчас чтение · 1 действие');
    expect(toolProgressText({ stage: 'working', toolUses: 5 })).toBe('5 действий');
    expect(toolProgressText({ stage: 'working', lastTool: 'mcp__glif__compose_project' })).toBe('сейчас генерация медиа glif');
    expect(toolProgressText({ stage: 'working', lastTool: 'mcp__tests__run_tests', toolUses: 2 })).toBe('сейчас тесты · 2 действия');
  });

  // FAIL Киры: подпись сабагента была «сейчас тесты · 1 действие» — без вида прогона
  it('сабагент гоняет тесты: вид прогона в кавычках вместе с «тесты»', () => {
    expect(toolProgressText({ stage: 'working', lastTool: 'mcp__tests__run_tests', lastToolKind: 'vitest', toolUses: 1 }))
      .toBe('сейчас «тесты · vitest» · 1 действие');
    expect(toolProgressText({ stage: 'working', lastTool: 'mcp__tests__run_tests', lastToolKind: 'Playwright' }))
      .toBe('сейчас «тесты · Playwright»');
    // Вид у прочих инструментов не приклеивается
    expect(toolProgressText({ stage: 'working', lastTool: 'Bash', lastToolKind: 'vitest' })).toBe('сейчас команда');
  });

  // Ревью Веры: «сейчас notes · notes_create · 3 действия» читалось как три пункта
  it('незнакомый MCP-инструмент — в кавычках, его разделитель не сливается с разделителями подписи', () => {
    expect(toolProgressText({ stage: 'working', lastTool: 'mcp__notes__notes_create', toolUses: 3 })).toBe('сейчас «notes · notes_create» · 3 действия');
    expect(toolProgressText({ stage: 'working', lastTool: 'mcp__fal__run_model', toolUses: 0 })).toBe('сейчас «fal · run_model»');
  });

  // Ревью Веры: строчная — только у русских подписей, сырое английское имя не искажаем
  it('незнакомый не-MCP инструмент — как есть, без строчной буквы', () => {
    expect(toolProgressText({ stage: 'working', lastTool: 'LSP' })).toBe('сейчас LSP');
    expect(toolProgressText({ stage: 'working', lastTool: 'TaskOutput', toolUses: 2 })).toBe('сейчас TaskOutput · 2 действия');
  });

  it('локальная генерация: очередь, оценка процента и сколько осталось', () => {
    expect(toolProgressText({ stage: 'queued', queuePosition: 2 })).toBe('2-я в очереди');
    expect(toolProgressText({ stage: 'queued' })).toBe('в очереди');
    expect(toolProgressText({ stage: 'running', percent: 41.6, etaSeconds: 95 })).toBe('≈42% · осталось ~1:35');
    expect(toolProgressText({ stage: 'running', label: '1 из 3', percent: 60 })).toBe('1 из 3 · ≈60%');
  });

  it('настоящие шаги ComfyUI: шаг в подписи, процент без «≈»', () => {
    expect(toolProgressText({ stage: 'running', label: 'шаг 12 из 25', percent: 48, exact: true })).toBe('шаг 12 из 25 · 48%');
  });

  it('нечего сказать — null', () => {
    expect(toolProgressText(undefined)).toBeNull();
    expect(toolProgressText({ stage: 'working' })).toBeNull();
    expect(toolProgressText({ stage: 'running', etaSeconds: 0 })).toBeNull();
  });
});

// Витрина Веры (п. 1): «ждёт очереди сборок (занято 2) · в очереди» — очередь дважды
describe('toolProgressText — очередь с подписью сервера', () => {
  it('подпись уже про очередь — общее «в очереди» не дописывается', () => {
    expect(toolProgressText({ stage: 'queued', label: 'ждёт очереди сборок (занято 2)' })).toBe('ждёт очереди сборок (занято 2)');
  });

  it('место в очереди — новая цифра, дописывается и к подписи', () => {
    expect(toolProgressText({ stage: 'queued', label: '1 из 3', queuePosition: 2 })).toBe('1 из 3 · 2-я в очереди');
  });

  it('ожидание в очереди распознаётся (полоса не бежит)', () => {
    expect(isQueued({ stage: 'queued' })).toBe(true);
    expect(isQueued({ stage: 'running', percent: 10 })).toBe(false);
    expect(isQueued(undefined)).toBe(false);
  });
});

describe('toolProgressPercent', () => {
  it('оценка с потолком 95, без оценки — null (полоса бегущая)', () => {
    expect(toolProgressPercent({ percent: 40 })).toBe(40);
    expect(toolProgressPercent({ percent: 120 })).toBe(95);
    // Настоящие шаги — потолок 99: «готово» всё равно только по результату
    expect(toolProgressPercent({ percent: 97, exact: true })).toBe(97);
    expect(toolProgressPercent({ percent: 100, exact: true })).toBe(99);
    expect(toolProgressPercent({ stage: 'working' })).toBeNull();
    expect(toolProgressPercent(null)).toBeNull();
  });
});

describe('formatClock', () => {
  it('M:SS до часа и H:MM:SS после', () => {
    expect(formatClock(0)).toBe('0:00');
    expect(formatClock(42_900)).toBe('0:42');
    expect(formatClock(605_000)).toBe('10:05');
    expect(formatClock(3_725_000)).toBe('1:02:05');
  });

  it('отрицательное прижимается к нулю', () => {
    expect(formatClock(-5000)).toBe('0:00');
  });
});

describe('toolElapsedMs', () => {
  it('без результата — от старта до «сейчас»: после F5 отсчёт идёт от серверного старта', () => {
    expect(toolElapsedMs({ startedAt: 10_000 }, 52_000)).toBe(42_000);
  });

  it('с результатом — от старта до конца, «сейчас» не влияет', () => {
    expect(toolElapsedMs({ startedAt: 10_000, finishedAt: 25_000, result: 'ok' }, 99_000)).toBe(15_000);
  });

  it('нет отметок (старая история) или нет конца при результате — null', () => {
    expect(toolElapsedMs({}, 1000)).toBeNull();
    expect(toolElapsedMs({ startedAt: null }, 1000)).toBeNull();
    expect(toolElapsedMs({ startedAt: 10_000, result: 'ok' }, 99_000)).toBeNull();
  });

  it('часы браузера отстают от серверных — не уходим в минус', () => {
    expect(toolElapsedMs({ startedAt: 10_000 }, 9_000)).toBe(0);
  });

  it('"result": null из истории — результата нет, инструмент идёт', () => {
    expect(toolElapsedMs({ startedAt: 10_000, result: null, finishedAt: null }, 52_000)).toBe(42_000);
  });
});

// Регресс QA: после «Разрешить» цифра «идёт» замирала до 17 с — считала от tool_use
// (с ожиданием разрешения), а после tool_started держала максимум, пока честный отсчёт
// не догонит. Теперь у Bash и агентов цифры нет до фактического старта
describe('toolClockMs — замирание после ожидания разрешения', () => {
  const bash = (over: Record<string, unknown> = {}) => ({ name: 'Bash', startedAt: 1_000, ...over });

  it('Bash и агент до tool_started — «идёт» без цифры, даже если отсчёт уже есть', () => {
    for (const name of ['Bash', 'Task', 'Agent']) expect(toolClockMs(bash({ name }), true, 17_000)).toBeNull();
  });

  it('после tool_started — живой отсчёт', () => {
    expect(toolClockMs(bash({ started: true }), true, 4_000)).toBe(4_000);
  });

  it('инструменты без tool_started считают от tool_use, как прежде', () => {
    expect(awaitsToolStart({ name: 'Read' })).toBe(false);
    expect(toolClockMs({ name: 'mcp__x__y', startedAt: 1_000 }, true, 5_000)).toBe(5_000);
  });

  it('итог «готово» не меньше уже показанного «идёт»', () => {
    const done = bash({ started: true, result: 'ok', finishedAt: 16_000 });
    expect(toolClockMs(done, false, 15_800)).toBe(15_000 + 800);
    expect(toolClockMs(done, false, 9_000)).toBe(15_000);
    expect(toolClockMs(done, false, null)).toBe(15_000);
  });

  // Витрина Веры (п. 9): у «прервано» не было длительности, у «готово» и «ошибка» — была
  it('прерванный без результата — время до обрыва: по отметке обрыва, иначе последнее показанное', () => {
    expect(toolClockMs(bash({ started: true }), false, null, 31_000)).toBe(30_000);
    expect(toolClockMs(bash({ started: true }), false, 32_000, 31_000)).toBe(32_000);
    expect(toolClockMs(bash({ started: true }), false, 9_000)).toBe(9_000);
    expect(toolClockMs(bash({ started: true }), false, null)).toBeNull();
    // Bash до фактического старта не работал вовсе — времени нет и при отметке обрыва
    expect(toolClockMs(bash(), false, null, 31_000)).toBeNull();
  });
});

// Регресс ревью: ранняя карточка Write тикала, пока модель 30 с писала аргументы, финальный
// tool_use сдвигал startedAt, а максимум держал старое — «готово · 0:30» при 50 мс работы
describe('tickShownClock — сдвиг startedAt финальным tool_use', () => {
  it('отсчёт от прежнего старта не переносится ни в «идёт», ни в итог', () => {
    let clock = tickShownClock(null, 1_000, 31_000);
    expect(shownFor(clock, 1_000)).toBe(30_000);
    // Финальный tool_use: старт сдвинут на 31 000, до первого тика прежнее значение не в счёт
    expect(shownFor(clock, 31_000)).toBeNull();
    clock = tickShownClock(clock, 31_000, 31_050);
    expect(shownFor(clock, 31_000)).toBe(50);
    const done = { name: 'Write', startedAt: 31_000, finishedAt: 31_050, result: 'ok' };
    expect(toolClockMs(done, false, shownFor(clock, done.startedAt))).toBe(50);
  });

  it('в пределах одного старта значение не убывает', () => {
    const clock = tickShownClock(tickShownClock(null, 1_000, 5_000), 1_000, 4_000);
    expect(shownFor(clock, 1_000)).toBe(4_000);
  });
});

describe('toolLiveness', () => {
  const tool = (id: string, over: Record<string, unknown> = {}) => ({ kind: 'tool_use', id, ...over });
  const user = { kind: 'user_message' };

  it('живой ход: вызов без результата — live; с результатом — ни там, ни там', () => {
    const l = toolLiveness([user, tool('a', { result: 'ok' }), tool('b'), tool('c', { result: null })], true);
    expect([...l.live]).toEqual(['b', 'c']);
    expect(l.dead.size).toBe(0);
  });

  it('чат не занят — живых нет', () => {
    expect(toolLiveness([user, tool('b')], false).live.size).toBe(0);
  });

  it('оборванный ход + следующий: старый вызов dead, новый live', () => {
    for (const end of ['interrupted', 'error', 'session_ended']) {
      const l = toolLiveness([user, tool('old'), { kind: end }, user, tool('new')], true);
      expect([...l.dead]).toEqual(['old']);
      expect([...l.live]).toEqual(['new']);
    }
  });

  it('момент обрыва — из ts пометки «прервано»/ошибки; без ts отметки нет', () => {
    const l = toolLiveness([user, tool('a'), { kind: 'interrupted', ts: 9_000 }, user, tool('b'), { kind: 'session_ended' }], false);
    expect(l.abortedAt?.get('a')).toBe(9_000);
    expect(l.abortedAt?.has('b')).toBe(false);
  });

  it('штатный result закрывает только верхний уровень: вызов сабагента может доработать', () => {
    const l = toolLiveness([user, tool('top'), tool('child', { parentToolUseId: 'agent' }), { kind: 'result' }], true);
    expect([...l.dead]).toEqual(['top']);
    expect([...l.live]).toEqual(['child']);
  });

  it('фоновый агент прерван штатным exited (bgAborted): его внутренний вызов dead и на следующих ходах', () => {
    const agent = tool('agent', { result: 'Async agent launched successfully', bgDone: true, bgAborted: true });
    const l = toolLiveness([user, agent, tool('inner', { parentToolUseId: 'agent' }), { kind: 'result' }, user, tool('next')], true);
    expect([...l.dead]).toEqual(['inner']);
    expect([...l.live]).toEqual(['next']);
  });

  it('фоновый агент завершился (bgDone / workflowDone): внутренний вызов без результата dead', () => {
    for (const over of [{ bgDone: true }, { workflowDone: true }]) {
      const agent = tool('agent', { result: 'Async agent launched successfully', ...over });
      const l = toolLiveness([user, agent, tool('inner', { parentToolUseId: 'agent' }), { kind: 'result' }], true);
      expect([...l.dead]).toEqual(['inner']);
      expect(l.live.size).toBe(0);
    }
  });

  it('фоновый агент ещё работает (только квитанция запуска): внутренний вызов live', () => {
    const agent = tool('agent', { result: 'Async agent launched successfully' });
    const l = toolLiveness([user, agent, tool('inner', { parentToolUseId: 'agent' }), { kind: 'result' }], true);
    expect([...l.live]).toEqual(['inner']);
    expect(l.dead.size).toBe(0);
  });

  // Регресс QA (сценарий 4): основной ход запустил фонового агента и закончился, агент ещё
  // гоняет свой Bash — внутренняя карточка тикает и при «чат не занят»
  it('ход закончился, фоновый агент ещё работает: его внутренний вызов live и без busy', () => {
    const agent = tool('agent', { result: 'Async agent launched successfully' });
    const sub = tool('sub', { parentToolUseId: 'agent' });
    const items = [user, agent, tool('inner', { parentToolUseId: 'agent' }), sub,
      tool('deep', { parentToolUseId: 'sub' }), { kind: 'result' }];
    const l = toolLiveness(items, false);
    expect(new Set(l.live)).toEqual(new Set(['inner', 'sub', 'deep']));
    expect(l.dead.size).toBe(0);
  });

  it('без busy не оживают: вызов обычного сабагента и вызов закрытого фонового агента', () => {
    const sync = toolLiveness([user, tool('child', { parentToolUseId: 'agent' }), { kind: 'result' }], false);
    expect(sync.live.size).toBe(0);
    const closed = tool('agent', { result: 'Async agent launched successfully', bgDone: true });
    const l = toolLiveness([user, closed, tool('inner', { parentToolUseId: 'agent' }), { kind: 'result' }], false);
    expect(l.live.size).toBe(0);
    expect([...l.dead]).toEqual(['inner']);
  });

  it('CLI не дослал tool_result внутреннего вызова у завершённого сабагента: dead ещё в живом ходе', () => {
    const agent = tool('agent', { result: 'Готово: отчёт агента' });
    const l = toolLiveness([user, agent, tool('inner', { parentToolUseId: 'agent' }), tool('top')], true);
    expect([...l.dead]).toEqual(['inner']);
    expect([...l.live]).toEqual(['top']);
  });

  it('вложенность через агента: родитель закрыт — внук тоже dead', () => {
    const outer = tool('outer', { result: 'итог' });
    const mid = tool('mid', { parentToolUseId: 'outer' });
    const l = toolLiveness([user, outer, mid, tool('leaf', { parentToolUseId: 'mid' })], true);
    expect(new Set(l.dead)).toEqual(new Set(['mid', 'leaf']));
    expect(l.live.size).toBe(0);
  });
});

// Подпись при текущем этапе строки этапов (Вера №2): на очереди подробность «занято 2» не
// пропадает, а «ждёт очереди сборок» не дублирует слово этапа
describe('stageCaptionOf', () => {
  it('очередь — подробность из скобок, тесты — подпись целиком, сборка — без подписи', () => {
    expect(stageCaptionOf({ stage: 'queued', label: 'ждёт очереди сборок (занято 2)' })).toBe('занято 2');
    expect(stageCaptionOf({ stage: 'queued', label: 'ждёт слота' })).toBe('ждёт слота');
    expect(stageCaptionOf({ stage: 'running', label: '12 из 177' })).toBe('12 из 177');
    expect(stageCaptionOf({ stage: 'build', label: 'сборка' })).toBeNull();
    expect(stageCaptionOf(null)).toBeNull();
  });
});
