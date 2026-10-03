// Образцы и персонаж как референсы контекста (ADR-023, 2к-3): загрузка → attachRef, одна запись персонажа,
// смена роли заменой, число входов для котировки — из стора контекста, а не из памяти вкладки.
import { beforeEach, describe, expect, it, vi } from 'vitest';

const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); }, clear: () => store.clear(), key: () => null, length: 0,
} as Storage;
vi.stubGlobal('window', Object.assign(new EventTarget(), { innerWidth: 1440, innerHeight: 900 }));

import { chatContextApi } from '../../../lib/chatContext/api';
import { __applyChatContext, __resetChatContextStore } from '../../../lib/chatContext/store';
import type { ChatContextDto, ChatContextRef } from '../../../lib/chatContext/types';
import { imageEditorApi } from '../api';
import { setSamples, getSamples } from '../thread/threadStore';
import {
  addProjectSample, addUploadSamples, changeSampleRole, characterSlugOf, contextInputCounts, sampleRefs, setCharacterRef,
} from './samples';

const ref = (id: string, kind: string, refBody: Record<string, unknown>, role: string): ChatContextRef => ({
  id, kind, ref: refBody, by: 'human', addedAt: '', label: id, version: null, thumb: null, missing: false, role, usedBy: [],
});
const dto = (refs: ChatContextRef[], revision = 1): ChatContextDto => ({ revision, primary: null, refs });
const file = (name: string) => new File(['x'], name, { type: 'image/png' });

beforeEach(() => {
  vi.restoreAllMocks();
  __resetChatContextStore();
  __applyChatContext('s1', dto([]));
});

describe('образцы в контексте', () => {
  it('файл с диска: загрузка в рабочую папку, затем attachRef с ref {upload} и ролью', async () => {
    const up = vi.spyOn(imageEditorApi(), 'uploadSample').mockResolvedValue({ uploadId: 'u7' });
    const attach = vi.spyOn(chatContextApi, 'attachRef').mockResolvedValue(dto([], 2));
    await addUploadSamples('p1', 's1', [file('кот.png')], 'style');
    expect(up).toHaveBeenCalledWith('p1', expect.any(File), 'кот.png');
    expect(attach).toHaveBeenCalledWith('s1', { kind: 'image', ref: { upload: 'u7' }, role: 'style' }, 1);
  });

  it('образец не попадает в память вкладки: при флаге _samples остаются пустыми', async () => {
    vi.spyOn(imageEditorApi(), 'uploadSample').mockResolvedValue({ uploadId: 'u1' });
    vi.spyOn(chatContextApi, 'attachRef').mockResolvedValue(dto([], 2));
    await addUploadSamples('p1', 's1', [file('a.png')], 'style');
    expect(getSamples('p1')).toEqual([]);
    setSamples('p1', []);
  });

  it('файл проекта встаёт project-file с путём', async () => {
    const attach = vi.spyOn(chatContextApi, 'attachRef').mockResolvedValue(dto([], 2));
    await addProjectSample('s1', 'art/a.png', 'object');
    expect(attach).toHaveBeenCalledWith('s1', { kind: 'project-file', ref: { path: 'art/a.png' }, role: 'object' }, 1);
  });

  it('смена роли: новая запись встаёт, прежняя снимается; сбой добавления прежнюю не трогает', async () => {
    const old = ref('r1', 'image', { upload: 'u1' }, 'style');
    __applyChatContext('s1', dto([old]));
    const attach = vi.spyOn(chatContextApi, 'attachRef').mockResolvedValue(dto([old, ref('r2', 'image', { upload: 'u1' }, 'face')], 2));
    const detach = vi.spyOn(chatContextApi, 'detachRef').mockResolvedValue(dto([], 3));
    await changeSampleRole('s1', old, 'face');
    expect(attach).toHaveBeenCalledWith('s1', { kind: 'image', ref: { upload: 'u1' }, role: 'face' }, 1);
    expect(detach).toHaveBeenCalledWith('s1', 'r1', 2);
    detach.mockClear();
    attach.mockRejectedValue(new Error('сбой'));
    await changeSampleRole('s1', old, 'object');
    expect(detach).not.toHaveBeenCalled();
  });
});

describe('персонаж в контексте', () => {
  it('новый персонаж заменяет прежнего: старый снимается, новый встаёт ролью «персонаж»', async () => {
    __applyChatContext('s1', dto([ref('c1', 'image-character', { slug: 'anya' }, 'character')]));
    const detach = vi.spyOn(chatContextApi, 'detachRef').mockResolvedValue(dto([], 2));
    const attach = vi.spyOn(chatContextApi, 'attachRef').mockResolvedValue(dto([], 3));
    await setCharacterRef('s1', 'boris');
    expect(detach).toHaveBeenCalledWith('s1', 'c1', 1);
    expect(attach).toHaveBeenCalledWith('s1', { kind: 'image-character', ref: { slug: 'boris' }, role: 'character' }, 2);
  });

  it('null снимает, тот же персонаж не дублируется', async () => {
    __applyChatContext('s1', dto([ref('c1', 'image-character', { slug: 'anya' }, 'character')]));
    const attach = vi.spyOn(chatContextApi, 'attachRef').mockResolvedValue(dto([]));
    await setCharacterRef('s1', 'anya');
    expect(attach).not.toHaveBeenCalled();
    const detach = vi.spyOn(chatContextApi, 'detachRef').mockResolvedValue(dto([], 2));
    await setCharacterRef('s1', null);
    expect(detach).toHaveBeenCalledWith('s1', 'c1', 1);
  });
});

describe('входы запуска', () => {
  it('число образцов и признак персонажа берутся из контекста, а не из _samples', () => {
    __applyChatContext('s1', dto([
      ref('a', 'image', { upload: 'u1' }, 'style'), ref('b', 'project-file', { path: 'x.png' }, 'face'),
      ref('c', 'image-character', { slug: 'anya' }, 'character'),
    ]));
    expect(contextInputCounts('s1')).toEqual({ references: 2, hasCharacter: true });
    expect(characterSlugOf(dto([ref('c', 'image-character', { slug: 'anya' }, 'character')]))).toBe('anya');
    expect(sampleRefs(dto([ref('c', 'image-character', { slug: 'anya' }, 'character')]))).toEqual([]);
    expect(contextInputCounts('пусто')).toEqual({ references: 0, hasCharacter: false });
  });
});
