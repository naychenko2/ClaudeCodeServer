// «Работать с этой» и клик по карточке (ADR-023, 2к-2): запись в стор контекста чата и показ панели.
import { beforeEach, describe, expect, it, vi } from 'vitest';

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); }, clear: () => store.clear(), key: () => null, length: 0,
} as Storage;
const events: CustomEvent[] = [];
vi.stubGlobal('window', Object.assign(new EventTarget(), { innerWidth: 1440, innerHeight: 900 }));
window.addEventListener('cc-reveal-panel', e => events.push(e as CustomEvent));

import { chatContextApi } from '../../../lib/chatContext/api';
import { __applyChatContext, __resetChatContextStore } from '../../../lib/chatContext/store';
import { pickInContext, workWithInContext } from './work';

const dto = { revision: 3, primary: null, refs: [] };

beforeEach(() => {
  vi.restoreAllMocks();
  events.length = 0;
  __resetChatContextStore();
  __applyChatContext('s1', dto);
});

describe('workWithInContext', () => {
  it('ставит основной объект и показывает панель «Контекст» (просьба открыть — и закрытую)', async () => {
    const put = vi.spyOn(chatContextApi, 'setPrimary').mockResolvedValue({ ...dto, revision: 4 });
    expect(await workWithInContext('s1', 't1', 'v2')).toBe(true);
    expect(put).toHaveBeenCalledWith('s1', { kind: 'image', ref: { threadId: 't1', versionId: 'v2' } }, 3);
    expect(events).toHaveLength(1);
    expect(events[0].detail).toMatchObject({ key: 'chatContext', sessionId: 's1' });
  });

  it('на телефоне панель не поднимается: шторка закрыла бы поле ввода', async () => {
    vi.spyOn(chatContextApi, 'setPrimary').mockResolvedValue({ ...dto, revision: 4 });
    expect(await workWithInContext('s1', 't1', 'v2', false)).toBe(true);
    expect(events).toHaveLength(0);
  });

  it('сбой записи — панель не показываем', async () => {
    vi.spyOn(chatContextApi, 'setPrimary').mockRejectedValue(new Error('сеть'));
    expect(await workWithInContext('s1', 't1', null)).toBe(false);
    expect(events).toHaveLength(0);
  });
});

describe('pickInContext', () => {
  it('карточка уже в работе — запись не нужна, закрытая панель не открывается', async () => {
    const put = vi.spyOn(chatContextApi, 'setPrimary').mockResolvedValue(dto);
    await pickInContext('s1', 't1', 'v2', true);
    expect(put).not.toHaveBeenCalled();
    expect(events).toHaveLength(0);
  });

  it('другая карточка — ставим основной объект, но закрытую панель клик не открывает', async () => {
    const put = vi.spyOn(chatContextApi, 'setPrimary').mockResolvedValue({ ...dto, revision: 4 });
    await pickInContext('s1', 't2', null, false);
    expect(put).toHaveBeenCalledWith('s1', { kind: 'image', ref: { threadId: 't2' } }, 3);
    expect(events).toHaveLength(0);
  });
});
