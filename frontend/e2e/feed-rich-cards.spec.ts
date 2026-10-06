import { test, expect, type Page, type Route } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Лента: богатая карточка на каждый вариант. Звук — песня из черновика, два запуска по два
// варианта и третий идёт; картинки — запуск на два варианта и второй идёт. Каждая версия —
// одна полноценная карточка на месте своего запуска, действия уходят от своей версии, пока
// запуск идёт — одна карточка хода. Бэкенд не нужен: собранный dist раздаётся статикой,
// /api/** и хаб — моки.
//
//   (cd dist && python3 -m http.server 5198) &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5198 FEED_SHOTS_DIR=../.cc-attachments/feed-rich-cards \
//     npx playwright test e2e/feed-rich-cards.spec.ts

const SHOTS = process.env.FEED_SHOTS_DIR || '';
const S = 'chat-song';
const T = 'thread-song';
const I = 'thread-img';
const now = new Date('2026-10-01T15:00:00Z').toISOString();
const at = (n: number) => Date.parse(now) + n * 1000;
// Непрозрачный PNG 1×1 — картинка версии
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

const CATALOG = {
  autoModelId: 'auto', maxCount: 4,
  providers: [{
    key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
    models: [{ id: 'ace-step', label: 'ACE-Step 1.5', caps: { ops: ['song'], languages: ['ru'], voiceKinds: [], producesFiles: ['main'], license: { label: 'Apache-2.0', kind: 'permissive' }, priceUnit: 'free', minDurationSec: 10, maxDurationSec: 240 } }],
  }],
};

const aver = (id: string, number: number, jobId: string, variant: number, base: string | null) =>
  ({ id, number, jobId, variant, baseVersionId: base, files: [{ role: 'main', path: `${id}.mp3` }], license: 'Apache-2.0', createdAt: now });
const alaunch = (jobId: string, status: string, base: string | null, prompt: string) =>
  ({ jobId, baseVersionId: base, at: now, status, initiator: 'human', prompt, license: 'Apache-2.0' });

const AUDIO = {
  focus: T, revision: 5,
  threads: [{
    id: T, file: null, name: 'Детская песенка', lineage: [], draftFolder: '', createdAt: now,
    versions: [aver('v1', 1, 'j1', 1, null), aver('v2', 2, 'j1', 2, null), aver('v3', 3, 'j2', 1, 'v2'), aver('v4', 4, 'j2', 2, 'v2')],
    currentVersionId: 'v4',
    launches: [alaunch('j1', 'done', null, 'весёлая детская песенка про ёжика'), alaunch('j2', 'done', 'v2', 'то же, но с колокольчиками'), alaunch('j3', 'running', 'v4', 'медленнее, колыбельная')],
    settings: { mode: 'music', operation: 'song', provider: 'local', model: 'ace-step', fields: null },
  }],
};

const iver = (id: string, number: number, jobId: string | null, variant: number | null) => ({
  id, number, jobId, variant, baseVersionId: jobId ? 'origin' : null, baseStepId: null,
  steps: jobId ? [`step-${id}`] : [], currentStepId: jobId ? `step-${id}` : null, createdAt: now,
});

const IMAGES = {
  focus: I, revision: 7,
  threads: [{
    id: I, file: null, lineage: [], draftFolder: '', stacks: [], currentStackId: null, currentStepId: null,
    settings: null, pendingJobId: null, createdAt: now,
    versions: [iver('origin', 0, null, null), iver('v1', 1, 'k1', 1), iver('v2', 2, 'k1', 2)],
    currentVersionId: 'v2',
    launches: [
      { jobId: 'k1', baseVersionId: null, baseStepId: null, at: now, status: 'done', initiator: 'human', prompt: 'ёжик с колокольчиком, акварель' },
      { jobId: 'k2', baseVersionId: 'v2', baseStepId: null, at: now, status: 'running', initiator: 'agent', prompt: 'тот же ёжик зимой' },
    ],
  }],
};

const record = (module: string, recordType: string, data: Record<string, unknown>, fallback: string, n: number) =>
  ({ kind: 'module_record', module, recordType, data, fallback, timestamp: at(n) });
