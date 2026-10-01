import { test, expect, type Page, type Route } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Интерфейс звука после слияния веток: один личный чат — полоса «Звук», панель во всех режимах
// и вкладка «Голоса», карточка нити, якоря запусков и карточки вызовов агента. Запуск, который
// нарисован якорем audio_launch_versions, второй карточкой audio_generate не дублируется.
// Бэкенд не нужен: собранный dist (с remote «Звук») раздаётся статикой, /api/** и хаб — моки.
//
//   (cd dist && python3 -m http.server 5197) &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5197 AUDIO_SHOTS_DIR=../.cc-attachments/audio-ui-merged \
//     npx playwright test e2e/audio-ui-merged.spec.ts

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
    models: [
      { id: 'qwen3-tts', label: 'Qwen3-TTS 1.7B', caps: { ops: ['speak'], languages: ['ru'], voiceKinds: ['preset'], producesFiles: ['main'], license: { label: 'Apache-2.0', kind: 'permissive' }, priceUnit: 'free' } },
      { id: 'ace-step', label: 'ACE-Step 1.5', caps: { ops: ['song', 'separate'], languages: ['ru'], voiceKinds: [], producesFiles: ['main'], license: { label: 'Apache-2.0', kind: 'permissive' }, priceUnit: 'free', minDurationSec: 10, maxDurationSec: 240 } },
    ],
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

const record = (recordType: string, data: Record<string, unknown>, fallback: string) =>
  ({ kind: 'module_record', module: 'audioeditor', recordType, data, fallback, timestamp: Date.parse(now) + 500 });
const anchor = (jobId: string, provider: string, model: string) => record('audio_launch_versions', {
  threadId: T, jobId, mode: 'voice', op: 'speak', provider, model, count: jobId === 'job-done' ? 2 : 1,
  price: { amount: 0, unit: 'free', approx: false }, license: 'Apache-2.0', initiator: 'agent', baseVersionId: 'v1',
}, `Claude запустил: озвучка · ${model}`);
HISTORY.splice(1, 0, record('audio_thread', { threadId: T }, 'Звук: podcast-intro.mp3'));
HISTORY.splice(HISTORY.length - 2, 0,
  anchor('job-done', 'local', 'qwen3-tts'), anchor('job-run', 'fal', 'fal-ai/minimax/speech-02-hd'), anchor('job-fail', 'fal', 'fal-ai/minimax/speech-02-hd'));

