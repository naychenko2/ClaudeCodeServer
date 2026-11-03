// Действия чата (QA-P1, бэк f777c8460): переименовать и архивировать — владелец и редактор
// (любой видимый чат, зеркало DenyContentWrite); удалить — автор чата и владелец проекта
// (зеркало ChatAuthorGate/ChatWriteRightsEndpointTests); читателю — ничего; обычный проект
// (без myRole) — как раньше. Окружение node без jsdom: логика гейта — чистыми функциями,
// проводка в карточку, список и шапку — сверкой исходников (образец contentRights.test.ts).
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  CHAT_DELETE_DENIED, CHAT_WRITE_DENIED,
  chatActionDeniedReason, chatDeleteDeniedReason, chatWriteDeniedReason, isNotChatAuthorError,
} from '../participantGate';

const ME = 'user-me';
const OTHER = 'user-other';

describe('chatWriteDeniedReason (переименовать/архивировать)', () => {
  it('читателю — отказ с причиной; редактору и владельцу — можно', () => {
    expect(chatWriteDeniedReason({ myRole: 'reader' })).toBe(CHAT_WRITE_DENIED);
    expect(chatWriteDeniedReason({ myRole: 'editor' })).toBeNull();
    expect(chatWriteDeniedReason({ myRole: 'owner' })).toBeNull();
  });
  it('обычный проект и чат вне проекта — можно, как раньше', () => {
    expect(chatWriteDeniedReason({})).toBeNull();
    expect(chatWriteDeniedReason(null)).toBeNull();
    expect(chatWriteDeniedReason(undefined)).toBeNull();
  });
});

describe('chatDeleteDeniedReason (удалить)', () => {
  it('редактору: свой чат можно, чужой — нет (и через диспетчер)', () => {
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: ME }, ME)).toBeNull();
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: OTHER }, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatActionDeniedReason('delete', { myRole: 'editor' }, { createdByUserId: OTHER }, ME)).toBe(CHAT_DELETE_DENIED);
    // чат без автора (старая запись) и мой id без пары — fail-closed, как на бэке (NoAuthorChatId)
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: null }, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, null, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: ME }, null)).toBe(CHAT_DELETE_DENIED);
  });
  it('читателю: любой чат, даже свой (и через диспетчер)', () => {
    expect(chatDeleteDeniedReason({ myRole: 'reader' }, { createdByUserId: ME }, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatActionDeniedReason('delete', { myRole: 'reader' }, { createdByUserId: ME }, ME)).toBe(CHAT_DELETE_DENIED);
  });
  it('владельцу и обычному проекту: любой чат, как раньше', () => {
    expect(chatDeleteDeniedReason({ myRole: 'owner' }, { createdByUserId: OTHER }, ME)).toBeNull();
    expect(chatDeleteDeniedReason({}, { createdByUserId: OTHER }, ME)).toBeNull();
    expect(chatDeleteDeniedReason(null, { createdByUserId: OTHER }, null)).toBeNull();
  });
});

describe('chatActionDeniedReason (диспетчер)', () => {
  it('rename/archive — гейт записи: редактору можно, читателю нет', () => {
    for (const key of ['rename', 'archive']) {
      expect(chatActionDeniedReason(key, { myRole: 'editor' }, { createdByUserId: OTHER }, ME)).toBeNull();
      expect(chatActionDeniedReason(key, { myRole: 'reader' }, { createdByUserId: ME }, ME)).toBe(CHAT_WRITE_DENIED);
    }
  });
  it('прочие пункты («Теги», «Срок», «Закрепить», «Мьют») гейт не трогает', () => {
    for (const key of ['tags', 'expiry', 'pin', 'notify']) {
      expect(chatActionDeniedReason(key, { myRole: 'reader' }, { createdByUserId: OTHER }, ME)).toBeNull();
    }
  });
});

