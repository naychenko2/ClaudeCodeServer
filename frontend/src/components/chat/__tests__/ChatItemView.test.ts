// Плашка «Ветка от …» (kind='branched_from'): дефект после удаления оригинала —
// плашка оставалась кликабельной ссылкой, клик по битой ссылке менял URL-хеш
// на несуществующий чат молча. Регрессия: при наличии availableChatIds и
// отсутствии id оригинала в нём — плашка деградирует в обычный текст, без
// <a> и без onClick. Тест рендерит только эту ветку через контекст-обвязку,
// нужную ChatItemView (его JSX-дерево использует несколько контекстов ленты).
import { afterEach, describe, it, expect } from 'vitest';
import { createElement, type ReactElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import { setAllFlags } from '../../../lib/featureFlags';
import type { FeatureRun } from '../../../types/featureRun';
import { ChatItemView, shouldHideAbortBanner } from '../ChatItemView';
import {
  ChatProjectContext, ChatTreePathContext, ChatSessionContext, PersonaContext,
  SpeakingItemContext, AssistantNameContext,
} from '../contexts';
import { __test } from '../../../lib/featureRuns';

// Минимальный элемент нужного kind. Нам интересно только поведение плашки «Ветка от …»
function branchedFrom(sourceSessionId: string, sourceName = 'Старый чат'): ChatItem {
  return {
    kind: 'branched_from',
    sourceSessionId,
    sourceName,
  };
}

// Обвязка провайдерами — без них useContext в ChatItemView вылетит на рендере.
// Все используемые ChatItemView контексты даём null/дефолтом: наш item не зависит
// ни от project, ни от persona, ни от session, и реагирует только на
// availableChatIds + логику case 'branched_from'. on* — noop'ы, чтобы memo
// не ругался на неопределённые функции (ChatItemView требует их в типах)
function renderItem(item: ChatItem, availableChatIds?: Set<string>): string {
  const noop = () => {};
  const tree: ReactElement = createElement(
    ChatProjectContext.Provider, { value: null },
    createElement(
      ChatTreePathContext.Provider, { value: null },
      createElement(
        ChatSessionContext.Provider, { value: null },
        createElement(
          PersonaContext.Provider, { value: null },
          createElement(
            SpeakingItemContext.Provider, { value: null },
            createElement(
              AssistantNameContext.Provider, { value: 'Ассистент' },
              createElement(ChatItemView, {
                item,
                index: 0,
                online: true,
                onToggleThinking: noop,
                onAllowPermission: noop,
                onDenyPermission: noop,
                onAllowAlways: noop,
                onAnswerQuestion: noop,
                onRespondPlan: noop,
                onSwitchMode: noop,
                onRetry: noop,
                onInterrupt: noop,
                availableChatIds,
              }),
            ),
          ),
        ),
      ),
    ),
  );
  return renderToStaticMarkup(tree);
}

// П.31: десктоп (project задан) — href «Ветка от …» project-scoped: #/project/{pid}/chat/{id},
// не глобальный #/chats/{id}. availableChatIds наполнен → плашка кликабельна.
function renderItemDesktop(item: ChatItem, availableChatIds: Set<string>, projectId: string): string {
  const noop = () => {};
  const tree: ReactElement = createElement(
    ChatProjectContext.Provider, { value: { id: projectId, rootPath: `/tmp/${projectId}` } },
    createElement(
      ChatTreePathContext.Provider, { value: null },
      createElement(
        ChatSessionContext.Provider, { value: null },
        createElement(
          PersonaContext.Provider, { value: null },
          createElement(
            SpeakingItemContext.Provider, { value: null },
            createElement(
              AssistantNameContext.Provider, { value: 'Ассистент' },
              createElement(ChatItemView, {
                item,
                index: 0,
                online: true,
                onToggleThinking: noop,
                onAllowPermission: noop,
                onDenyPermission: noop,
                onAllowAlways: noop,
                onAnswerQuestion: noop,
                onRespondPlan: noop,
                onSwitchMode: noop,
                onRetry: noop,
                onInterrupt: noop,
                availableChatIds,
              }),
            ),
          ),
        ),
      ),
    ),
  );
  return renderToStaticMarkup(tree);
}

describe('ChatItemView — плашка «Ветка от …»', () => {
  // Регрессия кейса: id оригинала есть в availableChatIds — кликабельная ссылка
  it('оригинал жив: рендерит ссылку с href на чат оригинала', () => {
    const html = renderItem(branchedFrom('src-1'), new Set(['src-1']));
    expect(html).toContain('Ветка от Старый чат');
    expect(html).toContain('href="#/chats/src-1"');
    // Клик по ссылке в нашем рендере не выполнится (статический маркап),
    // но важно, что элемент именно <a>, а не <span>
    expect(html).toMatch(/<a[^>]*href="#\/chats\/src-1"/);
  });

  // Регрессия кейса из задачи: оригинал удалён, его id нет в availableChatIds —
  // плашка деградирует в <span>, без href, без клика
  it('оригинал удалён: рендер без <a> и без href — обычная плашка-текст', () => {
    const html = renderItem(branchedFrom('src-1'), new Set(['другой-чат']));
    expect(html).toContain('Ветка от Старый чат');
    // Битой ссылки нет
    expect(html).not.toContain('href="#/chats/src-1"');
    // Обёртка плашки — <span> с title про удалённый оригинал, а не <a>.
    // (Статический маркап сериализует SVG с вложенными circle/path, поэтому
    // матчим по атрибуту title у открывающего тега, а не по точному внутреннему дереву)
    expect(html).toMatch(/<span\s[^>]*title="Оригинал удалён:[^"]*"/);
    // Подсказка для пользователя: оригинал удалён
    expect(html).toContain('Оригинал удалён: Старый чат');
  });

  // Обратная совместимость: availableChatIds не передан — fallback к ссылке
  it('availableChatIds не передан: ссылка рендерится как раньше', () => {
    const html = renderItem(branchedFrom('src-1'));
    expect(html).toContain('href="#/chats/src-1"');
    expect(html).toMatch(/<a[^>]*href="#\/chats\/src-1"/);
  });

  // Граничный кейс: Set пустой (загрузка ещё не пришла) — все плашки текстовые.
  // Это согласуется с «Set учит «ещё не знаем» = всё деградирует»
  it('availableChatIds — пустой Set: плашка текстовая', () => {
    const html = renderItem(branchedFrom('src-1'), new Set());
    expect(html).not.toContain('href="#/chats/src-1"');
    expect(html).toContain('Оригинал удалён');
  });

  // П.31: десктоп (project задан) — «Ветка от …» кликабельна, когда availableChatIds
  // наполнен: href project-scoped (#/project/{pid}/chat/{id}), не глобальный #/chats/{id}.
  it('десктоп (project задан): оригинал жив — кликабельная ссылка с project-scoped href', () => {
    const html = renderItemDesktop(branchedFrom('src-1'), new Set(['src-1']), 'p1');
    expect(html).toContain('Ветка от Старый чат');
    expect(html).toMatch(/<a[^>]*href="#\/project\/p1\/chat\/src-1"/);
  });

  // П.31 (грань): десктоп, но оригинал удалён (id не в availableChatIds) — даже в
  // project-контексте плашка деградирует в текст, project-scoped ссылки нет.
  it('десктоп (project задан): оригинал удалён — текст, без project-scoped href', () => {
    const html = renderItemDesktop(branchedFrom('src-1'), new Set(['другой']), 'p1');
    expect(html).toContain('Ветка от Старый чат');
    expect(html).not.toContain('href="#/project/p1/chat/src-1"');
  });
});

// Карточка плана: живое тело (переключатель «План хода / Исполнение фичи» + выбор фичи)
// рисуется ТОЛЬКО на текущей карточке плана (isCurrentPlan); прошлые — снимок TodoList.
// Мутация: убрать проверку isCurrentPlan → не-текущая карточка тоже «живая» → тест краснеет.
type ToolItem = Extract<ChatItem, { kind: 'tool_use' }>;

function renderTodoPlan(isCurrentPlan: boolean, sessionId: string): string {
  const noop = () => {};
  const item: ToolItem = {
    kind: 'tool_use',
    id: 'tw1',
    name: 'TodoWrite',
    input: { todos: [{ content: 'Шаг 1', status: 'pending' }] },
    result: null,
  };
  const tree: ReactElement = createElement(
    ChatProjectContext.Provider, { value: null },
    createElement(
      ChatTreePathContext.Provider, { value: null },
      createElement(
        ChatSessionContext.Provider, { value: sessionId },
        createElement(
          PersonaContext.Provider, { value: null },
          createElement(
            SpeakingItemContext.Provider, { value: null },
            createElement(
              AssistantNameContext.Provider, { value: 'Ассистент' },
              createElement(ChatItemView, {
                item,
                index: 0,
                online: true,
                isCurrentPlan,
                onToggleThinking: noop,
                onAllowPermission: noop,
                onDenyPermission: noop,
                onAllowAlways: noop,
                onAnswerQuestion: noop,
                onRespondPlan: noop,
                onSwitchMode: noop,
                onRetry: noop,
                onInterrupt: noop,
              }),
            ),
          ),
        ),
      ),
    ),
  );
  return renderToStaticMarkup(tree);
}

// Минимальный агрегат фичи (эпик + пустой конвейер) — для стора Ф1.
function featureRun(): FeatureRun {
  return {
    featureId: 'epic-1',
    epic: { id: 'epic-1', title: 'Пульт куратора', worktreeBranch: 'feat/curator-console' },
    steps: [],
    reviews: [],
    qa: [],
    final: null,
    merge: null,
    awaitingHumanSince: null,
    awaitingHumanReason: null,
    verdicts: { accepted: 2, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 1,
    ready: true,
  };
}

describe('TodoPlanView — живое тело только на текущей карточке плана', () => {
  const sid = 'plan-s1';

  it('не-текущая карточка (есть фичи) — снимок, без переключателя', () => {
    __test.reset();
    __test.setRuns(sid, [featureRun()]);
    const html = renderTodoPlan(false, sid);
    // Снимок: заголовок «План хода» + чек-лист, переключателя режима нет.
    expect(html).toContain('План хода');
    expect(html).not.toContain('Исполнение фичи');
  });

  it('текущая карточка (есть фичи) — живая, с переключателем', () => {
    __test.reset();
    __test.setRuns(sid, [featureRun()]);
    const html = renderTodoPlan(true, sid);
    // Живое тело: переключатель «План хода / Исполнение фичи» присутствует.
    expect(html).toContain('Исполнение фичи');
  });

  // Минор Р2 (Глеб): текущая карточка БЕЗ фич (runs=[]) — переключатель не рендерится,
  // разметка прежняя: заголовок «План хода», без «Исполнение фичи».
  it('текущая карточка (runs=[]) — без «Исполнение фичи», заголовок «План хода»', () => {
    __test.reset();
    __test.setRuns(sid, []);
    const html = renderTodoPlan(true, sid);
    expect(html).not.toContain('Исполнение фичи');
    expect(html).toContain('План хода');
  });
});

// === Ветка tool_use: tasks_create → строка инструмента (не карточка); tasks_run_executor → карточка ===
// Задачи создаются заранее (все шаги фичи сразу в todo) — карточка «Задача создана» на каждом
// tasks_create засоряла ленту. Теперь: tasks_create → обычная строка инструмента; карточка
// появляется, когда задача отдана в исполнение (tasks_run_executor, добор полной задачи по id).
describe('ChatItemView — ветка tool_use: tasks_create / tasks_run_executor', () => {
  function toolItem(name: string, over: Partial<Extract<ChatItem, { kind: 'tool_use' }>> = {}): ChatItem {
    return { kind: 'tool_use', id: 't1', name, input: {}, result: undefined, ...over } as ChatItem;
  }

  it('tasks_create → строка инструмента, БЕЗ карточки «Задача создана»', () => {
    const html = renderItem(toolItem('mcp__tasks__tasks_create', { input: { title: 'Шаг D1' }, result: '{"id":"task-1","title":"Шаг D1"}' }));
    expect(html, 'карточка «Задача создана» не рисуется').not.toContain('Задача создана');
    expect(html, 'строка инструмента рисуется (имя tools · tasks_create, не пустота)').toContain('tasks_create');
  });

  it('tasks_run_executor → SSR: карточки нет (задача ещё не доборена) — строка инструмента, не падение', () => {
    const html = renderItem(toolItem('mcp__tasks__tasks_run_executor', { input: { taskId: 'task-1' }, result: '{"id":"task-1","title":"Шаг D1","status":"inProgress"}' }));
    // SSR: useEffect ещё не отработал, задача не доборена → TaskRunExecutorView показывает строку
    // инструмента (не карточку). Карточка по доборенной задаче — покрыто TaskCreatedView.test.ts (async).
    expect(html, 'SSR: карточки нет (задача не доборена)').not.toContain('Задача создана');
    expect(html, 'строка инструмента рисуется').toContain('tasks_run_executor');
  });
});
describe('ChatItemView — work_loop_stopped: server_restart', () => {
  const text = 'Сервер был перезапущен — продолжаю ход с того же места';

  it('server_restart — инфо-пилюля с текстом сервера, без кнопок', () => {
    const html = renderItem({ kind: 'work_loop_stopped', reason: 'server_restart', text });
    expect(html).toContain(text);
    expect(html, 'инфо-токены').toContain('var(--c-info-bg)');
    expect(html, 'рамка и текст — инфо-цвет').toContain('var(--c-info)');
    expect(html, 'кнопок нет').not.toContain('<button');
  });

  it('team_restart остаётся нейтральной пилюлей', () => {
    const html = renderItem({ kind: 'work_loop_stopped', reason: 'team_restart', text: 'Перезапуск штаба' });
    expect(html).toContain('Перезапуск штаба');
    expect(html).not.toContain('var(--c-info-bg)');
  });
});

// === Обрыв хода + авто-продолжение (server_restart): красный баннер избыточен ===
// Обрыв хода — это kind='error' (текст «Ход прервался… Отправьте ещё раз»); сразу за ним —
// карточка work_loop_stopped reason='server_restart' (синяя пилюля). Решение — по соседству
// записей, не по тексту: shouldHideAbortBanner. Мутация: убрать `if (hideAbortBanner) return
// null` в case 'error' → тест «баннер скрыт» краснеет.
type ErrorItem = Extract<ChatItem, { kind: 'error' }>;
type WorkLoopStoppedItem = Extract<ChatItem, { kind: 'work_loop_stopped' }>;

const ABORT_TEXT = 'Ход прервался: процесс агента остановлен сервером (перезапуск или восстановление чата). Отправьте сообщение ещё раз.';

function errorItem(): ErrorItem {
  return { kind: 'error', text: ABORT_TEXT, canRetry: true };
}
function serverRestartCard(): WorkLoopStoppedItem {
  return { kind: 'work_loop_stopped', reason: 'server_restart', text: 'Сервер был перезапущен — продолжаю ход с того же места' };
}

// Рендер одиночной error-записи с пропом hideAbortBanner (та же обвязка контекстами, что renderItem).
function renderErrorItem(hideAbortBanner: boolean): string {
  const noop = () => {};
  const tree: ReactElement = createElement(
    ChatProjectContext.Provider, { value: null },
    createElement(
      ChatTreePathContext.Provider, { value: null },
      createElement(
        ChatSessionContext.Provider, { value: null },
        createElement(
          PersonaContext.Provider, { value: null },
          createElement(
            SpeakingItemContext.Provider, { value: null },
            createElement(
              AssistantNameContext.Provider, { value: 'Ассистент' },
              createElement(ChatItemView, {
                item: errorItem(),
                index: 0,
                online: true,
                hideAbortBanner,
                onToggleThinking: noop,
                onAllowPermission: noop,
                onDenyPermission: noop,
                onAllowAlways: noop,
                onAnswerQuestion: noop,
                onRespondPlan: noop,
                onSwitchMode: noop,
                onRetry: noop,
                onInterrupt: noop,
              }),
            ),
          ),
        ),
      ),
    ),
  );
  return renderToStaticMarkup(tree);
}

describe('ChatItemView — обрыв хода + авто-продолжение (server_restart)', () => {
  it('shouldHideAbortBanner: обрыв + server_restart → скрыть', () => {
    expect(shouldHideAbortBanner(errorItem(), serverRestartCard())).toBe(true);
  });

  it('shouldHideAbortBanner: обрыв без соседа (конец ленты) → показать', () => {
    expect(shouldHideAbortBanner(errorItem(), undefined)).toBe(false);
  });

  it('shouldHideAbortBanner: обрыв + work_loop_stopped с другим reason → показать', () => {
    expect(shouldHideAbortBanner(errorItem(), { kind: 'work_loop_stopped', reason: 'team_restart', text: 'Перезапуск штаба' })).toBe(false);
  });

  it('shouldHideAbortBanner: обрыв + сосед не work_loop_stopped → показать', () => {
    expect(shouldHideAbortBanner(errorItem(), errorItem())).toBe(false);
  });

  // Мутация: убрать `if (hideAbortBanner) return null` в case 'error' → этот тест краснеет.
  it('обрыв + hideAbortBanner=true → баннера нет (синей пилюли достаточно)', () => {
    expect(renderErrorItem(true), 'красный баннер обрыва скрыт').not.toContain(ABORT_TEXT);
  });

  it('обрыв + hideAbortBanner=false → баннер на месте (подсказка «отправьте ещё раз» верна)', () => {
    expect(renderErrorItem(false), 'красный баннер обрыва показан').toContain(ABORT_TEXT);
  });
});

// === F-2: причина промаха кэша в подвале хода (фича prompt-cache) ===
// Мутация: убрать span «кэш не помог: …» в мета-строке case 'result' → первый тест
// краснеет; убрать проверку фич-флага → красный и негативный про флаг.
function renderMeta(item: ChatItem, isLastResult = true): string {
  const noop = () => {};
  const tree: ReactElement = createElement(
    ChatProjectContext.Provider, { value: null },
    createElement(
      ChatTreePathContext.Provider, { value: null },
      createElement(
        ChatSessionContext.Provider, { value: null },
        createElement(
          PersonaContext.Provider, { value: null },
          createElement(
            SpeakingItemContext.Provider, { value: null },
            createElement(
              AssistantNameContext.Provider, { value: 'Ассистент' },
              createElement(ChatItemView, {
                item,
                index: 0,
                online: true,
                isLastResult,
                onToggleThinking: noop,
                onAllowPermission: noop,
                onDenyPermission: noop,
                onAllowAlways: noop,
                onAnswerQuestion: noop,
                onRespondPlan: noop,
                onSwitchMode: noop,
                onRetry: noop,
                onInterrupt: noop,
              }),
            ),
          ),
        ),
      ),
    ),
  );
  return renderToStaticMarkup(tree);
}

// Успешный итог хода с сигналом кэша; missReason — из карты подписей lib/promptCache
const resultWithCache = (missReason: string): ChatItem => ({
  kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1,
  usage: { inputTokens: 1200, outputTokens: 300 },
  promptCache: { readTokens: 1000, writtenTokens: 500, anchorAtMs: Date.now(), ttl: '1h', missReason },
} as ChatItem);
// Ход без данных кэша (сторонний провайдер, локальный движок, старая история)
const plainResult = (): ChatItem => ({
  kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1,
  usage: { inputTokens: 1200, outputTokens: 300 },
} as ChatItem);

describe('ChatItemView — причина промаха кэша в подвале хода (F-2)', () => {
  afterEach(() => setAllFlags({}));

  it('флаг вкл, промах есть: «кэш не помог: смена модели» и в подсказке «это нормально»', () => {
    setAllFlags({ 'prompt-cache': true });
    const html = renderMeta(resultWithCache('modelChanged'));
    expect(html, 'строка причины в подвале').toContain('кэш не помог: смена модели');
    expect(html, 'подтверждение пользы, не техники').toContain('Это нормально');
  });

  it('флаг выкл — строки нет, даже с промахом', () => {
    setAllFlags({ 'prompt-cache': false });
    expect(renderMeta(resultWithCache('modelChanged')), 'за выключенным флагом ничего нет').not.toContain('кэш не помог');
  });

  it('промаха нет (missReason none) — строки нет', () => {
    setAllFlags({ 'prompt-cache': true });
    expect(renderMeta(resultWithCache('none'))).not.toContain('кэш');
  });

  it('данных кэша нет — строки нет (не-Claude провайдер)', () => {
    setAllFlags({ 'prompt-cache': true });
    expect(renderMeta(plainResult())).not.toContain('кэш');
  });

  // Незнакомая причина (минор ревью F-2): в строке — человекочитательная «другая
  // причина», сырой кода — в подсказке (title).
  it('незнакомая причина: «другая причина» в строке, сырой код — в подсказке', () => {
    setAllFlags({ 'prompt-cache': true });
    const html = renderMeta(resultWithCache('weirdReason'));
    expect(html).toContain('кэш не помог: другая причина');
    expect(html, 'сырой код не потерян — он в title').toContain('Причина не распознана (weirdReason)');
    expect(html).toContain('Кэш пересобирается сам');
  });

  // Минор ревью F-2: мета-строка — только у последнего хода. Не последний result с
  // промахом — сегмента нет вовсе.
  it('isLastResult: false и промах — сегмента «кэш не помог» нет', () => {
    setAllFlags({ 'prompt-cache': true });
    expect(renderMeta(resultWithCache('modelChanged'), false), 'мета только у последнего хода').not.toContain('кэш не помог');
  });
});