const audioLaunch = (jobId: string, n: number) => record('audioeditor', 'audio_launch_versions', {
  threadId: T, jobId, mode: 'music', op: 'song', provider: 'local', model: 'ace-step', count: 2,
  price: { amount: 0, unit: 'free', approx: false }, license: 'Apache-2.0', initiator: 'human',
}, 'Вы запустили: песня · ace-step', n);
const imageLaunch = (jobId: string, prompt: string, initiator: string, n: number) => record('imageeditor', 'image_launch_versions', {
  threadId: I, jobId, prompt, model: 'qwen-image-2.1', count: 2, initiator,
  estimate: { amount: 0, unit: 'free', approx: false }, baseVersionId: jobId === 'k1' ? 'origin' : 'v2',
}, 'запуск картинки', n);

const HISTORY = [
  { kind: 'user_message', text: 'Сделай детскую песенку и картинку к ней', timestamp: at(0) },
  record('audioeditor', 'audio_thread', { threadId: T, versionId: null }, 'Звук: Детская песенка', 1),
  audioLaunch('j1', 2),
  audioLaunch('j2', 3),
  audioLaunch('j3', 4),
  record('imageeditor', 'image_thread', { threadId: I, versionId: 'origin' }, 'Картинка: новая', 5),
  imageLaunch('k1', 'ёжик с колокольчиком, акварель', 'human', 6),
  imageLaunch('k2', 'тот же ёжик зимой', 'agent', 7),
];

const SESSION = { id: S, mode: 'default', status: 'finished', messageCount: HISTORY.length, createdAt: now, updatedAt: now, name: 'Детская песенка', ownerId: 'u1' };

// Запросы, по которым видно, от какой версии пошло действие
const calls: { url: string; body: unknown }[] = [];

