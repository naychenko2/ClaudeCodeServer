// Автор-только действия чата (S1-F9): у участника «Переименовать / В архив / Удалить» выключены,
// отказ 403 not_chat_author опознаётся. Окружение node без jsdom — логика гейта чистыми
// функциями, проводка в карточку и список — сверкой исходников.
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { CHAT_AUTHOR_ONLY, chatAuthorOnlyReason, isNotChatAuthorError } from '../participantGate';

describe('chatAuthorOnlyReason', () => {
  it('участнику (editor/reader) — причина', () => {
    expect(chatAuthorOnlyReason({ myRole: 'editor' })).toBe(CHAT_AUTHOR_ONLY);
    expect(chatAuthorOnlyReason({ myRole: 'reader' })).toBe('Только автор чата');
  });
  it('владельцу и чатам вне проекта — null', () => {
    expect(chatAuthorOnlyReason({ myRole: 'owner' } as never)).toBeNull();
    expect(chatAuthorOnlyReason({})).toBeNull();
    expect(chatAuthorOnlyReason(null)).toBeNull();
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
  it('три пункта меню выключаются причиной', () => {
    expect(card).toContain("disabled={isAuthorOnly('rename')}");
    expect(card).toContain("disabled={isAuthorOnly('archive')}");
    expect(card).toContain("disabled={isAuthorOnly('delete')}");
  });
  it('список передаёт причину из единого гейта и ловит отказ', () => {
    expect(list).toContain('authorOnlyReason={chatAuthorOnlyReason(project)}');
    expect(list).toContain('isNotChatAuthorError(e)');
  });
});
