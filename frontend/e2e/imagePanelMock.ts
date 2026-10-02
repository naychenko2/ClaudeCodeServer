import { expect, type Locator, type Page, type Route, type WebSocketRoute } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Общий мок спек полосы и панели «Картинки» (image-panel-v5.spec.ts — с флагом, image-panel-legacy.spec.ts
// — без него). Бэкенд не нужен: собранный dist раздаётся статикой, /api/** и хабы — моки.
// Мир один на тест и общий для всех вкладок контекста: нити, лента (записи модуля), задачи,
// котировки и сохранения. Запись нитей рассылается событием хаба image_thread_changed всем
// вкладкам, как на бою.

export const P = 'proj-img';
export const S = 'chat-img';
export const H = 'thread-hero';
export const D = 'thread-draft';
export const CAT = 'thread-cat';
export const now = new Date('2026-10-02T10:00:00Z').toISOString();
export const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
// hero.png 320×240 (небо, солнце, земля): по ней кисть рисует маску, у PNG 1×1 места под мазок нет
export const HERO = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAUAAAADwCAIAAAD+Tyo8AAACy0lEQVR42u3TUQkAIBBEQXOaxBCG8NskhrgQRrCECCcDk2DZV9oMuG7H4IHiaghYwCBgASNgBIyABQwCFjACRsAIGAEjYAGDgAWMgBEwAhYwCFjAIGABI2AEjIAFDAIWMAJGwAgYASNgAYOABYyAETACFjAIWMAIWF0CRsAIGAELGAQsYASMgBEwAkbAAgYBCxgBI2AELGAQsIARMAJGwAgYAQsYBCxgBIyAEbCAvQ0BCxgELGAEjIARsIBBwAJGwAgYASNgBCxgELCAETACRsACBgELGAQsYASMgBGwgEHAAkbACBgBI2AELGAQsIARMAJGwAIGAQsYASNgBIyAEbCAQcACRsAIGAEjYAQsYBCwgBEwAkbAAgYBCxgBI2AEjIARsIBBwAJGwAgYAQsYBCxgELCAETACRsACBgELGAEjYASMgBGwgEHAAkbACBgBCxgELGAQsIARMAJGwAIGAQsYASNgBIyAEbCAQcACRsAIGAELGAQsYASMgBEwAkbAAgYBCxgBI2AEjIARsIBBwAJGwAgYAQsYBCxgBIyAETACRsACBgELGAEjYAQsYBCwgEHAAkbACBgBCxgELGAEjIARMAJGwAIGAQsYASNgBCxgELCAQVoCRsAIGAELGAQsYASMgBEwAkbAAgYBCxgBI2AELGAQsIARMAJGwAgYAQsYBCxgBIyAEbCAvQ0B5w149QokJWAQMCBgQMAgYEDAgIABAYOAAQEDAgYBAwIGBAwIGAQMCBgQMCBgEDAgYEDAIGBAwICAAQGDgAEBAwIGBAwCBgQMCBgEDAgYEDAgYBAwIGBAwCBgK4CAAQEDAgYBAwIGBAwIGAQMCBgQMAgYEDAgYEDAIGBAwICAAQGDgAEBAwIGAQMCBgQMCBgEDAgYEDAgYBAwIGBAwCBgQMCAgAEBg4ABAQMCBgEDAgYEDAgYBAwIGBAwIGD4xwF2D3CwePmopAAAAABJRU5ErkJggg==', 'base64');

const caps = (ops: string[], mask = 'none', maxReferences = 4) => ({ ops, mask, maxReferences, maxCount: 4, faceByReferences: false });
export const FILL = 'fal-ai/flux-pro/v1/fill';
export const CATALOG = {
  default: { provider: 'local', model: 'auto' },
  providers: [
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, models: [
      { id: 'auto', label: 'Авто' },
      { id: 'qwen-image-2.1', label: 'Qwen-Image 2.1', caps: caps(['generate', 'edit', 'inpaint', 'outpaint', 'removeBackground', 'upscale'], 'asReference', 3), priceHint: { amount: 0, unit: 'free', per: 'image' } },
      { id: 'face-detailer', label: 'Улучшить лица', caps: { ...caps(['enhanceFaces'], 'none', 0), maxCount: 1 }, priceHint: { amount: 0, unit: 'free', per: 'image' } },
    ] },
    { key: 'fal', label: 'fal', priceUnit: 'usd', models: [
      { id: 'auto', label: 'Авто' },
      { id: 'fal-ai/flux-pro/kontext', label: 'FLUX Kontext', caps: caps(['generate', 'edit']), priceHint: { amount: 0.04, unit: 'usd', per: 'image' } },
      { id: FILL, label: 'FLUX Fill', caps: caps(['edit', 'inpaint'], 'native'), priceHint: { amount: 0.08, unit: 'usd', per: 'image' } },
      { id: 'fal-ai/birefnet', label: 'BiRefNet', caps: caps(['removeBackground', 'upscale'], 'none', 0), priceHint: { amount: 0.01, unit: 'usd', per: 'image' } },
    ] },
    { key: 'higgsfield', label: 'Higgsfield', priceUnit: 'credits', models: [
      { id: 'soul_2', label: 'Soul', caps: caps(['generate']), priceHint: { amount: 2, unit: 'credits', per: 'image' } },
    ] },
  ],
  limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null,
};