async function mockApi(page: Page) {
  await page.route('**/hubs/**', async (r: Route) => {
    if (r.request().url().includes('negotiate')) {
      return r.fulfill({ json: { negotiateVersion: 1, connectionId: 'c', connectionToken: 'c', availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }] } });
    }
    return r.fulfill({ status: 404, body: '' });
  });
  await page.routeWebSocket(/\/hubs\//, ws => {
    ws.onMessage(m => {
      if (typeof m === 'string' && m.includes('"protocol"')) ws.send('{}\u001e');
      if (typeof m === 'string') {
        for (const rec of m.split('\u001e')) {
          const id = /"invocationId":"([^"]+)"/.exec(rec)?.[1];
          if (id) ws.send(JSON.stringify({ type: 3, invocationId: id, result: null }) + '\u001e');
        }
      }
    });
  });
  await page.route('**/api/**', async (r: Route) => {
    const url = new URL(r.request().url());
    const p = url.pathname.replace(/^\/api/, '');
    const method = r.request().method();
    const json = (body: unknown) => r.fulfill({ json: body });
    if (method !== 'GET') calls.push({ url: p, body: r.request().postDataJSON() });
    if (p === '/auth/me') {
      return json({
        id: 'u1', username: 'admin', displayName: 'Андрей', role: 'admin', executionEnvironment: 'local',
        featureFlags: { 'audio-editor': true, 'image-editor': true }, subsystems: ['audioeditor', 'imageeditor'],
      });
    }
    if (p === '/subsystem-modules') {
      return json({ items: [
        { id: 'audioeditor', remoteUrl: '/audio-editor-remote/remoteEntry.js', exposedModule: './subsystem' },
        { id: 'imageeditor', remoteUrl: '/image-editor-remote/remoteEntry.js', exposedModule: './subsystem' },
      ] });
    }
    if (p === `/chats/${S}/history`) return json(HISTORY);
    if (p === `/chats/${S}`) return json(SESSION);
    if (p === '/chats' && method === 'GET') return json([SESSION]);
    // Звук
    if (p === `/audio-editor/chats/${S}/state`) return json({ threads: AUDIO, catalog: CATALOG, prefs: { voice: null, music: null, process: null } });
    if (p === `/audio-editor/chats/${S}/catalog`) return json(CATALOG);
    if (p === `/audio-editor/chats/${S}/prefs`) return json({ voice: null, music: null, process: null });
    if (p.startsWith(`/audio-editor/chats/${S}/threads`) && p.endsWith('/current')) {
      const { versionId } = r.request().postDataJSON() as { versionId: string };
      AUDIO.threads[0].currentVersionId = versionId;
      AUDIO.revision += 1;
      return json(AUDIO);
    }
    if (p.startsWith(`/audio-editor/chats/${S}/threads`) && method !== 'GET') return json(AUDIO);
    if (p.startsWith(`/audio-editor/chats/${S}/threads`) && method === 'GET' && !p.includes('/versions/')) return json(AUDIO);
    if (p === '/audio-editor/schema') {
      return json({ provider: url.searchParams.get('provider'), model: url.searchParams.get('model'), source: 'local-catalog', reserved: ['text'], fields: [] });
    }
    if (url.searchParams.get('download') === 'true') return r.fulfill({ contentType: 'audio/mpeg', headers: { 'content-disposition': 'attachment; filename="song.mp3"' }, body: Buffer.from('ID3') });
    if (p.includes('/peaks')) return json({ peaks: Array.from({ length: 200 }, (_, i) => Math.abs(Math.sin(i / 7))), seconds: 8 });
    if (p.startsWith(`/audio-editor/chats/${S}/jobs/`)) return json({ jobId: 'j3', status: 'Running', variants: [] });
    // Картинки
    if (p === `/image-editor/chats/${S}/threads/${I}/current`) {
      const { versionId } = r.request().postDataJSON() as { versionId: string };
      IMAGES.threads[0].currentVersionId = versionId;
      IMAGES.revision += 1;
      return json(IMAGES);
    }
    if (p.startsWith(`/image-editor/chats/${S}/threads`)) return json(IMAGES);
    if (p === `/image-editor/chats/${S}/catalog`) {
      return json({
        default: { provider: 'local', model: 'auto' },
        providers: [{ key: 'local', label: 'Локальные модели', priceUnit: 'free', models: [
          { id: 'auto', label: 'Авто' },
          { id: 'qwen-image-2.1', label: 'Qwen-Image 2.1', caps: { ops: ['generate', 'edit'], mask: 'none', maxReferences: 4, maxCount: 4, faceByReferences: false }, priceHint: { amount: 0, unit: 'free', per: 'image' } },
        ] }],
        limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null,
      });
    }
    if (p === `/image-editor/chats/${S}/prefs`) return json({});
    if (p.includes('/image-editor/') && p.includes('/steps/')) return r.fulfill({ contentType: 'image/png', body: PNG });
    if (p.includes('/image-editor/') && /\/jobs\/[^/]+$/.test(p)) {
      return json({ jobId: 'k2', status: 'running', phase: 'run', count: 2, variants: [], model: 'qwen-image-2.1', queuePosition: 1 });
    }
    const OBJ: Record<string, unknown> = {
      '/modules': { items: [] }, '/models': { models: [] }, '/settings': {}, '/usage': { snapshots: [] }, '/home/summary': {},
      '/chats/agents-presence': { agents: [], commands: [] }, '/watchdogs': { sessions: [], projects: [] },
      '/notifications/unread-count': { count: 0 },
    };
    if (p in OBJ) return json(OBJ[p]);
    if (process.env.FEED_TRACE) console.log('FALLBACK', method, p);
    return method === 'GET' ? json([]) : json({});
  });
}

// Шторка панели генерации на мобиле перекрывает ленту — опускаем её
async function closeSheet(page: Page) {
  const sheet = page.locator('[data-gen-sheet="sheet"]');
  if (!(await sheet.count())) return;
  await page.getByRole('button', { name: 'Опустить шторку' }).click();
  await expect(sheet).toHaveCount(0);
}

const audioCards = (page: Page) => page.locator('[data-audio-card]');
const imageCards = (page: Page) => page.locator('[data-image-version]');

