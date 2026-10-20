// Плашка «Ветка от …» (kind='branched_from'): дефект после удаления оригинала —
// плашка оставалась кликабельной ссылкой, клик по битой ссылке менял URL-хеш
// на несуществующий чат молча. Регрессия: при наличии availableChatIds и
// отсутствии id оригинала в нём — плашка деградирует в обычный текст, без
// <a> и без onClick. Тест рендерит только эту ветку через контекст-обвязку,
// нужную ChatItemView (его JSX-дерево использует несколько контекстов ленты).
import { describe, it, expect } from 'vitest';
import { createElement, type ReactElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import type { FeatureRun } from '../../../types/featureRun';
import { ChatItemView } from '../ChatItemView';
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