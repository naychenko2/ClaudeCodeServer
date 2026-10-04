// Метка отметок и вход «С компьютера» вида «картинка» (ADR-023, 2к-3).
import { beforeEach, describe, expect, it, vi } from 'vitest';

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); }, clear: () => store.clear(), key: () => null, length: 0,
} as Storage;
vi.stubGlobal('window', Object.assign(new EventTarget(), { innerWidth: 1440, innerHeight: 900 }));

import type { ChatContextPrimary } from '../../../lib/chatContext/types';
import { imageEditorApi } from '../api';
import { __applyThreads, __resetThreadStore, getThreadMarks, setThreadMarks } from '../thread/threadStore';
import type { ImageThread } from '../thread/threadsApi';
import { imageKindApi } from './kind';

const ctx = { projectId: 'p1', sessionId: 's1', isMobile: false };
const primary = (kind = 'image'): ChatContextPrimary => ({
  id: 'x', kind, ref: { threadId: 'cat' }, by: 'human', addedAt: '', label: 'cat', version: null, thumb: null, missing: false, role: null,
});
const thread = { id: 'cat', file: 'images/cat.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null, currentStepId: null, settings: null, pendingJobId: null, createdAt: '' } as unknown as ImageThread;
const mark = { type: 'mask', points: [[1, 1], [9, 9]], width: 4 } as never;

beforeEach(() => {
  __resetThreadStore();
  __applyThreads('s1', 'p1', { focus: 'cat', revision: 1, threads: [thread] });
});

describe('метка «Отмечено: N ✕»', () => {
  it('без отметок метки нет; с отметками — число и ✕ снимает их', () => {
    expect(imageKindApi.note?.(ctx, primary())).toBeNull();
    setThreadMarks('cat', [mark, mark], { w: 100, h: 100 });
    const note = imageKindApi.note?.(ctx, primary());
    expect(note?.label).toBe('Отмечено: 2');
    note?.clear();
    expect(getThreadMarks('cat').marks).toHaveLength(0);
    expect(imageKindApi.note?.(ctx, primary())).toBeNull();
  });
});

describe('«С компьютера»', () => {
  it('основной объект-картинка берёт образцы ролями стиль, объект, лицо; чужой вид — нет', () => {
    const up = imageKindApi.upload?.(ctx, primary());
    expect(up?.roles.map(r => r.role)).toEqual(['style', 'object', 'face']);
    expect(up?.kind).toBe('image');
    expect(imageKindApi.upload?.(ctx, primary('audio'))).toBeNull();
  });

  it('send загружает файл и возвращает ref {upload}', async () => {
    vi.spyOn(imageEditorApi(), 'uploadSample').mockResolvedValue({ uploadId: 'u9' });
    const ref = await imageKindApi.upload?.(ctx, primary())?.send(new File(['x'], 'a.png'));
    expect(ref).toEqual({ upload: 'u9' });
  });
});
