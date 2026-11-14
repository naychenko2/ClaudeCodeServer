import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ImageThread, ImageThreadLaunch, ImageThreadVersion, ImageThreadsState } from './threadsApi';
import type { JobStatus } from './useJobStatus';

// Жалоба 13.11: в карточке версии и строке запуска видна конкретная модель, что отработала (с причиной
// «Авто» и запасной), а во время рисования — одна полоса на запуск. SSR-рендер, как в launchFace.test.ts;
// живой статус задачи подменяется — эффекты подписки в SSR не идут

const storage = new Map<string, string>([['cc-image-editor-mock', 'all']]);
const fakeStorage = (m: Map<string, string>) => ({
  getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); },
  removeItem: (k: string) => { m.delete(k); }, clear: () => m.clear(), key: () => null, length: 0,
}) as Storage;
vi.stubGlobal('localStorage', fakeStorage(storage));
vi.stubGlobal('sessionStorage', fakeStorage(new Map()));

const live = vi.hoisted(() => ({ status: null as JobStatus | null }));
vi.mock('./useJobStatus', async orig => ({
  ...(await orig<typeof import('./useJobStatus')>()),
  useJobStatus: () => ({ status: live.status, cancel: async () => {} }),
}));

const { LaunchAnchor } = await import('./VersionCards');
const { __applyThreads, __resetThreadStore } = await import('./threadStore');
const { loadCatalog } = await import('./catalog');

const ver = (id: string, patch: Partial<ImageThreadVersion> = {}): ImageThreadVersion => ({
  id, number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null,
  steps: [], currentStepId: null, createdAt: '2026-11-13T00:00:00Z', ...patch,
});

const launch = (patch: Partial<ImageThreadLaunch> = {}): ImageThreadLaunch => ({
  jobId: 'j1', baseVersionId: null, baseStepId: null, at: '2026-11-13T00:00:00Z',
  status: 'done', initiator: 'agent', prompt: 'рыбак', ...patch,
});

const thread = (l: ImageThreadLaunch, extra: ImageThreadVersion[] = []): ImageThread => ({
  id: 't1', file: 'img/fisher.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null,
  currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-11-13T00:00:00Z',
  versions: [ver('origin'), ...extra], currentVersionId: extra.length ? extra[0].id : 'origin',
  launches: [l],
});

const state = (t: ImageThread): ImageThreadsState => ({ focus: t.id, revision: 3, threads: [t] });

const ctx = (data: Record<string, unknown>): ChatItemToolCtx => ({
  item: { kind: 'module_record', module: 'imageeditor', recordType: 'image_launch_versions', data, fallback: null } as never,
  online: true, projectId: 'pm', sessionId: 's1', persona: null,
});

const render = (t: ImageThread, data: Record<string, unknown>) => {
  __applyThreads('s1', 'pm', state(t));
  return renderToStaticMarkup(createElement(LaunchAnchor, { ctx: ctx({ threadId: 't1', jobId: 'j1', count: 1, ...data }) }));
};

const status = (patch: Partial<JobStatus>): JobStatus => ({
  phase: 'run', variants: [], count: 0, cost: null, charged: null, error: null, model: null, createdAt: null,
  run: null, runs: null, eta: null, runStartedAt: null, queuePosition: null, fallbackEta: null, gpu: null, ...patch,
});

const bars = (html: string) => html.split('role="progressbar"').length - 1;

const reason = 'Авто → MOSS: фото людей';
const v1 = ver('v1', { number: 1, jobId: 'j1', variant: 1, steps: ['s1'], currentStepId: 's1' });

beforeEach(async () => {
  __resetThreadStore();
  live.status = null;
  await loadCatalog('pm');
});

describe('Подпись модели в карточке версии', () => {
  it('локальная модель — её имя из каталога и причина «Авто», не «локальные модели»', () => {
    const html = render(thread(launch({ model: 'moss-image', routeReason: reason }), [v1]), { model: 'moss-image' });
    expect(html).toContain('MOSS-Image · авто: фото людей');
    expect(html).not.toContain('локальные модели');
  });

  it('запуск ушёл на запасную — фактическая модель и «вместо …» модели котировки', () => {
    const html = render(thread(launch({ model: 'qwen-image-2.1', routeReason: reason }), [v1]), { model: 'moss-image' });
    expect(html).toContain('Qwen-Image 2.1 · вместо MOSS-Image · авто: фото людей');
  });

  it('старая нить без модели в запуске — модель из записи ленты, без причины', () => {
    const html = render(thread(launch(), [v1]), { model: 'qwen-image-2.1-turbo' });
    expect(html).toContain('Qwen-Image 2.1 Turbo');
    expect(html).not.toContain('авто:');
  });
});

describe('Идущий запуск: одна полоса', () => {
  const running = () => thread(launch({ status: 'running', model: 'moss-image', routeReason: reason }));

  it('локальная задача со строкой очереди GPU — полоса одна (своя не дублирует строку GPU)', () => {
    live.status = status({ run: 1, runs: 2, model: 'moss-image', gpu: { running: true, percent: 40, percentExact: true } });
    const html = render(running(), { model: 'moss-image', count: 2 });
    expect(bars(html)).toBe(1);
  });

  it('поставщик без строки GPU — полоса тоже одна, своя', () => {
    live.status = status({});
    expect(bars(render(running(), { model: 'moss-image', count: 2 }))).toBe(1);
  });

  it('несколько вариантов — «вариант k из N» текстом', () => {
    live.status = status({ run: 2, runs: 2, model: 'moss-image', gpu: { running: true } });
    const html = render(running(), { model: 'moss-image', count: 2 });
    expect(html).toContain('Рисуем… · вариант 2 из 2');
    expect(html).not.toContain('идёт на');
  });

  it('драйвер ушёл на запасную до первого варианта — «идёт на …» по живому статусу', () => {
    live.status = status({ run: 1, runs: 1, model: 'qwen-image-2.1' });
    const html = render(running(), { model: 'moss-image' });
    expect(html).toContain('Рисуем… · идёт на Qwen-Image 2.1');
  });
});