export type Launch = { jobId: string; baseVersionId: string | null; baseStepId: string | null; at: string; status: string; initiator: 'human' | 'agent'; prompt: string | null };
export type Version = { id: string; number: number; jobId: string | null; variant: number | null; baseVersionId: string | null; baseStepId: string | null; steps: string[]; currentStepId: string | null; createdAt: string };
export interface Thread {
  id: string; file: string | null; lineage: string[]; draftFolder: string | null; stacks: never[]; currentStackId: null;
  currentStepId: string | null; settings: null; pendingJobId: null; createdAt: string; versions: Version[]; currentVersionId: string; launches: Launch[];
}
const origin: Version = { id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: now };
export const base = (id: string, file: string | null, at = now, draftFolder = 'images'): Thread => ({
  id, file, lineage: [], draftFolder: file ? null : draftFolder, stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: at, versions: [{ ...origin }], currentVersionId: 'origin', launches: [],
});
export const heroThread = () => base(H, 'images/hero.png', '2026-10-02T09:00:00Z');
// Кот, которого нарисовал агент: версия 1 с шагом
export const catThread = (): Thread => ({
  ...base(CAT, null, '2026-10-02T09:30:00Z'),
  versions: [{ ...origin }, { id: 'v1', number: 1, jobId: 'job-cat', variant: 0, baseVersionId: null, baseStepId: null, steps: ['st-cat'], currentStepId: 'st-cat', createdAt: '2026-10-02T09:31:00Z' }],
  currentVersionId: 'v1',
  launches: [{ jobId: 'job-cat', baseVersionId: null, baseStepId: null, at: '2026-10-02T09:30:30Z', status: 'done', initiator: 'agent', prompt: 'рыжий кот на подоконнике' }],
});

// Запись модуля в ленте — как её пишет сервер (StoredModuleRecord)
export const record = (recordType: string, data: Record<string, unknown>, at = Date.parse(now)) =>
  ({ kind: 'module_record', module: 'imageeditor', recordType, data, fallback: '', timestamp: at });
export const anchor = (threadId: string, at?: number) => record('image_thread', { threadId, versionId: 'origin' }, at);

type Job = { op: string | null; prompt: string | null; threadId: string | null; fields: string[]; aspectRatio: string | null; jobId: string };

