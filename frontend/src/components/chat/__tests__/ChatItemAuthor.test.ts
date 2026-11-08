// Подпись автора над пузырём человека в общем проекте (S2a-F1). Рендер статикой: проверяем
// раскрытый DOM пузыря — есть ли подпись, чей аватар (серый у бывшего) и пометка.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItem } from '../../../types';
import { C } from '../../../lib/design';
import { ChatItemView } from '../ChatItemView';
import {
  AssistantNameContext, ChatProjectContext, ChatSessionContext, ChatTreePathContext,
  PersonaContext, SpeakingItemContext,
} from '../contexts';

vi.mock('../../../lib/offline', async (orig) => ({
  ...(await orig<typeof import('../../../lib/offline')>()),
  readStoredUserId: () => 'u-me',
}));

type Ctx = NonNullable<React.ContextType<typeof ChatProjectContext>>;
const sharedCtx: Ctx = {
  id: 'p1', rootPath: '/tmp/p1', myRole: 'editor',
  formerMembers: [{ userId: 'u-old', displayName: 'Олег' }],
};

function render(item: ChatItem, project: Ctx | null): string {
  const noop = () => {};
  return renderToStaticMarkup(
    createElement(ChatProjectContext.Provider, { value: project },
      createElement(ChatTreePathContext.Provider, { value: null },
        createElement(ChatSessionContext.Provider, { value: null },
          createElement(PersonaContext.Provider, { value: null },
            createElement(SpeakingItemContext.Provider, { value: null },
              createElement(AssistantNameContext.Provider, { value: 'Ассистент' },
                createElement(ChatItemView, {
                  item, index: 0, online: true,
                  onToggleThinking: noop, onAllowPermission: noop, onDenyPermission: noop,
                  onAllowAlways: noop, onAnswerQuestion: noop, onRespondPlan: noop,
                  onSwitchMode: noop, onRetry: noop, onInterrupt: noop,
                }))))))),
  );
}

const msg = (senderUserId?: string): ChatItem => ({
  kind: 'user_message', text: 'Привет, команда', ts: Date.UTC(2026, 9, 30, 12, 30),
  ...(senderUserId ? { senderUserId } : {}),
});

describe('подпись автора над пузырём человека', () => {
  it('свой пузырь в общем проекте — без подписи', () => {
    const html = render(msg('u-me'), sharedCtx);
    expect(html).toContain('Привет, команда');
    expect(html).not.toContain('message-author');
  });

  it('чужой пузырь — подпись с аватаром', () => {
    const html = render(msg('u-maria'), sharedCtx);
    expect(html).toContain('message-author');
    expect(html).toContain('width:18px');
    expect(html).not.toContain('больше не в проекте');
  });

  it('бывший участник — имя, серый аватар и «больше не в проекте»', () => {
    const html = render(msg('u-old'), sharedCtx);
    expect(html).toContain('Олег');
    expect(html).toContain('больше не в проекте');
    expect(html).toContain(`background:${C.bgInset}`);
  });

  it('личный проект и сообщение без senderUserId — как раньше, без подписи', () => {
    expect(render(msg('u-maria'), { id: 'p1', rootPath: '/tmp/p1' })).not.toContain('message-author');
    expect(render(msg('u-maria'), null)).not.toContain('message-author');
    expect(render(msg(), sharedCtx)).not.toContain('message-author');
  });
});
