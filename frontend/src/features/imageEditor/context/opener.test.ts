// Вход из «Файлов» (слот context-opener, ADR-023, 2к-2): путь → нить файла → ссылка основного объекта.
import { beforeEach, describe, expect, it, vi } from 'vitest';

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); }, clear: () => store.clear(), key: () => null, length: 0,
} as Storage;
vi.stubGlobal('window', Object.assign(new EventTarget(), { innerWidth: 1440, innerHeight: 900 }));

import { __applyThreads, __resetThreadStore } from '../thread/threadStore';
import { threadsApi, type ImageThread } from '../thread/threadsApi';
import { imageRefOfPath } from './opener';

const thread = (id: string): ImageThread => ({ id, file: `images/${id}.png`, lineage: [], stacks: [] } as unknown as ImageThread);

beforeEach(() => {
  vi.restoreAllMocks();
  __resetThreadStore();
  __applyThreads('s1', 'p1', { focus: null, revision: 4, threads: [] });
});

describe('imageRefOfPath', () => {
  it('создаёт нить файла от текущей ревизии и отдаёт ссылку на ту, где встал фокус', async () => {
    const create = vi.spyOn(threadsApi, 'create').mockResolvedValue({ focus: 't9', revision: 5, threads: [thread('t9')] });
    expect(await imageRefOfPath('p1', 's1', 'images/hero.png')).toEqual({ kind: 'image', ref: { threadId: 't9' } });
    expect(create).toHaveBeenCalledWith('p1', 's1', { file: 'images/hero.png', revision: 4 });
  });

  it('мутация не прошла — null, ссылки нет', async () => {
    vi.spyOn(threadsApi, 'create').mockRejectedValue(new Error('сеть'));
    expect(await imageRefOfPath('p1', 's1', 'images/hero.png')).toBeNull();
  });
});
