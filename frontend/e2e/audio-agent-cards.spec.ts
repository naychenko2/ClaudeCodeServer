import { test, expect, type Page, type Route } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Карточки вызовов агента audio_* в ленте (задача 6.3). Бэкенд не нужен: собранный dist
// (с remote «Звук») раздаётся статикой, а /api/** и хаб — моки. История личного чата —
// фикстура с вызовами всех восьми инструментов, включая отказы лимита и делегированного
// хода. Снимки — десктоп и 360 px, светлая и тёмная темы.
//
//   (cd dist && python3 -m http.server 5197) &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5197 AUDIO_SHOTS_DIR=../.cc-attachments/audio-agent-cards \
//     npx playwright test e2e/audio-agent-cards.spec.ts

const SHOTS = process.env.AUDIO_SHOTS_DIR || '';
const S = 'chat-audio-1';
const T = 'thread-intro';
const T2 = 'thread-concat';
const tool = (name: string) => `mcp__audio-editor__${name}`;
const now = new Date('2026-10-01T15:00:00Z').toISOString();

const CATALOG = {
  autoModelId: 'auto', maxCount: 4,
  providers: [{
    key: 'local', label: 'Локально', priceUnit: 'free', available: true, reason: null,
    models: [{ id: 'qwen3-tts', label: 'Qwen3-TTS 1.7B', caps: { ops: ['speak'], languages: ['ru'], voiceKinds: ['preset'], producesFiles: ['main'], license: { label: 'Apache-2.0', kind: 'permissive' }, priceUnit: 'free' } }],
  }, {
    key: 'fal', label: 'fal.ai', priceUnit: 'usd', available: true, reason: null,
    models: [{ id: 'fal-ai/minimax/speech-02-hd', label: 'MiniMax Speech 02 HD', caps: { ops: ['speak'], languages: ['ru'], voiceKinds: ['preset'], producesFiles: ['main'], license: { label: 'коммерческая', kind: 'permissive' }, priceUnit: 'chars' } }],
  }],
};

const version = (id: string, number: number, jobId: string | null) =>
  ({ id, number, jobId, variant: jobId ? number : null, baseVersionId: jobId ? 'origin' : null, files: [{ role: 'main', path: 'audio/intro.mp3' }], license: null, createdAt: now });

const THREADS = {
  focus: T, revision: 3,
  threads: [{
    id: T, file: 'audio/podcast-intro.mp3', lineage: [], draftFolder: null, createdAt: now,
    versions: [version('origin', 0, null), version('v1', 1, 'job-done'), version('v2', 2, 'job-done')],
    currentVersionId: 'v1',
    launches: [
      { jobId: 'job-done', baseVersionId: 'origin', at: now, status: 'done', initiator: 'agent', prompt: null, license: null },
      { jobId: 'job-run', baseVersionId: 'v1', at: now, status: 'running', initiator: 'agent', prompt: null, license: null },
      { jobId: 'job-fail', baseVersionId: 'v1', at: now, status: 'failed', initiator: 'agent', prompt: null, license: null },
    ],
    settings: { mode: 'voice', operation: 'speak', provider: 'local', model: 'qwen3-tts', fields: null },
  }, {
    id: T2, file: null, name: 'склейка-реплик.wav', lineage: [], draftFolder: '', createdAt: now,
    versions: [version('c1', 1, null)], currentVersionId: 'c1', launches: [], settings: null,
  }],
};

const launch = (jobId: string, provider: string, model: string, price: Record<string, unknown>, count = 1) => JSON.stringify({
  jobId, threadId: T, baseVersion: { versionId: 'v1', label: 'версия 1' },
  quote: { provider, model, op: 'speak', count, price, license: 'Apache-2.0' }, note: '…',
});

let n = 0;
const call = (name: string, input: Record<string, unknown>, result: string, isError = false) => {
  n++;
  // В истории результат хранится прямо в записи tool_use
  return [{ kind: 'tool_use', id: `tu${n}`, name: tool(name), input, result, isError, timestamp: Date.parse(now) + n * 1000 }];
};

const HISTORY = [
  { kind: 'user_message', text: 'Озвучь вступление подкаста и склей реплики', timestamp: Date.parse(now) },
  ...call('audio_state', {}, JSON.stringify({ threads: [{}, {}], catalog: CATALOG })),
  ...call('audio_voices', { language: 'ru' }, JSON.stringify({ providers: [{ provider: 'local', voices: Array(12).fill({}) }], library: [{}, {}] })),
  ...call('audio_focus', { threadId: T }, JSON.stringify({ focus: T, thread: { threadId: T, file: 'audio/podcast-intro.mp3', currentVersionId: 'v1', versions: [{ versionId: 'v1', label: 'версия 1' }] } })),
  ...call('audio_new', { mode: 'voice' }, JSON.stringify({ focus: 'thread-new', thread: { threadId: 'thread-new', name: 'Новый звук', versions: [] } })),
  ...call('audio_suggest_prompt', { prompt: 'Добрый вечер! В эфире «Тёплый звук» — подкаст о том, как звучат города.', mode: 'voice', model: 'qwen3-tts' }, '{}'),
  ...call('audio_generate', { threadId: T }, launch('job-done', 'local', 'qwen3-tts', { amount: 0, unit: 'free', approx: false }, 2)),
  ...call('audio_generate', { threadId: T }, launch('job-run', 'fal', 'fal-ai/minimax/speech-02-hd', { amount: 0.12, unit: 'usd', approx: true })),
  ...call('audio_generate', { threadId: T }, launch('job-fail', 'fal', 'fal-ai/minimax/speech-02-hd', { amount: 0.12, unit: 'usd', approx: true })),
  ...call('audio_generate', { threadId: T }, 'За один ход можно запустить не больше 2 операций со звуком. Покажи человеку, что уже получилось, и дождись его ответа.', true),
  ...call('audio_generate', { threadId: T }, 'Запуск операции со звуком недоступно на делегированном ходу: этот ход инициирован другим чатом, и цепочка делегирования дальше не идёт. Верни результат тому, кто тебя позвал — решение примет он или пользователь.', true),
  ...call('audio_concat', { pieces: [{ threadId: T }, { threadId: 'a' }, { file: 'audio/outro.wav' }] }, JSON.stringify({ threadId: T2, versionId: 'c1', name: 'склейка-реплик.wav' })),
  ...call('audio_cancel', { jobId: 'job-run' }, JSON.stringify({ jobId: 'job-run', status: 'cancelled', charged: false, variants: 0 })),
  { kind: 'text', text: 'Готово: две версии вступления и склейка реплик.', timestamp: Date.parse(now) + 60_000 },
  { kind: 'result', subtype: 'success', numTurns: 12, durationMs: 1000, timestamp: Date.parse(now) + 61_000 },
];