for (const vp of [{ name: 'w1440', width: 1440, height: 1000 }, { name: 'm360', width: 360, height: 780 }]) {
  test(`карточка на каждый вариант — ${vp.name}`, async ({ page }) => {
    calls.length = 0;
    AUDIO.threads[0].currentVersionId = 'v4';
    IMAGES.threads[0].currentVersionId = 'v2';
    await page.setViewportSize({ width: vp.width, height: vp.height });
    await page.addInitScript(() => { localStorage.setItem('cc_token', 'e2e-token'); localStorage.setItem('theme-mode', 'light'); });
    await mockApi(page);
    const errors: string[] = [];
    page.on('pageerror', e => errors.push(e.message));
    if (process.env.FEED_TRACE) page.on('console', m => { if (m.type() === 'error') console.log('CONSOLE', m.text().slice(0, 600)); });
    await page.goto(`/#/chats/${S}`);
    const shot = async (name: string) => {
      if (!SHOTS) return;
      fs.mkdirSync(SHOTS, { recursive: true });
      await page.screenshot({ path: path.join(SHOTS, `${vp.name}-${name}.png`) });
    };

    // Звук: четыре варианта — четыре карточки на своих версиях, идущий запуск — одна карточка хода
    await expect(audioCards(page).first()).toBeVisible({ timeout: 30_000 });
    await expect.poll(async () => audioCards(page).evaluateAll(els => els.map(e => e.getAttribute('data-audio-card'))))
      .toEqual(['v1', 'v2', 'v3', 'v4', 'running']);
    await expect(page.locator('[data-audio-card="v4"]')).toHaveAttribute('data-current', 'true');
    await expect(page.locator('[data-audio-variant]')).toHaveCount(0);
    await expect(page.locator('[data-audio-card="draft"]')).toHaveCount(0);
    await expect(page.locator('[data-audio-card="v3"] [data-audio-nav]')).toHaveText('версия 3 · вариант 1 из 2');
    await expect(page.locator('[data-audio-card="running"]').getByRole('button', { name: 'Отменить' })).toHaveCount(1);
    // У каждой карточки — полный набор: «Работать с этой», «В контекст», «Скачать» (личный чат); меню «Обработать ▾» удалено
    for (const id of ['v1', 'v2', 'v3', 'v4']) {
      const c = page.locator(`[data-audio-card="${id}"]`);
      await expect(c.getByRole('button', { name: 'Работать с этой' }), id).toHaveCount(1);
      await expect(c.locator('[data-audio-process]'), id).toHaveCount(0);
      await expect(c.getByRole('button', { name: 'Скачать' }), id).toHaveCount(1);
    }
    await page.locator('[data-audio-card="v1"]').scrollIntoViewIfNeeded();
    await shot('audio-first');
    await page.locator('[data-audio-card="running"]').scrollIntoViewIfNeeded();
    await shot('audio-second');

    // Действия — от своей версии: «Работать с этой» у варианта 2 первого запуска
    await page.locator('[data-audio-card="v2"]').getByRole('button', { name: 'Работать с этой' }).click();
    await expect.poll(() => calls.filter(c => c.url.endsWith(`/threads/${T}/current`)).map(c => (c.body as { versionId: string }).versionId)).toEqual(['v2']);
    await expect(page.locator('[data-audio-card="v2"]')).toHaveAttribute('data-current', 'true');
    await expect(page.locator('[data-audio-card="v4"]')).toHaveAttribute('data-current', 'false');
    // «Работать с этой» открывает панель «Звук»; на мобиле это шторка поверх ленты — закрываем
    await closeSheet(page);
    // «Скачать» у варианта 1 второго запуска — файл его версии
    const dl = page.waitForEvent('download');
    await page.locator('[data-audio-card="v3"]').getByRole('button', { name: 'Скачать' }).click();
    expect(new URL((await dl).url()).pathname).toBe(`/api/audio-editor/chats/${S}/threads/${T}/versions/v3/files/main`);
    // Картинки: два варианта — две карточки во всю ширину, идущий запуск — одна карточка хода
    await expect.poll(async () => imageCards(page).evaluateAll(els => els.map(e => e.getAttribute('data-image-version'))))
      .toEqual(['1', '2', 'running']);
    await expect(page.locator('[data-image-version="2"]')).toHaveAttribute('data-current', 'true');
    await expect(page.locator('[data-image-version="1"]')).toContainText('«ёжик с колокольчиком, акварель»');
    const widths = await imageCards(page).evaluateAll(els => els.map(e => Math.round(e.getBoundingClientRect().width)));
    expect(new Set(widths).size, `ширины ${widths.join(', ')}`).toBe(1);
    await expect(page.locator('[data-image-version="running"]').getByRole('button', { name: 'Отменить' })).toHaveCount(1);
    await page.locator('[data-image-version="1"]').scrollIntoViewIfNeeded();
    await shot('images');
    await page.locator('[data-image-version="1"]').getByRole('button', { name: 'Продолжить от неё' }).click();
    await expect.poll(() => calls.filter(c => c.url.endsWith(`/threads/${I}/current`)).map(c => (c.body as { versionId: string }).versionId)).toEqual(['v1']);

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    expect(errors, errors.join('\n')).toEqual([]);
  });
}
