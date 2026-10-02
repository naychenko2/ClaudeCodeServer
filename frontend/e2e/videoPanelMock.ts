import { expect, type Page, type Route, type WebSocketRoute } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Мок стенда для спек «Видео» (video-*.spec.ts): собранный dist раздаётся статикой, /api/** и хаб —
// моки по контрактам ADR-022. Мир один на тест: сцены чата, фильмы проекта, лента (записи модуля),
// котировки, запуски и сборки; запись нитей и фильма рассылается событием хаба, как на бою.
// «Картинки» и «Звук» — минимальные заглушки ровно для стыков (preset и returnTo).

export const P = 'proj-vid';
export const S = 'chat-vid';
export const FILM = 'video/утро-в-горах/утро-в-горах.film';
export const now = '2026-10-02T10:00:00Z';
const FIX = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures');
export const MP4 = fs.readFileSync(path.join(FIX, 'video-clip.mp4'));
const PNG_A = fs.readFileSync(path.join(FIX, 'video-frame-a.png'));
const PNG_B = fs.readFileSync(path.join(FIX, 'video-frame-b.png'));

export const CATALOG = {
  providers: [
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true,
      models: [{ id: 'minimax-h3', label: 'MiniMax H3', durations: [5, 10], aspects: ['16:9', '9:16'], sound: true, lastFrame: true }] },
    { key: 'fal', label: 'fal.ai', priceUnit: 'usd', available: true,
      models: [
        { id: 'veo-3.1', label: 'Veo 3.1', durations: [4, 6, 8], aspects: ['16:9', '9:16'], sound: true, lastFrame: true, license: 'watermark' },
        { id: 'seedance-2.5', label: 'Seedance 2.5', durations: [5, 10], aspects: ['16:9', '1:1'], sound: false, lastFrame: true },
      ] },
    { key: 'higgsfield', label: 'Higgsfield', priceUnit: 'credits', available: true,
      models: [{ id: 'kling3_0', label: 'Kling 3.0', durations: [5, 10], aspects: ['16:9'], sound: false, lastFrame: true, license: 'commercial' }] },
  ],
  autoModelId: 'auto', maxCount: 4, autoProviders: ['local', 'fal', 'higgsfield'],
};
const PREFS = { provider: 'fal', model: 'veo-3.1', durationSec: 8, aspect: '16:9', sound: true, count: 2 };

type Frame = { kind: 'file'; path: string } | { kind: 'image'; threadId: string; versionId: string; follow?: boolean };
export interface Scene {
  sceneId: string; name: string; folder: string;
  settings: { frameA?: Frame; frameB?: Frame; text: string; provider?: string; model?: string; durationSec?: number; aspect?: string; sound?: boolean; count?: number };
  versions: Record<string, unknown>[]; currentVersionId?: string; launches: Record<string, unknown>[];
  stale?: { text: boolean; frameA: boolean; frameB: boolean; versionId?: string };
  savedFiles: { versionId: string; path: string }[]; filmRef?: { path: string; position: number }; createdAt: string;
}
interface FilmItem { file: string; trim: [number, number]; scene?: Record<string, unknown> }
export interface Film {
  path: string; revision: string;
  document: { schema: 1; aspect: string; items: FilmItem[]; cuts: { type: string; sec: number }[]; music?: { file: string; volume: number; fadeOut: number }; builds: { file: string; sourceHash: string; at: string }[] };
  spent: { usd: number; credits: number; gpuSeconds: number };
  marks: { index: number; claude: boolean; updated: boolean; stale: boolean }[];
  build?: { state: string; progress: number; file?: string; error?: string };
}

const file = (p: string): Frame => ({ kind: 'file', path: p });
export const ver = (n: number, jobId: string, initiator = 'human') => ({
  versionId: `ver-${jobId}-${n}`, number: n, jobId, variant: n - 1, provider: 'fal', model: 'veo-3.1', durationSec: 8, sizeBytes: 36026,
  hasSound: true, license: 'watermark', cost: { currency: 'usd', amount: 1.6 }, initiator,
  inputs: { text: 'т', frameA: 'file:a', frameB: 'file:b' }, createdAt: now,
});

export function scene(n: number, extra: Partial<Scene> = {}): Scene {
  return {
    sceneId: `scene-${n}`, name: `Сцена ${n}`, folder: 'video/утро-в-горах',
    settings: {
      frameA: file(`video/утро-в-горах/кадры/кадр-${n}.png`), frameB: file(`video/утро-в-горах/кадры/кадр-${n + 1}.png`),
      text: n === 5 ? 'Солнце поднимается над хребтом, камера медленно отъезжает' : `Сцена ${n}: камера плывёт над долиной`,
      provider: 'fal', model: 'veo-3.1', durationSec: 8, aspect: '16:9', sound: true, count: 2,
    },
    versions: [], launches: [], savedFiles: [], createdAt: new Date(Date.parse(now) + n * 60_000).toISOString(), ...extra,
  };
}

// Сцены 1–4 сняты и стоят в фильме, сцена 5 — к съёмке
export function standardScenes(): Scene[] {
  const out: Scene[] = [];
  for (let n = 1; n <= 4; n++) {
    const v = ver(1, `job-s${n}`);
    out.push(scene(n, {
      versions: [v], currentVersionId: v.versionId as string,
      launches: [{ jobId: `job-s${n}`, at: now, status: 'done', interrupted: false, initiator: 'human', provider: 'fal', model: 'veo-3.1', count: 1 }],
      savedFiles: [{ versionId: v.versionId as string, path: `video/утро-в-горах/scene-0${n}.mp4` }],
      filmRef: { path: FILM, position: n - 1 },
    }));
  }
  out.push(scene(5));
  return out;
}

export function standardFilm(built = false): Film {
  const items: FilmItem[] = [1, 2, 3, 4].map(n => ({
    file: `video/утро-в-горах/scene-0${n}.mp4`, trim: [0, 8],
    scene: { text: `Сцена ${n}: камера плывёт над долиной`, frameA: `video/утро-в-горах/кадры/кадр-${n}.png`, frameB: `video/утро-в-горах/кадры/кадр-${n + 1}.png`, provider: 'fal', model: 'veo-3.1', durationSec: 8 },
  }));
  return {
    path: FILM, revision: 'r1',
    document: {
      schema: 1, aspect: '16:9', items, cuts: [{ type: 'butt', sec: 0 }, { type: 'dissolve', sec: 1 }, { type: 'butt', sec: 0 }],
      music: { file: 'music/утро.mp3', volume: 60, fadeOut: 4 },
      builds: built ? [{ file: 'video/утро-в-горах/film.mp4', sourceHash: 'h1', at: now }] : [],
    },
    spent: { usd: 13, credits: 0, gpuSeconds: 0 },
    marks: [{ index: 1, claude: true, updated: false, stale: false }],
  };
}

export interface World {
  personal: boolean;
  focus: { sceneId?: string; filmPath?: string };
  revision: number;
  scenes: Scene[];
  films: Map<string, Film>;
  feed: Record<string, unknown>[];
  quotes: Record<string, unknown>[];
  jobs: Record<string, unknown>[];
  patches: Record<string, unknown>[];
  hubs: WebSocketRoute[];
  imageThreads: Record<string, unknown>[];
  imageRevision: number;
  // Ход съёмки: сразу готово или ждёт finishJob
  autoFinish: boolean;
  dsp: boolean;
  audioThreads: Record<string, unknown>[];
  // Фильм, ждущий музыку из «Звука» (films/music)
  musicFor: string | null;
}

let world: World;
export const w = () => world;

export function newWorld(o: Partial<Pick<World, 'personal' | 'focus' | 'scenes' | 'autoFinish' | 'dsp' | 'feed'>> & { films?: Film[] } = {}): World {
  world = {
    personal: !!o.personal, focus: o.focus ?? {}, revision: 1, scenes: o.scenes ?? [], films: new Map((o.films ?? []).map(f => [f.path, f])),
    feed: o.feed ?? [], quotes: [], jobs: [], patches: [], hubs: [], imageThreads: [], imageRevision: 1, autoFinish: o.autoFinish ?? true,
    dsp: o.dsp ?? true, audioThreads: [], musicFor: null,
  };
  return world;
}

export const record = (recordType: string, data: Record<string, unknown>, fallback = '', at = Date.parse(now)) =>
  ({ kind: 'module_record', module: 'videoeditor', recordType, data, fallback, timestamp: at });

export function hubSend(msg: Record<string, unknown>) {
  for (const h of world.hubs) h.send(JSON.stringify({ type: 1, target: 'message', arguments: [msg] }) + '\u001e');
}

const scope = () => (world.personal ? 'personal' : P);
const threadsState = () => ({ focus: world.focus, revision: world.revision, scenes: world.scenes });
export function pushThreads() {
  hubSend({ type: 'video_thread_changed', sessionId: S, scopeKey: scope(), revision: world.revision, state: threadsState() });
}
export function pushFilm(f: Film) {
  hubSend({ type: 'video_film_changed', sessionId: S, scopeKey: P, path: f.path, state: f });
}
export function pushRecord(rec: ReturnType<typeof record>) {
  world.feed.push(rec);
  hubSend({ type: 'module_record', sessionId: S, module: rec.module, recordType: rec.recordType, data: rec.data, fallback: rec.fallback, timestamp: rec.timestamp });
}

// Первая версия звука-черновика: сервер ставит её музыкой фильма и шлёт свежий фильм
export function audioVersionReady(file: string) {
  const f = world.musicFor ? world.films.get(world.musicFor) : undefined;
  if (!f) return;
  f.document.music = { file, volume: 60, fadeOut: 4 };
  f.revision = `r${Number(f.revision.slice(1)) + 1}`;
  world.musicFor = null;
  pushFilm(f);
  pushRecord(record('video_note', { filmPath: f.path, music: file }, `Музыка фильма готова: ${file}`, Date.now()));
}

// Агент берёт сцену в работу: фокус меняется событием, без ответа на клик человека
export function agentFocus(sceneId: string) {
  world.focus = { ...world.focus, sceneId };
  world.revision++;
  pushThreads();
}

export function finishJob(jobId: string) {
  const s = world.scenes.find(x => x.launches.some(l => l.jobId === jobId));
  if (!s) return;
  const l = s.launches.find(x => x.jobId === jobId)!;
  l.status = 'done';
  const count = (l.count as number) || 1;
  for (let i = 1; i <= count; i++) s.versions.push(ver(s.versions.length + 1, jobId, l.initiator as string));
  s.currentVersionId = s.versions[s.versions.length - 1].versionId as string;
  s.stale = undefined;
  world.revision++;
  hubSend({ type: 'video_edit_completed', sessionId: S, scopeKey: scope(), jobId, sceneId: s.sceneId, variants: [1, 2].slice(0, count), initiator: l.initiator });
  pushThreads();
}

const SESSION = () => ({
  id: S, projectId: world.personal ? null : P, mode: 'default', status: 'finished', messageCount: 1, createdAt: now, updatedAt: now,
  name: world.personal ? 'Мои сцены' : 'Утро в горах', ownerId: 'u1',
});
const PROJECT = { id: P, name: 'Фильм про горы', rootPath: '/tmp/vid', createdAt: now, updatedAt: now, ownerId: 'u1' };
const TREE = [
  ['video', true], ['video/утро-в-горах', true], ['video/утро-в-горах/кадры', true], ['music', true],
  ['video/утро-в-горах/кадры/кадр-1.png', false], ['video/утро-в-горах/кадры/кадр-2.png', false], ['video/утро-в-горах/кадры/кадр-3.png', false],
  ['video/утро-в-горах/кадры/кадр-5.png', false], ['video/утро-в-горах/кадры/кадр-6.png', false], ['video/утро-в-горах/кадры/кадр-7.png', false],
  ['video/утро-в-горах/scene-01.mp4', false], ['video/утро-в-горах/scene-02.mp4', false], ['video/утро-в-горах/scene-03.mp4', false],
  ['video/утро-в-горах/scene-04.mp4', false], ['video/утро-в-горах/scene-06.mp4', false], ['video/утро-в-горах/film.mp4', false],
  [FILM, false], ['music/утро.mp3', false], ['music/вечер.mp3', false],
].map(([p, d]) => ({ name: (p as string).split('/').pop(), path: p, isDirectory: d, modified: now, isModified: false }));

function filmOf(p: string): Film | undefined { return world.films.get(p); }

function applyOps(f: Film, ops: Record<string, unknown>[]) {
  const d = f.document;
  for (const op of ops) {
    switch (op.op) {
      case 'add': {
        const at = typeof op.index === 'number' ? op.index : d.items.length;
        d.items.splice(at, 0, { file: op.file as string, trim: [0, ((op.scene as { durationSec?: number })?.durationSec) ?? 8], scene: op.scene as Record<string, unknown> });
        if (d.items.length > 1) d.cuts.splice(Math.max(0, at - 1), 0, { type: 'butt', sec: 0 });
        break;
      }
      case 'remove': {
        const i = op.index as number;
        d.items.splice(i, 1);
        d.cuts.splice(Math.min(i, d.cuts.length - 1), 1);
        break;
      }
      case 'move': {
        const [it] = d.items.splice(op.from as number, 1);
        d.items.splice(op.to as number, 0, it);
        break;
      }
      case 'cut': d.cuts[op.index as number] = { type: op.cutType as string, sec: (op.sec as number) ?? 0 }; break;
      case 'trim': d.items[op.index as number].trim = op.trim as [number, number]; break;
      case 'music': d.music = (op.music as Film['document']['music']) ?? undefined; break;
    }
  }
  f.revision = `r${Number(f.revision.slice(1)) + 1}`;
  if (d.builds.length) {
    f.marks = d.items.map((_, i) => ({ index: i, claude: false, updated: f.marks.find(m => m.index === i)?.updated ?? false, stale: false }));
  }
}

function runBuild(f: Film) {
  f.build = { state: 'waiting', progress: 0 };
  pushFilm(f);
  setTimeout(() => { f.build = { state: 'running', progress: 0.4, file: 'film.mp4' }; pushFilm(f); }, 300);
  setTimeout(() => {
    const n = f.document.builds.length;
    const out = `video/утро-в-горах/${n ? `film.v${n + 1}.mp4` : 'film.mp4'}`;
    f.document.builds.push({ file: out, sourceHash: `h${n + 2}`, at: now });
    f.build = { state: 'done', progress: 1, file: out };
    f.marks = f.marks.map(m => ({ ...m, updated: false, stale: false }));
    f.revision = `r${Number(f.revision.slice(1)) + 1}`;
    pushFilm(f);
    pushRecord(record('video_film_built', { path: f.path, file: out }, `Фильм собран: ${out}`, Date.now()));
  }, 900);
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
          const inv = JSON.parse(rec) as { target?: string };
          ws.send(JSON.stringify({ type: 3, invocationId: id, result: inv.target === 'SendMessage' ? 'started' : null }) + '\u001e');
        }
      }
    });
  });
  await page.route('**/api/**', async (r: Route) => {
    const url = new URL(r.request().url());
    const p = decodeURIComponent(url.pathname.replace(/^\/api/, ''));
    const method = r.request().method();
    const json = (body: unknown, status = 200) => r.fulfill({ status, json: body });
    const body = () => (r.request().postDataJSON() ?? {}) as Record<string, unknown>;
    const session = SESSION();
    if (p === '/auth/me') {
      return json({
        id: 'u1', userId: 'u1', username: 'admin', displayName: 'Андрей', role: 'admin', executionEnvironment: 'local',
        featureFlags: { 'video-editor': true, 'image-editor': true, 'audio-editor': true, 'image-panel-v5': true },
        subsystems: ['videoeditor', 'imageeditor', 'audioeditor'],
      });
    }
    if (p === '/subsystem-modules') {
      return json({ items: [
        { id: 'videoeditor', remoteUrl: '/video-editor-remote/remoteEntry.js', exposedModule: './subsystem' },
        { id: 'imageeditor', remoteUrl: '/image-editor-remote/remoteEntry.js', exposedModule: './subsystem' },
        { id: 'audioeditor', remoteUrl: '/audio-editor-remote/remoteEntry.js', exposedModule: './subsystem' },
      ] });
    }
    if (p === '/projects' && method === 'GET') return json(world.personal ? [] : [PROJECT]);
    if (p === `/projects/${P}`) return json(PROJECT);
    if (p === `/chats/${S}/history` || p === `/projects/${P}/sessions/${S}/history`) {
      return json([{ kind: 'user_message', text: 'Сделаем фильм про утро в горах', timestamp: Date.parse(now) - 60_000 }, ...world.feed]);
    }
    if (p === `/chats/${S}` || p === `/projects/${P}/sessions/${S}`) return json(session);
    if (p === '/chats' && method === 'GET') return json([session]);
    if (p === `/projects/${P}/sessions` || p === `/projects/${P}/chats`) return json(world.personal ? [] : [session]);

    // ── Видео ──
    const vb = world.personal ? `/video-editor/chats/${S}` : `/projects/${P}/video-editor`;
    const cb = world.personal ? vb : `${vb}/sessions/${S}`;
    if (p === `${cb}/state`) return json({ threads: threadsState(), catalog: CATALOG, prefs: PREFS });
    if (p === `${vb}/catalog`) return json(CATALOG);
    if (p === `${vb}/prefs`) return json(method === 'GET' ? PREFS : { ...PREFS, ...body() });
    if (p === `${cb}/scenes` && method === 'POST') {
      const b = body();
      const n = world.scenes.length + 1;
      const s: Scene = { ...scene(n), settings: { text: '', ...(b.settings as object) } as Scene['settings'], name: (b.name as string) ?? `Сцена ${n}` };
      world.scenes.push(s);
      world.focus = { ...world.focus, sceneId: s.sceneId };
      world.revision++;
      pushRecord(record('video_scene', { sceneId: s.sceneId }, `Видео: ${s.name}`, Date.now()));
      setTimeout(pushThreads, 30);
      return json(threadsState());
    }
    if (p === `${cb}/scenes/focus`) {
      const b = body();
      world.focus = (b.focus as World['focus']) ?? {};
      world.revision++;
      setTimeout(pushThreads, 30);
      return json(threadsState());
    }
    const sm = /\/scenes\/([^/]+)\/(settings|current|save|versions\/([^/]+)\/(file|poster))$/.exec(p);
    if (sm && p.startsWith(cb)) {
      const s = world.scenes.find(x => x.sceneId === sm[1]);
      if (!s) return json({ error: 'Сцена не найдена', code: 'scene_not_found' }, 404);
      if (sm[2] === 'settings') {
        const b = body();
        if ((b.revision as number) !== world.revision) return json({ error: 'конфликт', code: 'revision_conflict', state: threadsState() }, 409);
        const prev = s.settings;
        s.settings = b.settings as Scene['settings'];
        if (s.versions.length) {
          s.stale = {
            text: (s.stale?.text ?? false) || prev.text !== s.settings.text,
            frameA: (s.stale?.frameA ?? false) || JSON.stringify(prev.frameA) !== JSON.stringify(s.settings.frameA),
            frameB: (s.stale?.frameB ?? false) || JSON.stringify(prev.frameB) !== JSON.stringify(s.settings.frameB),
            versionId: s.currentVersionId,
          };
        }
        world.revision++;
        setTimeout(pushThreads, 30);
        return json(threadsState());
      }
      if (sm[2] === 'current') { s.currentVersionId = body().versionId as string; world.revision++; setTimeout(pushThreads, 30); return json(threadsState()); }
      if (sm[2] === 'save') {
        const b = body();
        const vId = (b.versionId as string) ?? s.currentVersionId!;
        const n = Number(s.sceneId.replace(/\D/g, ''));
        const prev = s.savedFiles.length;
        const out = `video/утро-в-горах/scene-0${n}${prev ? `.v${prev + 1}` : ''}.mp4`;
        s.savedFiles.push({ versionId: vId, path: out });
        let added = false;
        const f = world.focus.filmPath ? filmOf(world.focus.filmPath) : undefined;
        if (f) {
          const idx = f.document.items.findIndex(it => it.file.replace(/(\.v\d+)?\.mp4$/, '') === out.replace(/(\.v\d+)?\.mp4$/, ''));
          if (idx >= 0) {
            f.document.items[idx].file = out;
            const m = f.marks.find(x => x.index === idx);
            if (m) m.updated = true; else f.marks.push({ index: idx, claude: false, updated: true, stale: false });
          } else {
            applyOps(f, [{ op: 'add', file: out, scene: { text: s.settings.text, durationSec: 8 } }]);
            s.filmRef = { path: f.path, position: f.document.items.length - 1 };
            if (f.document.builds.length) {
              const last = f.document.items.length - 1;
              f.marks = [...f.marks.filter(m => m.index !== last), { index: last, claude: false, updated: true, stale: false }];
            }
          }
          f.revision = `r${Number(f.revision.slice(1)) + 1}`;
          added = true;
          setTimeout(() => pushFilm(f), 30);
        }
        world.revision++;
        setTimeout(pushThreads, 30);
        pushRecord(record('video_saved', { sceneId: s.sceneId, path: out }, `Сцена сохранена: ${out}${added ? ' · стоит в фильме' : ''}`, Date.now()));
        return json({ path: out, framePaths: [], addedToFilm: added });
      }
      if (sm[4] === 'file') return r.fulfill({ contentType: 'video/mp4', body: MP4 });
      return r.fulfill({ contentType: 'image/png', body: PNG_A });
    }
    if (p === `${vb}/quote`) {
      const b = body();
      world.quotes.push(b);
      const local = b.provider === 'local';
      const credits = b.provider === 'higgsfield';
      const count = (b.count as number) ?? 1;
      return json({
        quoteId: `q${world.quotes.length}`, provider: b.provider ?? 'fal', model: b.model ?? 'veo-3.1', count, durationSec: b.durationSec ?? 8,
        price: local ? { unit: 'free', approx: false, source: 'local', eta: 330, queueLength: 1 }
          : credits ? { amount: 16 * count, unit: 'credits', approx: false, source: 'get_cost' }
          : { amount: 1.6 * count, unit: 'usd', approx: true, source: 'pricing', eta: 240 },
        license: 'watermark', heavy: false, expiresAt: '2030-01-01T00:00:00Z',
      });
    }
    if (p === `${vb}/jobs` && method === 'POST') {
      const b = body();
      const jobId = `job${world.jobs.length + 1}`;
      world.jobs.push({ ...b, jobId });
      const q = world.quotes.at(-1) ?? {};
      const s = world.scenes.find(x => x.sceneId === b.sceneId);
      if (s) {
        s.launches.push({ jobId, at: now, status: 'running', interrupted: false, initiator: 'human', provider: q.provider ?? 'fal', model: q.model ?? 'veo-3.1', count: q.count ?? 1, prompt: s.settings.text });
        world.revision++;
        pushRecord(record('video_launch_versions', { sceneId: s.sceneId, jobId, count: q.count ?? 1, initiator: 'human' }, 'Вы запустили съёмку', Date.now()));
        hubSend({ type: 'video_edit_progress', sessionId: S, scopeKey: scope(), jobId, sceneId: s.sceneId, stage: 'running', variant: 1, count: q.count ?? 1, initiator: 'human' });
        pushThreads();
        if (world.autoFinish) setTimeout(() => finishJob(jobId), 400);
      }
      return json({ jobId }, 202);
    }
    if (/\/video-editor\/(?:chats\/[^/]+\/)?jobs\/[^/]+$/.test(p)) return json({ jobId: 'x', status: 'running' });
    if (p === `${vb}/films`) {
      if (method === 'PATCH') {
        const fp = url.searchParams.get('path')!;
        let f = filmOf(fp);
        if (!f) { f = { ...standardFilm(), path: fp, revision: 'r0', document: { schema: 1, aspect: '16:9', items: [], cuts: [], builds: [] }, spent: { usd: 0, credits: 0, gpuSeconds: 0 }, marks: [] }; world.films.set(fp, f); }
        const b = body();
        world.patches.push(b);
        if (b.expectedRevision !== f.revision && f.revision !== 'r0') return json({ error: 'Фильм поменяли', code: 'revision_conflict', state: f }, 409);
        applyOps(f, b.ops as Record<string, unknown>[]);
        return json(f);
      }
      return json([...world.films.values()].map(f => ({ path: f.path, name: f.path.split('/').pop()!.replace('.film', ''), itemCount: f.document.items.length, durationSec: 31, stale: !f.document.builds.length, valid: true })));
    }
    if (p === `${vb}/films/state`) {
      const f = filmOf(url.searchParams.get('path')!);
      return f ? json(f) : json({ path: url.searchParams.get('path'), revision: 'r0', document: { schema: 1, aspect: '16:9', items: [], cuts: [], builds: [] }, spent: { usd: 0, credits: 0, gpuSeconds: 0 }, marks: [] });
    }
    if (p === `${vb}/films/music` && method === 'POST') {
      // Сервер заводит черновик звука в чате и ждёт его первую версию музыкой фильма
      const id = `audio-${world.audioThreads.length + 1}`;
      world.audioThreads.push({ id, file: null, lineage: [], draftFolder: 'music', createdAt: now, versions: [], currentVersionId: null, launches: [], settings: null });
      world.musicFor = url.searchParams.get('path');
      setTimeout(() => hubSend({ type: 'audio_thread_changed', sessionId: S, scopeKey: scope(), revision: 2, state: { focus: id, revision: 2, threads: world.audioThreads } }), 30);
      return json({ threadId: id });
    }
    if (p === `${vb}/films/build`) {
      const f = filmOf(url.searchParams.get('path')!);
      if (!world.dsp) return json({ error: 'На сервере нет ffmpeg — сборка фильма недоступна', code: 'dsp_unavailable' }, 503);
      if (f && method === 'POST') { runBuild(f); return json({ state: 'waiting', progress: 0 }); }
      return json(f?.build ?? { state: 'done', progress: 1 });
    }

    // ── Картинки (заглушка для стыка) ──
    const ib = world.personal ? `/image-editor/chats/${S}` : `/projects/${P}/image-editor`;
    const itb = world.personal ? `${ib}/threads` : `${ib}/sessions/${S}/threads`;
    const imgState = () => ({ focus: (world.imageThreads.at(-1)?.id as string) ?? null, revision: world.imageRevision, threads: world.imageThreads });
    if (p === itb && method === 'GET') return json(imgState());
    if (p === itb && method === 'POST') {
      const b = body();
      world.imageThreads.push({
        id: `img-${world.imageThreads.length + 1}`, file: b.file ?? null, lineage: [], draftFolder: b.file ? null : b.draftFolder, stacks: [], currentStackId: null,
        currentStepId: null, settings: null, pendingJobId: null, createdAt: now,
        versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: now }],
        currentVersionId: 'origin', launches: [],
      });
      world.imageRevision++;
      setTimeout(() => hubSend({ type: 'image_thread_changed', sessionId: S, projectId: world.personal ? null : P, revision: world.imageRevision, state: imgState() }), 30);
      return json(imgState());
    }
    if (p.startsWith(itb)) { world.imageRevision++; return json(imgState()); }
    if (p === `${ib}/catalog`) return json({ default: { provider: 'fal', model: 'auto' }, providers: [{ key: 'fal', label: 'fal', priceUnit: 'usd', available: true, models: [{ id: 'auto', label: 'Авто' }] }], limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null });
    if (p === `${ib}/prefs`) return json({ provider: null, model: null, count: 1, matchSourceSize: true, characterSlug: null, create: { provider: null, model: null, count: 1 }, edit: { provider: null, model: null, count: 1, op: 'edit', editMode: null, ratio: null } });
    if (p.includes('/steps/')) return r.fulfill({ contentType: 'image/png', body: PNG_B });

    // ── Звук (заглушка для стыка) ──
    const ab = world.personal ? `/audio-editor/chats/${S}` : `/projects/${P}/audio-editor/sessions/${S}`;
    if (p.startsWith(`${ab}/threads/focus`)) return json({ focus: (world.audioThreads.at(-1)?.id as string) ?? null, revision: 3, threads: world.audioThreads });
    if (p === `${ab}/state`) {
      return json({ threads: { focus: (world.audioThreads.at(-1)?.id as string) ?? null, revision: 2, threads: world.audioThreads }, catalog: { providers: [], autoModelId: 'auto', maxCount: 4 }, prefs: { voice: null, music: null, process: null } });
    }

    // ── Файлы проекта ──
    if (p === `/projects/${P}/files/tree`) return json(TREE);
    if (p === `/projects/${P}/files` && method === 'GET') {
      const dir = url.searchParams.get('path') ?? '';
      return json(TREE.filter(e => ((e.path as string).includes('/') ? (e.path as string).slice(0, (e.path as string).lastIndexOf('/')) : '') === dir));
    }
    if (p === `/projects/${P}/files/upload`) return json({});
    if (p === `/projects/${P}/files/stream`) {
      const fp = url.searchParams.get('path') ?? '';
      if (/\.mp4$/.test(fp)) return r.fulfill({ contentType: 'video/mp4', body: MP4 });
      return r.fulfill({ contentType: 'image/png', body: /кадр-[246]/.test(fp) ? PNG_B : PNG_A });
    }
    if (p === `/projects/${P}/files/content`) {
      const fp = url.searchParams.get('path') ?? '';
      if (/\.mp4$/.test(fp)) return json({ content: null, isBinary: true, isVideo: true, mimeType: 'video/mp4', fileSize: MP4.length });
      if (/\.film$/.test(fp)) return json({ content: JSON.stringify(filmOf(fp)?.document ?? {}, null, 2), isBinary: false, fileSize: 400 });
      return json({ content: null, isBinary: true, isImage: true, mimeType: 'image/png', base64: PNG_A.toString('base64'), fileSize: PNG_A.length });
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

export async function openChat(page: Page, o: { vp: { width: number; height: number }; theme?: 'light' | 'dark'; trace?: boolean; route?: string }) {
  await page.setViewportSize(o.vp);
  await page.addInitScript(([th, s]) => {
    localStorage.setItem('cc_token', 'e2e-token');
    localStorage.setItem('cc_user_id', 'u1');
    localStorage.setItem('theme-mode', th as string);
    localStorage.setItem(`cc-composer-strip-collapsed:${s}:video`, '0');
  }, [o.theme ?? 'light', S]);
  await mockApi(page, o.trace);
  if (o.trace) page.on('console', m => { if (m.type() === 'error') console.log('CONSOLE', m.text().slice(0, 2000)); });
  await page.goto(o.route ?? (world.personal ? `/#/chats/${S}` : `/#/project/${P}/chat/${S}`));
}

export const strip = (page: Page) => page.locator('[data-video-strip]');
export const panel = (page: Page) => page.getByRole('complementary', { name: 'Видео' }).or(page.getByRole('dialog', { name: 'Видео' })).first();

export async function shot(page: Page, dir: string, name: string) {
  if (!dir) return;
  fs.mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: path.join(dir, `${name}.png`) });
}

export async function stripReady(page: Page) {
  await expect(page.locator('[data-composer-strip="video"]')).toBeVisible({ timeout: 30_000 });
}
