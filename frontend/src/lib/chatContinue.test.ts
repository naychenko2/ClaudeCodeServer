import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ChatItem } from '../types';
import {
  lastAssistantTextIndex, continueHintThreshold, shouldShowContinueHint,
  continuationLinks, submitContinue, formatContextK,
  continueButtonGate, continuedLinkFromError, autoNavigateToContinued,
} from './chatContinue';

// autoNavigateToContinued опирается на nav/openChat/toast — в юните подменяем все три:
// getNav управляем через mock (вместо window.history.state), openChatById/showToast — шпионы.
const { mockGetNav, mockOpenChatById, mockShowToast } = vi.hoisted(() => ({
  mockGetNav: vi.fn(),
  mockOpenChatById: vi.fn(),
  mockShowToast: vi.fn(),
}));
vi.mock('./nav', () => ({ getNav: (...a: unknown[]) => mockGetNav(...a) }));
vi.mock('./openChat', () => ({ openChatById: (...a: unknown[]) => mockOpenChatById(...a) }));
vi.mock('./toast', () => ({ showToast: (...a: unknown[]) => mockShowToast(...a) }));
beforeEach(() => {
  mockGetNav.mockReset();
  mockOpenChatById.mockReset();
  mockShowToast.mockReset();
});

const user = (text: string): ChatItem => ({ kind: 'user_message', text } as ChatItem);
const text = (t: string, extra: object = {}): ChatItem => ({ kind: 'text', text: t, ...extra } as ChatItem);
const result = (): ChatItem => ({ kind: 'result' } as ChatItem);

describe('lastAssistantTextIndex — кнопка только на последнем ответе агента', () => {
  it('берёт последний text, а не любой', () => {
    const items = [user('a'), text('1'), result(), user('b'), text('2'), result()];
    expect(lastAssistantTextIndex(items)).toBe(4);
  });
  it('реплики сабагента (parentToolUseId) не считаются', () => {
    const items = [user('a'), text('главный'), text('сабагент', { parentToolUseId: 't1' })];
    expect(lastAssistantTextIndex(items)).toBe(1);
  });
  it('нет ответов агента — -1', () => {
    expect(lastAssistantTextIndex([user('a')])).toBe(-1);
    expect(lastAssistantTextIndex([])).toBe(-1);
  });
});

describe('порог подсказки', () => {
  it('окно 1M — 300K, остальные — 60 % окна', () => {
    expect(continueHintThreshold(1_000_000)).toBe(300_000);
    expect(continueHintThreshold(2_000_000)).toBe(300_000);
    expect(continueHintThreshold(200_000)).toBe(120_000);
  });
  it('ниже порога — нет, на пороге — да', () => {
    expect(shouldShowContinueHint({ tokens: 299_999, windowTokens: 1_000_000 })).toBe(false);
    expect(shouldShowContinueHint({ tokens: 300_000, windowTokens: 1_000_000 })).toBe(true);
    expect(shouldShowContinueHint({ tokens: 119_000, windowTokens: 200_000 })).toBe(false);
    expect(shouldShowContinueHint({ tokens: 120_000, windowTokens: 200_000 })).toBe(true);
  });
  it('нет оценки контекста — нет подсказки', () => {
    expect(shouldShowContinueHint({ tokens: undefined, windowTokens: 1_000_000 })).toBe(false);
  });
  it('«Не сейчас» прячет до роста на +100K', () => {
    const snoozedAt = 320_000;
    expect(shouldShowContinueHint({ tokens: 320_000, windowTokens: 1_000_000, snoozedAt })).toBe(false);
    expect(shouldShowContinueHint({ tokens: 419_999, windowTokens: 1_000_000, snoozedAt })).toBe(false);
    expect(shouldShowContinueHint({ tokens: 420_000, windowTokens: 1_000_000, snoozedAt })).toBe(true);
  });
  it('формат «N K»', () => {
    expect(formatContextK(312_400)).toBe('312K');
  });
  it('от 1M — в миллионах «N,NM»', () => {
    expect(formatContextK(1_200_000)).toBe('1,2M');
    expect(formatContextK(1_000_000)).toBe('1M');
  });
});

describe('связи продолжения', () => {
  it('отсутствующие поля читаются как null', () => {
    expect(continuationLinks({})).toEqual({ from: null, into: null });
    expect(continuationLinks({ continuedFromSessionId: 'a', continuedInSessionId: 'b' }))
      .toEqual({ from: 'a', into: 'b' });
  });
});