export interface World {
  focus: string | null;
  revision: number;
  threads: Thread[];
  // Лента чата кроме первого сообщения человека
  feed: Record<string, unknown>[];
  jobs: Job[];
  quotes: Record<string, unknown>[];
  saves: Record<string, unknown>[];
  cancelled: string[];
  // Задача сразу готова версией в нити (полоса) или рисуется, пока тест её не закончит
  autoFinish: boolean;
  // Готовые задачи: jobId → варианты
  finished: Map<string, number[]>;
  // Занятые пути проекта — для «Сохранить как…»
  taken: Set<string>;
  hubs: WebSocketRoute[];
  // Вызовы хаба (SendMessage и прочие) и загруженные вложения чата
  invocations: { target: string; args: unknown[] }[];
  uploads: string[];
  flags: Record<string, boolean>;
  personal: boolean;
}

let world: World;
export const w = () => world;

export function newWorld(o: { focus: string | null; threads: Thread[]; v5: boolean; personal?: boolean; autoFinish?: boolean; feed?: Record<string, unknown>[] }): World {
  world = {
    focus: o.focus, revision: 1, threads: o.threads, feed: o.feed ?? [], jobs: [], quotes: [], saves: [], cancelled: [],
    autoFinish: o.autoFinish ?? true, finished: new Map(), taken: new Set(['images/generated/hero.v2.png']), hubs: [], invocations: [], uploads: [],
    flags: { 'image-editor': true, 'image-panel-v5': o.v5 }, personal: !!o.personal,
  };
  return world;
}

// Сообщение хаба message всем вкладкам
export function hubSend(msg: Record<string, unknown>) {
  for (const h of world.hubs) h.send(JSON.stringify({ type: 1, target: 'message', arguments: [msg] }) + '\u001e');
}

export function pushThreads() {
  hubSend({ type: 'image_thread_changed', sessionId: S, projectId: world.personal ? null : P, revision: world.revision, state: snapshot() });
}

// Запись модуля в ленту: в историю и живым событием
export function pushRecord(rec: ReturnType<typeof record>) {
  world.feed.push(rec);
  hubSend({ type: 'module_record', sessionId: S, module: rec.module, recordType: rec.recordType, data: rec.data, fallback: rec.fallback, timestamp: rec.timestamp });
}

const snapshot = () => ({ focus: world.focus, revision: world.revision, threads: world.threads });

// Задача закончилась: варианты — версии нити, запуск — done
export function finishJob(jobId: string, variants = [0]) {
  world.finished.set(jobId, variants);
  const t = world.threads.find(x => x.launches.some(l => l.jobId === jobId));
  if (t) {
    const l = t.launches.find(x => x.jobId === jobId)!;
    l.status = 'done';
    for (const v of variants) {
      const n = t.versions.length;
      t.versions.push({ id: `v${n}`, number: n, jobId, variant: v, baseVersionId: null, baseStepId: null, steps: [`st-${jobId}-${v}`], currentStepId: `st-${jobId}-${v}`, createdAt: l.at });
      t.currentVersionId = `v${n}`;
    }
    world.revision++;
    pushThreads();
  }
}

const SESSION_OF = () => ({
  id: S, projectId: world.personal ? null : P, mode: 'default', status: 'finished', messageCount: 1, createdAt: now, updatedAt: now,
  name: world.personal ? 'Мои картинки' : 'Картинки для сайта', ownerId: 'u1',
});
const PROJECT = { id: P, name: 'Сайт студии', rootPath: '/tmp/img', createdAt: now, updatedAt: now, ownerId: 'u1' };
const PREFS = {
  provider: null, model: null, count: 2, matchSourceSize: true, characterSlug: null,
  create: { provider: null, model: null, count: 2 },
  edit: { provider: null, model: null, count: 2, op: 'edit', editMode: null, ratio: null },
};
const TREE = [
  { name: 'assets', path: 'assets', isDirectory: true, modified: now, isModified: false },
  { name: 'images', path: 'images', isDirectory: true, modified: now, isModified: false },
  { name: 'blog', path: 'images/blog', isDirectory: true, modified: now, isModified: false },
  { name: 'generated', path: 'images/generated', isDirectory: true, modified: now, isModified: false },
  ...['assets/palette.png', 'assets/logo.png', 'images/hero.png', 'images/описание.txt', 'images/generated/hero.v2.png'].map(fp => ({ name: fp.split('/').pop(), path: fp, isDirectory: false, modified: now, isModified: false })),
];