const SESSION = {
  id: S, mode: 'default', status: 'finished', messageCount: HISTORY.length, createdAt: now, updatedAt: now,
  name: 'Озвучка подкаста', ownerId: 'u1',
};

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
      // Вызовы хаба: отвечаем пустым завершением, чтобы клиент не ждал
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
    if (p === '/auth/me') {
      return json({
        id: 'u1', username: 'admin', displayName: 'Андрей', role: 'admin', executionEnvironment: 'local',
        featureFlags: { 'audio-editor': true }, subsystems: ['audioeditor'],
      });
    }
    if (p === '/subsystem-modules') return json({ items: [{ id: 'audioeditor', remoteUrl: '/audio-editor-remote/remoteEntry.js', exposedModule: './subsystem' }] });
    if (p === `/chats/${S}/history`) return json(HISTORY);
    if (p === `/chats/${S}`) return json(SESSION);
    if (p === '/chats' && method === 'GET') return json([SESSION]);
    if (p === `/audio-editor/chats/${S}/state`) {
      return json({ threads: THREADS, catalog: CATALOG, prefs: { voice: null, music: null, process: null } });
    }
    if (p.startsWith(`/audio-editor/chats/${S}/jobs/`)) {
      return json({ jobId: 'job-fail', status: 'Failed', outcome: 'failed', charged: false, error: 'Сервис озвучки ответил 503', variants: [] });
    }
    const OBJ: Record<string, unknown> = {
      '/modules': { items: [] }, '/models': { models: [] }, '/settings': {}, '/usage': { snapshots: [] }, '/home/summary': {},
      '/chats/agents-presence': { agents: [], commands: [] }, '/watchdogs': { sessions: [], projects: [] },
      '/notifications/unread-count': { count: 0 },
    };
    if (p in OBJ) return json(OBJ[p]);
    if (process.env.AUDIO_E2E_TRACE) console.log('FALLBACK', method, p);
    return method === 'GET' ? json([]) : json({});
  });
}

for (const theme of ['light', 'dark'] as const) {
  for (const vp of [{ name: 'desktop', width: 1280, height: 2200 }, { name: 'm360', width: 360, height: 2600 }]) {
    test(`карточки audio_* — ${vp.name}, ${theme}`, async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.addInitScript(th => {
        localStorage.setItem('cc_token', 'e2e-token');
        localStorage.setItem('theme-mode', th as string);
      }, theme);
      await page.emulateMedia({ colorScheme: theme });
      await mockApi(page);
      page.on('pageerror', e => console.log('PAGEERROR', e.message));
      await page.goto(`/#/chats/${S}`);

      // Каждая карточка — на месте, сырого JSON и текстов, адресованных агенту, в ленте нет
      await expect(page.getByText('Claude взял в работу')).toBeVisible({ timeout: 30_000 });
      await expect(page.locator('[data-audio-sysline="focus"]').getByRole('button', { name: 'podcast-intro.mp3 · версия 1' })).toBeVisible();
      await expect(page.getByText('Claude завёл новый звук (голос)')).toBeVisible();
      await expect(page.getByText('Звуки чата: 2 звука')).toBeVisible();
      await expect(page.getByText('Дикторы (ru): 12 дикторов, голосов проекта: 2')).toBeVisible();
      await expect(page.getByRole('button', { name: 'Вставить в промпт' })).toBeVisible();
      await expect(page.getByRole('button', { name: /Сгенерировать · бесплатно/ })).toBeVisible();
      // Запуски, которые знает нить, рисует якорь audio_launch_versions (карточки вариантов) — не эта карточка
      // (вместе с якорями — e2e/audio-ui-merged.spec.ts)
      await expect(page.getByText('Готово: Озвучить')).toHaveCount(0);
      await expect(page.getByText('Claude — запуск: Озвучить')).toHaveCount(0);
      await expect(page.getByText('Не получилось: Озвучить')).toHaveCount(0);
      await expect(page.getByText('Лимит запусков за ход')).toBeVisible();
      await expect(page.getByText('Запуск недоступен на чужом ходу')).toBeVisible();
      await expect(page.getByText('Склеено: склейка-реплик.wav')).toBeVisible();
      await expect(page.getByText(/Операция со звуком отменена/)).toBeVisible();
      await expect(page.getByText('Верни результат')).toHaveCount(0);
      await expect(page.getByText('"jobId"')).toHaveCount(0);

      // Ширина: ничего не вылезает за ленту на 360
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);

      if (SHOTS) {
        fs.mkdirSync(SHOTS, { recursive: true });
        await page.screenshot({ path: path.join(SHOTS, `${vp.name}-${theme}.png`), fullPage: true });
      }
    });
  }
}
