// «Продолжить в новом чате»: кнопка под ответом, подсказка и плашки связи.
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import { ChatItemView } from '../ChatItemView';
import { ContinueHint, ContinuationBanner } from '../ContinueChat';
import {
  ChatProjectContext, ChatTreePathContext, ChatSessionContext, PersonaContext,
  SpeakingItemContext, AssistantNameContext,
} from '../contexts';

function renderText(onContinue?: () => void): string {
  const noop = () => {};
  const view = createElement(ChatItemView, {
    item: { kind: 'text', text: 'Готово' } as ChatItem, index: 0, online: true,
    onToggleThinking: noop, onAllowPermission: noop, onDenyPermission: noop, onAllowAlways: noop,
    onAnswerQuestion: noop, onRespondPlan: noop, onSwitchMode: noop, onRetry: noop, onInterrupt: noop,
    onContinue,
  });
  // Локальный проект (project = null) — кнопка не завязана на ProjectFeature.ChatBranch
  return renderToStaticMarkup(
    createElement(ChatProjectContext.Provider, { value: null },
      createElement(ChatTreePathContext.Provider, { value: null },
        createElement(ChatSessionContext.Provider, { value: null },
          createElement(PersonaContext.Provider, { value: null },
            createElement(SpeakingItemContext.Provider, { value: null },
              createElement(AssistantNameContext.Provider, { value: 'Ассистент' }, view)))))));
}

describe('кнопка «Продолжить в новом чате»', () => {
  it('есть у ответа, которому передан onContinue (в том числе без ветвления)', () => {
    const html = renderText(() => {});
    expect(html).toContain('data-testid="continue-chat-button"');
    expect(html).toContain('Продолжить в новом чате');
    expect(html).not.toContain('Ветвление от этого ответа');
  });
  it('нет у ответа без onContinue', () => {
    expect(renderText(undefined)).not.toContain('continue-chat-button');
  });
});

describe('подсказка при большом контексте (вариант C: полоса RowNotice)', () => {
  it('десктоп: размер, «Продолжить» со стрелкой, ✕ «Не сейчас», role=status', () => {
    const html = renderToStaticMarkup(createElement(ContinueHint, { tokens: 312_000, onOpen: () => {}, onSnooze: () => {} }));
    expect(html).toContain('data-testid="continue-hint"');
    expect(html).toContain('role="status"');
    expect(html).toContain('data-testid="continue-hint-open"');
    expect(html).toContain('data-testid="continue-hint-snooze"');
    expect(html).toContain('aria-label="Не сейчас"');
    expect(html).toContain('>312K<');
    expect(html).toContain('Продолжить');
    expect(html).toContain('дешевле продолжить в новом чате');
  });
  it('мобила: короче — «Контекст 312K» и «В новый чат»', () => {
    const html = renderToStaticMarkup(createElement(ContinueHint, { tokens: 312_000, isMobile: true, onOpen: () => {}, onSnooze: () => {} }));
    expect(html).toContain('data-testid="continue-hint"');
    expect(html).toContain('В новый чат');
    expect(html).not.toContain('дешевле продолжить в новом чате');
  });
});

describe('плашки связи', () => {
  it('в старом чате — «Продолжено в новом чате»', () => {
    const html = renderToStaticMarkup(createElement(ContinuationBanner, { kind: 'into', onOpen: () => {} }));
    expect(html).toContain('Продолжено в новом чате');
    expect(html).toContain('continuation-into');
  });
  it('в новом чате — «Продолжение чата <имя>»', () => {
    const html = renderToStaticMarkup(createElement(ContinuationBanner, { kind: 'from', name: 'Спринт 12', onOpen: () => {} }));
    expect(html).toContain('Продолжение чата «Спринт 12»');
    expect(html).toContain('continuation-from');
  });
});