function jobOf(jobId: string) {
  const done = world.finished.get(jobId);
  const base0 = { jobId, projectId: P, provider: 'fal', model: FILL, createdAt: now, variants: [] as number[] };
  if (world.cancelled.includes(jobId)) return { ...base0, status: 'cancelled', outcome: 'cancelled', charged: false };
  if (done) return { ...base0, status: 'completed', outcome: 'ok', charged: true, variants: done, cost: { amount: 0.08 * done.length, unit: 'usd' } };
  return { ...base0, status: 'running' };
}

export async function mockApi(page: Page, trace = false) {
  await page.route('**/hubs/**', async (r: Route) => {
    if (r.request().url().includes('negotiate')) {
      return r.fulfill({ json: { negotiateVersion: 1, connectionId: 'c', connectionToken: 'c', availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }] } });
    }
    return r.fulfill({ status: 404, body: '' });
  });
  await page.routeWebSocket(/\/hubs\//, ws => {
    if (ws.url().includes('/hubs/session')) world.hubs.push(ws);
    ws.onMessage(m => {
      if (typeof m === 'string' && m.includes('"protocol"')) ws.send('{}\u001e');
      if (typeof m === 'string') {
        for (const rec of m.split('\u001e')) {
          const id = /"invocationId":"([^"]+)"/.exec(rec)?.[1];
          if (!id) continue;
          const inv = JSON.parse(rec) as { target?: string; arguments?: unknown[] };
          if (inv.target) world.invocations.push({ target: inv.target, args: inv.arguments ?? [] });
          ws.send(JSON.stringify({ type: 3, invocationId: id, result: inv.target === 'SendMessage' ? 'started' : null }) + '\u001e');
        }
      }
    });
  });
  await page.route('**/api/**', async (r: Route) => {
    const url = new URL(r.request().url());
    const p = url.pathname.replace(/^\/api/, '');
    const method = r.request().method();
    const json = (body: unknown) => r.fulfill({ json: body });
    const session = SESSION_OF();
    // Область: проект — …/projects/P/image-editor, личный чат — …/image-editor/chats/S
    const ie = world.personal ? `/image-editor/chats/${S}` : `/projects/${P}/image-editor`;
    if (p === '/auth/me') {
      return json({
        id: 'u1', userId: 'u1', username: 'admin', displayName: 'Андрей', role: 'admin', executionEnvironment: 'local',
        featureFlags: world.flags, subsystems: ['imageeditor'],
      });
    }
    if (p === '/subsystem-modules') {
      return json({ items: [{ id: 'imageeditor', remoteUrl: '/image-editor-remote/remoteEntry.js', exposedModule: './subsystem' }] });
    }
    if (p === '/projects' && method === 'GET') return json(world.personal ? [] : [PROJECT]);
    if (p === `/projects/${P}`) return json(PROJECT);
    if (p === `/chats/${S}/history` || p === `/projects/${P}/sessions/${S}/history`) return json([{ kind: 'user_message', text: 'Нужны картинки для сайта', timestamp: Date.parse(now) - 60_000 }, ...world.feed]);
    if (p === `/chats/${S}` || p === `/projects/${P}/sessions/${S}`) return json(session);
    if (p === '/chats' && method === 'GET') return json([session]);
    if (p === `/projects/${P}/sessions` || p === `/projects/${P}/chats`) return json(world.personal ? [] : [session]);
    const tb = world.personal ? `${ie}/threads` : `${ie}/sessions/${S}/threads`;
    if (p === tb && method === 'GET') return json(snapshot());
    if (p === tb && method === 'POST') {
      const body = r.request().postDataJSON() as { file?: string | null; draftFolder?: string | null };
      const file = body.file ?? null;
      let t: Thread | undefined;
      if (file) {
        // Файл в работу: его нить найдётся или заведётся заново
        t = world.threads.find(x => x.file === file);
        if (!t) { t = base(world.threads.some(x => x.id === H) ? `thread-file-${world.revision}` : H, file); world.threads.push(t); pushRecord(anchor(t.id, Date.now())); }
      } else {
        // Черновик «Новая картинка»
        t = world.threads.find(x => x.id === D);
        if (!t) { t = base(D, null, now, body.draftFolder ?? 'images'); world.threads.push(t); pushRecord(anchor(D, Date.now())); }
      }
      world.focus = t.id;
      world.revision++;
      setTimeout(pushThreads, 30);
      return json(snapshot());
    }
    // Нить без правок уходит из ленты целиком (✕ на чипе)
    if (method === 'DELETE' && p.startsWith(`${tb}/`)) {
      const id = decodeURIComponent(p.slice(tb.length + 1).split('/')[0]);
      world.threads = world.threads.filter(t => t.id !== id);
      if (world.focus === id) world.focus = null;
      world.revision++;
      setTimeout(pushThreads, 30);
      return json(snapshot());
    }
    if (p === `${tb}/focus`) {
      world.focus = (r.request().postDataJSON() as { threadId: string | null }).threadId;
      world.revision++;
      setTimeout(pushThreads, 30);
      return json(snapshot());
    }
    if (p.startsWith(tb)) { world.revision++; return json(snapshot()); }
    if (p === `${ie}/catalog`) return json(CATALOG);
    if (p === `${ie}/prefs`) return json(method === 'GET' ? PREFS : { ...PREFS, ...(r.request().postDataJSON() as object) });
    if (p === `${ie}/characters`) {
      return json([
        { slug: 'anya', name: 'Аня', path: 'characters/anya', photos: [{ file: '1.png' }, { file: '2.png' }], createdAt: now },
        { slug: 'petya', name: 'Петя', path: 'characters/petya', photos: [{ file: '1.png' }], createdAt: now },
      ]);
    }
    if (p.startsWith(`${ie}/characters/`)) return r.fulfill({ contentType: 'image/png', body: PNG });
    if (p === `${ie}/quote`) {
      const req = r.request().postDataJSON() as { provider: string; model: string; count: number; op: string };
      world.quotes.push(req);
      const free = req.provider === 'local';
      const model = req.model === 'auto' ? (free ? 'qwen-image-2.1' : FILL) : req.model;
      return json({
        quoteId: `q${world.quotes.length}`, provider: req.provider, model,
        estimate: free ? { amount: 0, unit: 'free', approx: false, source: 'provider', etaSeconds: 40, queueLength: 0 }
          : { amount: 0.08 * req.count, unit: 'usd', approx: true, source: 'catalog' },
        expiresAt: new Date(Date.now() + 600_000).toISOString(), expectedSeconds: 40,
      });
    }
    if (p === `${ie}/jobs` && method === 'POST') {
      const body = r.request().postDataBuffer()?.toString('utf8') ?? '';
      const fields = [...body.matchAll(/name="([^"]+)"/g)].map(m => m[1]);
      const field = (n: string) => new RegExp(`name="${n}"\\r\\n\\r\\n([^\\r]*)`).exec(body)?.[1] ?? null;
      const jobId = `job${world.jobs.length + 1}`;
      const job: Job = { jobId, op: world.quotes.at(-1)?.op as string ?? null, prompt: field('prompt'), threadId: field('threadId'), fields, aspectRatio: field('aspectRatio') };
      world.jobs.push(job);
      // Запуск ложится в нить и якорем в ленту; готовность — сразу или по finishJob
      const t = world.threads.find(x => x.id === job.threadId);
      if (t) {
        const at = new Date(Date.parse(now) + world.jobs.length * 60_000).toISOString();
        t.launches.push({ jobId, baseVersionId: null, baseStepId: null, at, status: 'running', initiator: 'human', prompt: job.prompt });
        world.revision++;
        pushRecord(record('image_launch_versions', { threadId: t.id, jobId, prompt: job.prompt, count: 1, model: null, initiator: 'human' }, Date.now()));
        setTimeout(() => (world.autoFinish ? finishJob(jobId) : pushThreads()), 50);
      }
      return json({ jobId });
    }
    const jm = /\/image-editor\/(?:chats\/[^/]+\/)?jobs\/([^/]+)$/.exec(p);
    if (jm) {
      const id = decodeURIComponent(jm[1]);
      if (method === 'DELETE') {
        world.cancelled.push(id);
        hubSend({ type: 'image_edit_failed', jobId: id, projectId: P, outcome: 'cancelled', charged: false, initiator: 'agent' });
      }
      return json(jobOf(id));
    }
    if (/\/jobs\/[^/]+\/variants\/\d+$/.test(p) || p.includes('/files/stream') || p.includes('/steps/') || p.includes('/files/raw')) {
      return r.fulfill({ contentType: 'image/png', body: HERO });
    }
    if (p === `${ie}/save/check`) {
      const folder = url.searchParams.get('folder') ?? '';
      const name = (url.searchParams.get('name') ?? 'kartinka').replace(/\.png$/i, '');
      const full = (n: string) => `${folder ? `${folder}/` : ''}${n}.png`;
      const stem = name.replace(/\.v\d+$/, '');
      const taken = world.taken.has(full(name));
      let n = 2;
      while (world.taken.has(full(`${stem}.v${n}`)) || full(`${stem}.v${n}`) === full(name)) n++;
      return json({ path: full(name), taken, suggestion: taken ? full(`${stem}.v${n}`) : null });
    }
    if (p === `${ie}/save` && method === 'POST') {
      const req = r.request().postDataJSON() as { folder?: string; fileName?: string; sourcePath?: string; threadId?: string; mode?: string };
      world.saves.push(req);
      const rel = req.mode === 'next-version' && req.sourcePath
        ? req.sourcePath.replace(/\.png$/, '.v2.png')
        : `${req.folder ? `${req.folder}/` : ''}${(req.fileName ?? 'kartinka').replace(/\.png$/i, '')}.png`;
      world.taken.add(rel);
      // Нить идёт за файлом: черновик становится картинкой-файлом
      const t = world.threads.find(x => x.id === req.threadId);
      if (t) { t.file = rel; t.draftFolder = null; world.revision++; setTimeout(pushThreads, 30); }
      return json({ path: rel });
    }
    if (p === `/chats/${S}/files/upload` && method === 'POST') {
      const name = /filename="([^"]+)"/.exec(r.request().postDataBuffer()?.toString('utf8') ?? '')?.[1] ?? 'file';
      const rel = `.cc-attachments/${name}`;
      world.uploads.push(rel);
      return json({ path: rel });
    }
    if (p === `/projects/${P}/files/tree`) return json(TREE);
    // Папка дерева файлов — один уровень
    if (p === `/projects/${P}/files` && method === 'GET') {
      const dir = url.searchParams.get('path') ?? '';
      return json(TREE.filter(e => (e.path.includes('/') ? e.path.slice(0, e.path.lastIndexOf('/')) : '') === dir));
    }
    if (p === `/projects/${P}/files/content`) {
      return json({ content: null, isBinary: true, isImage: true, mimeType: 'image/png', base64: HERO.toString('base64'), fileSize: HERO.length });
    }
    const OBJ: Record<string, unknown> = {
      '/modules': { items: [] }, '/models': { models: [] }, '/settings': {}, '/usage': { snapshots: [] }, '/home/summary': { active: [], recent: [] },
      '/chats/agents-presence': { agents: [], commands: [] }, '/watchdogs': { sessions: [], projects: [] },
      '/notifications/unread-count': { count: 0 },
      [`/projects/${P}/git/status`]: { isRepo: false, branch: null, upstream: null, ahead: 0, behind: 0, detached: false, staged: [], unstaged: [], untracked: [], isWorktree: false },
    };
    if (p in OBJ) return json(OBJ[p]);
    if (trace) console.log('FALLBACK', method, p);
    return method === 'GET' ? json([]) : json({});
  });
}