// Схемы моделей: у голосовой модели ключ key — свой параметр, в «Голосе» он в «Дополнительно»
const SCHEMA: Record<string, unknown[]> = {
  'qwen3-tts': [{ key: 'speaker', type: 'string', enum: ['Vivian', 'Eric'] }, { key: 'key', type: 'string', title: 'Ключ стиля' }],
  'ace-step': [{ key: 'bpm', type: 'integer', min: 40, max: 220 }, { key: 'key', type: 'string' }],
};

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
    if (p === '/audio-editor/schema') {
      const model = url.searchParams.get('model') || '';
      return json({ provider: url.searchParams.get('provider'), model, source: 'local-catalog', reserved: ['text'], fields: SCHEMA[model] ?? [] });
    }
    if (p === `/audio-editor/chats/${S}/catalog`) return json(CATALOG);
    if (p === `/audio-editor/chats/${S}/prefs`) return json({ voice: null, music: null, process: null });
    if (p === `/audio-editor/chats/${S}/threads`) return json(THREADS);
    // Настройки нити сервер принимает как есть — иначе ответ откатил бы выбор режима в панели
    if (/\/threads\/[^/]+\/settings$/.test(p)) {
      const { settings } = r.request().postDataJSON() as { settings: unknown };
      const t = THREADS.threads.find(x => p.includes(`/threads/${x.id}/`)) as { settings: unknown } | undefined;
      if (t) t.settings = settings;
      THREADS.revision += 1;
      return json(THREADS);
    }
    if (/\/threads\/[^/]+\/current$/.test(p) || p.endsWith('/threads/focus')) return json(THREADS);
    if (p.includes('/peaks')) return json({ peaks: Array.from({ length: 200 }, (_, i) => Math.abs(Math.sin(i / 7))), seconds: 8 });
    if (p === `/audio-editor/chats/${S}/quote`) {
      return json({ quoteId: 'q1', mode: 'voice', op: 'speak', provider: 'local', model: 'qwen3-tts', count: 1, voiceKind: null,
        price: { amount: 0, unit: 'free', approx: false }, license: 'Apache-2.0', heavy: false, expiresAt: '2099-01-01T00:00:00Z' });
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


const closeToasts = async (page: Page) => {
  const close = page.locator('[data-cc-src*="NotificationToasts"] [title="Закрыть"]');
  for (let i = 0; i < 5 && await close.count(); i++) await close.first().click().catch(() => {});
};

for (const vp of [{ name: 'w1440', width: 1440, height: 1000 }, { name: 'm360', width: 360, height: 780 }]) {
  test(`звук в одном чате — ${vp.name}`, async ({ page }) => {
    const mobile = vp.width < 500;
    await page.setViewportSize({ width: vp.width, height: vp.height });
    await page.addInitScript(() => { localStorage.setItem('cc_token', 'e2e-token'); localStorage.setItem('theme-mode', 'light'); });
    await mockApi(page);
    const errors: string[] = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.goto(`/#/chats/${S}`);
    const shot = async (name: string) => {
      if (!SHOTS) return;
      fs.mkdirSync(SHOTS, { recursive: true });
      await page.screenshot({ path: path.join(SHOTS, `${vp.name}-${name}.png`) });
    };

    // Карточка нити и якоря запусков
    await expect(page.locator('[data-audio-card="thread"], [data-audio-nav]').first()).toBeVisible({ timeout: 30_000 });
    await closeToasts(page);
    await expect(page.locator('[data-audio-launch]')).toHaveCount(3);
    await expect(page.locator('[data-audio-launch="done"] [data-audio-variant]')).toHaveCount(2);

    // Карточки агента на месте, а запуски, нарисованные якорями, второй карточкой не повторяются
    await expect(page.getByText('Claude взял в работу')).toBeVisible();
    await expect(page.getByText('Лимит запусков за ход')).toBeVisible();
    await expect(page.getByText('Запуск недоступен на чужом ходу')).toBeVisible();
    await expect(page.getByText('Склеено: склейка-реплик.wav')).toBeVisible();
    await expect(page.locator('[data-audio-card="launch"]')).toHaveCount(2);
    await expect(page.getByText('Готово: Озвучить')).toHaveCount(0);
    await expect(page.getByText('Не получилось: Озвучить')).toHaveCount(0);
    await expect(page.getByText('Claude — запуск: Озвучить')).toHaveCount(0);
    await page.locator('[data-audio-launch="done"]').scrollIntoViewIfNeeded();
    await shot('feed');

    // Полоса «Звук» над полем ввода
    const strip = page.locator('[data-composer-strip="sound"]');
    await expect(strip).toBeVisible();
    // Свёрнутая полоса разворачивается кликом по заголовку (сводка открыла бы панель)
    if (await strip.getAttribute('data-sound-strip') === 'mini') await strip.click({ position: { x: 12, y: 15 } });
    await expect(page.locator('[data-sound-strip="full"]')).toBeVisible();
    await shot('strip');

    // Панель: «Настройки» во всех режимах
    await page.locator('[data-sound-settings-toggle] button').click();
    const p = page.locator('[data-sound-settings]');
    await expect(p).toBeVisible();
    await p.getByRole('button', { name: 'Голос', exact: true }).click();
    await expect(p.locator('[data-opt="op:speak"]')).toBeVisible();
    // Ключ key у голосовой модели — не «Тональность» на виду, а свой параметр в «Дополнительно»
    await expect(p.locator('[data-param="speaker"]')).toBeVisible();
    await expect(p.getByText('Тональность')).toHaveCount(0);
    await p.getByRole('button', { name: /^Дополнительно/ }).click();
    await expect(p.locator('[data-sound-advanced="open"]').getByText('Ключ стиля')).toBeVisible();
    await shot('panel-voice');

    await p.getByRole('button', { name: 'Музыка', exact: true }).click();
    await p.locator('[data-opt="op:song"] button').click();
    await expect(p.locator('[data-param="bpm"]')).toBeVisible();
    await expect(p.getByText('Тональность')).toBeVisible();
    await shot('panel-music');

    await p.getByRole('button', { name: 'Обработка', exact: true }).click();
    await expect(p.locator('[data-opt^="op:"]').first()).toBeVisible();
    await shot('panel-process');

    // Вкладка «Голоса»: у личного чата библиотеки нет — понятный отказ, без падения
    await page.getByRole('tab', { name: 'Голоса' }).or(page.getByRole('button', { name: 'Голоса', exact: true })).first().click();
    await expect(p).toHaveCount(0);
    await shot('panel-voices');

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(overflow).toBeLessThanOrEqual(0);
    expect(errors, errors.join('\n')).toEqual([]);
    void mobile;
  });
}