describe('isNotChatAuthorError', () => {
  const err = (status: number, code: string) =>
    Object.assign(new Error('Только автор чата'), { status, body: { error: 'Только автор чата', code } });
  it('403 + not_chat_author', () => {
    expect(isNotChatAuthorError(err(403, 'not_chat_author'))).toBe(true);
  });
  it('другой код или статус — нет', () => {
    expect(isNotChatAuthorError(err(403, 'other'))).toBe(false);
    expect(isNotChatAuthorError(err(409, 'not_chat_author'))).toBe(false);
    expect(isNotChatAuthorError(new Error('x'))).toBe(false);
    expect(isNotChatAuthorError(null)).toBe(false);
  });
});

describe('проводка в карточку и список', () => {
  const card = readFileSync(new URL('../../../../components/ChatCard.tsx', import.meta.url), 'utf8');
  const list = readFileSync(new URL('../../../../components/SessionList.tsx', import.meta.url), 'utf8');
  it('карточка выключает каждое действие своей причиной', () => {
    expect(card).toContain("k === 'rename' ? renameDeniedReason ?? null");
    expect(card).toContain("k === 'archive' ? archiveDeniedReason ?? null");
    expect(card).toContain("k === 'delete' ? deleteDeniedReason ?? null");
    expect(card).toContain("disabled={!!deniedReason('rename')}");
    expect(card).toContain("disabled={!!deniedReason('archive')}");
    expect(card).toContain("disabled={!!deniedReason('delete')}");
  });
  it('список передаёт причины из единого гейта и ловит отказ', () => {
    expect(list).toContain("renameDeniedReason={chatActionDeniedReason('rename', project, s, myUserId)}");
    expect(list).toContain("archiveDeniedReason={chatActionDeniedReason('archive', project, s, myUserId)}");
    expect(list).toContain("deleteDeniedReason={chatActionDeniedReason('delete', project, s, myUserId)}");
    expect(list).toContain('const myUserId = readStoredUserId();');
    expect(list).toContain('isNotChatAuthorError(e)');
  });
});

describe('проводка в шапку открытого чата', () => {
  const bar = readFileSync(new URL('../../../../components/chat/ChatHeaderBar.tsx', import.meta.url), 'utf8');
  // Блок JSX/объекта от якоря до ближайшего onClick — внутри обязаны быть и подсказка, и disabled
  const block = (anchor: string): string => {
    const start = bar.indexOf(anchor);
    expect(start, `якорь не найден: ${anchor}`).toBeGreaterThan(-1);
    return bar.slice(start, bar.indexOf('onClick', start));
  };
  it('ряд, «⋯» и runAction идут через единый гейт; отказ бэка ловится', () => {
    expect(bar).toContain('const actionDenied = (key: string): string | null => chatActionDeniedReason(key, project, session, myUserId);');
    expect(bar).toContain('if (actionDenied(key)) return;');
    expect(bar).toContain('const denied = actionDenied(key);');
    expect(bar).toContain('disabled={!!denied}');
    expect(bar).toContain('disabled: !!denied,');
    expect(bar).toContain('isNotChatAuthorError(e)');
  });
  it('AI-входы следуют гейту переименования: слушатель retitle, пункт и «AI-действия»', () => {
    expect(bar).toContain("locked={!!actionDenied('rename')}");
    const b1 = block('label="Переименовать по переписке"');
    expect(b1).toContain("hint={actionDenied('rename') ?? undefined}");
    expect(b1).toContain("disabled={!!actionDenied('rename')}");
    const b2 = block("label: 'AI-действия'");
    expect(b2).toContain("disabled: !!actionDenied('rename'),");
    expect(b2).toContain("sublabel: actionDenied('rename') ?? undefined,");
  });
});

describe('проводка AI-переименования в палитре (chat.retitle)', () => {
  const actions = readFileSync(new URL('../../../../lib/ai/actions.tsx', import.meta.url), 'utf8');
  it('when-гейт действия — тот же гейт записи', () => {
    expect(actions).toContain('!chatWriteDeniedReason(c.nav?.project)');
  });
});