// Вкладка чата: тема, режим «Создать / Править» (только с флагом), полоса развёрнута
export async function openChat(page: Page, o: { vp: { width: number; height: number }; theme?: 'light' | 'dark'; mode?: 'edit' | 'create'; route?: string; trace?: boolean }) {
  await page.setViewportSize(o.vp);
  await page.addInitScript(([th, m, s]) => {
    localStorage.setItem('cc_token', 'e2e-token');
    localStorage.setItem('cc_user_id', 'u1');
    localStorage.setItem('theme-mode', th as string);
    if (m) localStorage.setItem(`cc-image-mode:${s}`, m as string);
    // Полоса развёрнута: на телефоне по умолчанию она строкой, макет считает от развёрнутой
    localStorage.setItem(`cc-composer-strip-collapsed:${s}:images`, '0');
  }, [o.theme ?? 'light', o.mode ?? '', S]);
  await mockApi(page, o.trace);
  if (o.trace) page.on('console', m => { if (m.type() === 'error') console.log('CONSOLE', m.text().slice(0, 3000)); });
  await page.goto(o.route ?? (world.personal ? `/#/chats/${S}` : `/#/project/${P}/chat/${S}`));
}

// Счёт как в макете: клик — нажатие на контрол, ввод текста не в счёт; переход — смена зоны
// «лента / полоса и поле ввода (b) / панель (p) / редактор (e)», старт у поля ввода
export class Counter {
  clicks = 0;
  moves = 0;
  zone = 'b';
  async click(z: string, l: Locator) {
    await l.click();
    this.step(z);
  }
  async select(z: string, l: Locator, value: string) {
    // Открыть список и выбрать пункт — два нажатия
    await l.selectOption(value);
    this.clicks += 1;
    this.step(z);
  }
  step(z: string) {
    this.clicks += 1;
    if (z !== this.zone) { this.moves += 1; this.zone = z; }
  }
}