describe('submitContinue — обработка отказа 409', () => {
  it('успех: true, заметка уходит обрезанной', async () => {
    let got = '';
    expect(await submitContinue(async n => { got = n; }, '  дальше  ')).toBe(true);
    expect(got).toBe('дальше');
  });
  it('отказ (чат занят): false — диалог снимет блокировку и останется открытым', async () => {
    const busy = async () => { throw new Error('Чат занят'); };
    expect(await submitContinue(busy, '')).toBe(false);
  });
});

describe('continueButtonGate — кнопка под последним ответом', () => {
  const base = { online: true, isWaiting: false, turnRunning: false, continuedInto: false };
  it('показывает, когда ход завершён, сеть есть и чат не продолжен', () => {
    expect(continueButtonGate(base)).toBe(true);
  });
  it('прячет, пока ход идёт (turnRunning) — ответ ещё стримится', () => {
    expect(continueButtonGate({ ...base, turnRunning: true })).toBe(false);
  });
  it('прячет, пока ход занят (isWaiting)', () => {
    expect(continueButtonGate({ ...base, isWaiting: true })).toBe(false);
  });
  it('прячет, если чат уже продолжен (continuedInto)', () => {
    expect(continueButtonGate({ ...base, continuedInto: true })).toBe(false);
  });
  it('прячет офлайн', () => {
    expect(continueButtonGate({ ...base, online: false })).toBe(false);
  });
});

describe('continuedLinkFromError — ссылка на новый чат в отказе 409', () => {
  it('id в тексте ошибки → ссылка', () => {
    const err = new Error('Чат уже продолжен: 1b2c3d4e-5f60-7182-93a4-b5c6d7e8f900') as Error & { status?: number };
    err.status = 409;
    expect(continuedLinkFromError(err)).toBe('1b2c3d4e-5f60-7182-93a4-b5c6d7e8f900');
  });
  it('id в поле тела (newSessionId) → ссылка', () => {
    const err = new Error('Чат уже продолжен') as Error & { status?: number; body?: unknown };
    err.status = 409;
    err.body = { error: 'Чат уже продолжен', newSessionId: 'aaaa1111-bbbb-cccc-dddd-eeee11112222' };
    expect(continuedLinkFromError(err)).toBe('aaaa1111-bbbb-cccc-dddd-eeee11112222');
  });
  it('не 409 → ссылки нет', () => {
    const err = new Error('1b2c3d4e-5f60-7182-93a4-b5c6d7e8f900') as Error & { status?: number };
    err.status = 400;
    expect(continuedLinkFromError(err)).toBeNull();
  });
  it('409 без ссылки в тексте → null (тост без действия)', () => {
    const err = new Error('Чат уже продолжен') as Error & { status?: number };
    err.status = 409;
    expect(continuedLinkFromError(err)).toBeNull();
  });
});

describe('autoNavigateToContinued — авто-переход в новый чат по chat_continued', () => {
  it('исходный чат открыт — переход (openChatById + тост), true', () => {
    mockGetNav.mockReturnValue({ screen: 'chats', chatId: 'source' });
    expect(autoNavigateToContinued({ sessionId: 'source', newSessionId: 'new' })).toBe(true);
    expect(mockOpenChatById).toHaveBeenCalledWith('new');
    expect(mockShowToast).toHaveBeenCalledWith('Продолжение чата', 'Продолжили в новом чате', 'info');
  });
  it('открыт другой чат — без перехода, false (стор обновит обработчик списка)', () => {
    mockGetNav.mockReturnValue({ screen: 'chats', chatId: 'other' });
    expect(autoNavigateToContinued({ sessionId: 'source', newSessionId: 'new' })).toBe(false);
    expect(mockOpenChatById).not.toHaveBeenCalled();
    expect(mockShowToast).not.toHaveBeenCalled();
  });
  it('уже в новом чате (ручной «Продолжить» ушёл первым) — без повторного перехода, false', () => {
    mockGetNav.mockReturnValue({ screen: 'chats', chatId: 'new' });
    expect(autoNavigateToContinued({ sessionId: 'source', newSessionId: 'new' })).toBe(false);
    expect(mockOpenChatById).not.toHaveBeenCalled();
  });
  it('другой экран (nav null) — без перехода, false', () => {
    mockGetNav.mockReturnValue(null);
    expect(autoNavigateToContinued({ sessionId: 'source', newSessionId: 'new' })).toBe(false);
    expect(mockOpenChatById).not.toHaveBeenCalled();
  });
});
