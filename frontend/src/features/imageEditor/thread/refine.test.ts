import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

// Доводка версии Fibo 2.0 (Ш8): кнопка в карточке версии и запуск — котировка генерации local / fibo2 и поля
// refineField / refineValue. Окружение node: мок API включается ключом localStorage
const storage = new Map<string, string>([['cc-image-editor-mock', 'all']]);
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
vi.stubGlobal('localStorage', fakeStorage(storage));
vi.stubGlobal('sessionStorage', fakeStorage(new Map()));
vi.stubGlobal('window', Object.assign(new EventTarget(), {
  setTimeout, clearTimeout, setInterval, clearInterval, innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));

const { imageEditorApi } = await import('../api');
const { launchRefine } = await import('./useThreadLaunch');
const { VersionCard } = await import('./VersionCards');
const { __resetThreadStore } = await import('./threadStore');
import type { ImageEditJobInput, ImageEditQuoteRequest } from '../api';
import type { ImageThread, ImageThreadVersion } from './threadsApi';

const scene = { json: '{"background_setting":"street"}', seed: 101, width: 1248, height: 832 };

const ver = (id: string, patch: Partial<ImageThreadVersion> = {}): ImageThreadVersion => ({
  id, number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null,
  steps: [], currentStepId: null, createdAt: '2026-11-12T00:00:00Z', ...patch,
});

const thread = (v: ImageThreadVersion): ImageThread => ({
  id: 't1', file: 'img/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-11-12T00:00:00Z',
  versions: [ver('origin'), v], currentVersionId: v.id, launches: [],
});

const fibo = () => ver('v1', { number: 1, jobId: 'j1', variant: 1, baseVersionId: 'origin', steps: ['s1'], currentStepId: 's1', vgl: scene });

beforeEach(() => {
  __resetThreadStore();
  vi.restoreAllMocks();
});

describe('Доводка Fibo 2.0', () => {
  it('кнопка «Доводка» — только у версии с JSON-описанием', () => {
    const html = (v: ImageThreadVersion) => renderToStaticMarkup(createElement(VersionCard, {
      projectId: 'p1', sessionId: 's1', thread: thread(v), version: v, focused: false,
    }));
    expect(html(fibo())).toContain('Доводка');
    expect(html({ ...fibo(), vgl: null })).not.toContain('Доводка');
  });

  it('запуск: котировка генерации local / fibo2 на один вариант, поле и значение — в запуск от этой версии', async () => {
    const api = imageEditorApi();
    const quotes: ImageEditQuoteRequest[] = [];
    const jobs: ImageEditJobInput[] = [];
    vi.spyOn(api, 'quote').mockImplementation(async (_p, body) => { quotes.push(body); return { quoteId: 'q1' } as never; });
    vi.spyOn(api, 'startJob').mockImplementation(async (_p, input) => { jobs.push(input); return { jobId: 'j2' } as never; });
    const v = fibo();

    const ok = await launchRefine('p1', 's1', thread(v), v, 'background', '  зимний лес ');

    expect(ok).toBe(true);
    expect(quotes).toHaveLength(1);
    expect(quotes[0]).toMatchObject({ provider: 'local', model: 'fibo2', op: 'generate', count: 1, references: 0, hasCharacter: false });
    expect(jobs).toEqual([expect.objectContaining({
      quoteId: 'q1', prompt: 'Доводка: фон → зимний лес', sessionId: 's1', threadId: 't1', versionId: 'v1',
      refineField: 'background', refineValue: 'зимний лес',
    })]);
    expect(jobs[0].source).toBeUndefined();
    expect(jobs[0].characterSlug).toBeUndefined();
  });
});