export const strip = (page: Page) => page.locator('[data-images-strip="full"]');
export const input = (page: Page) => page.locator('textarea').last();
export const panel = (page: Page) => page.getByRole('complementary', { name: 'Картинки' });
export const imageToggle = (page: Page) => page.getByRole('button', { name: 'Режим «Картинка»' });

// Поле ввода в режим «Картинка» — подготовка сценария, в счёт не входит
export async function imageComposer(page: Page) {
  await expect(strip(page)).toBeVisible({ timeout: 30_000 });
  if (await imageToggle(page).isVisible()) await imageToggle(page).click();
  await expect(input(page)).toHaveAttribute('placeholder', /Что изменить|Опишите новую/);
}

export async function shot(page: Page, dir: string, name: string) {
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: path.join(dir, `${name}.png`) });
}

// Мазок кистью по картинке холста редактора
export async function brush(page: Page, pic: Locator) {
  const box = (await pic.boundingBox())!;
  await page.mouse.move(box.x + box.width * 0.6, box.y + box.height * 0.4);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.75, box.y + box.height * 0.6, { steps: 6 });
  await page.mouse.up();
}

// Выбор картинки на телефоне сам поднимает шторку панели «Картинки» — опустить её к ленте
export async function closeSheet(page: Page) {
  const sheet = page.locator('[data-gen-sheet="sheet"]');
  await sheet.waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  if (await sheet.isVisible()) await sheet.getByTitle('Закрыть панель — сводка останется в полосе').click();
  await expect(sheet).toHaveCount(0);
}
