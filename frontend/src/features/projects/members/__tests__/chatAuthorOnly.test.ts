// Автор-только действия чата (S1-F9): у участника «Переименовать / В архив / Удалить» выключены,
// отказ 403 not_chat_author опознаётся. Окружение node без jsdom — логика гейта чистыми
// функциями, проводка в карточку и список — сверкой исходников.
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { CHAT_AUTHOR_ONLY, chatActionAuthorLock, chatAuthorOnlyReason, isNotChatAuthorError } from '../participantGate';

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

describe('chatActionAuthorLock (шапка открытого чата)', () => {
  it('участнику rename/archive/delete выключены причиной', () => {
    for (const key of ['rename', 'archive', 'delete']) {
      expect(chatActionAuthorLock(key, { myRole: 'editor' })).toBe(CHAT_AUTHOR_ONLY);
      expect(chatActionAuthorLock(key, { myRole: 'reader' })).toBe(CHAT_AUTHOR_ONLY);
    }
  });
  it('владельцу и чату вне проекта активны; прочие пункты гейт не трогает', () => {
    for (const key of ['rename', 'archive', 'delete']) {
      expect(chatActionAuthorLock(key, { myRole: 'owner' } as never)).toBeNull();
      expect(chatActionAuthorLock(key, undefined)).toBeNull();
    }
    expect(chatActionAuthorLock('tags', { myRole: 'reader' })).toBeNull();
    expect(chatActionAuthorLock('expiry', { myRole: 'editor' })).toBeNull();
  });
});

describe('проводка в шапку открытого чата', () => {
  const bar = readFileSync(new URL('../../../../components/chat/ChatHeaderBar.tsx', import.meta.url), 'utf8');
  it('кнопки ряда, «⋯» и runAction идут через единый гейт; отказ бэка ловится', () => {
    expect(bar).toContain('disabled={!!authorLock}');
    expect(bar).toContain('disabled: !!authorLock');
    expect(bar).toContain('if (chatActionAuthorLock(key, project)) return;');
    expect(bar).toContain('isNotChatAuthorError(e)');
  });
});

describe('проводка AI-переименования в шапке (S1-F10)', () => {
  const bar = readFileSync(new URL('../../../../components/chat/ChatHeaderBar.tsx', import.meta.url), 'utf8');
  // Блок JSX/объекта от якоря до ближайшего onClick — внутри обязаны быть и hint, и disabled
  const block = (anchor: string): string => {
    const start = bar.indexOf(anchor);
    expect(start, `якорь не найден: ${anchor}`).toBeGreaterThan(-1);
    return bar.slice(start, bar.indexOf('onClick', start));
  };
  it('right-click меню: «Переименовать по переписке» выключен и подписан причиной', () => {
    const b = block('label="Переименовать по переписке"');
    expect(b).toContain('hint={chatAuthorOnlyReason(project) ?? undefined}');
    expect(b).toContain('disabled={!!chatAuthorOnlyReason(project)}');
  });
  it('мобильный пункт «AI-действия» выключен и подписан причиной', () => {
    const b = block("label: 'AI-действия'");
    expect(b).toContain('disabled: !!chatAuthorOnlyReason(project)');
    expect(b).toContain('sublabel: chatAuthorOnlyReason(project) ?? undefined');
  });
});
