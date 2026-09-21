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
import { ChatItemView } from '../ChatItemView';
import {
  ChatProjectContext, ChatTreePathContext, ChatSessionContext, PersonaContext,
  SpeakingItemContext, AssistantNameContext,
} from '../contexts';

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
});