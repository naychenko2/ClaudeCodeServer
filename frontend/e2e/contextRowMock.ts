import type { Page, Route, WebSocketRoute } from '@playwright/test';

// Мок API для context-row.spec.ts: бэкенд не нужен, собранный dist раздаётся статикой, а /api/**
// и хабы — моки. Контекст чата (ADR-023) ведёт мир теста: мутации меняют ревизию и рассылают
// событие chat_context_changed, как на бою. ЖИВОГО API КОНТЕКСТА ещё нет — проверка на моке.

export const P = 'proj-ctx';
export const S = 'chat-ctx';
const caps = (ops: string[], extra: Record<string, unknown> = {}) => ({
  ops, languages: ['ru', 'en'], voiceKinds: ['preset'], producesFiles: ['audio'], license: { label: 'Apache-2.0', kind: 'permissive' },
  priceUnit: 'free', maxTextChars: 5000, minDurationSec: 10, maxDurationSec: 240, ...extra,
});
const stemCaps = (set: string) => caps(['separate'], { languages: [], languageNeutral: true, stemSet: set, license: { label: 'MIT', kind: 'permissive' } });
// Каталог звука мока: «Стемы» разбирают три локальные модели (по набору), озвучка и песня — по одной
const AUDIO_CATALOG = {
  autoModelId: 'auto', maxCount: 4, autoProviders: ['local'],
  providers: [{
    key: 'local', label: 'Локальные модели', priceUnit: 'free', available: true, reason: null,
    models: [
      { id: 'qwen3-tts', label: 'Qwen3-TTS', caps: caps(['speak', 'designVoice', 'cloneVoice']) },
      { id: 'ace-step-1.5-xl', label: 'ACE-Step 1.5 XL', caps: caps(['song']) },
      { id: 'bs-roformer', label: 'BS-RoFormer', caps: stemCaps('vocals') },
      { id: 'htdemucs-ft-4stems', label: 'HTDemucs · 4 стема', caps: stemCaps('4') },
      { id: 'htdemucs-6stems', label: 'HTDemucs · 6 стемов', caps: stemCaps('6') },
    ],
  }],
};

export const now = new Date('2026-10-03T10:00:00Z').toISOString();

type Item = { id: string; kind: string; ref: Record<string, unknown>; by: 'human' | 'agent'; addedAt: string; label: string; version: string | null; thumb: string | null; missing: boolean };
export type Primary = Item & { role: null };
export type Ref = Item & { role: string | null; usedBy: string[] };
export interface Ctx { revision: number; primary: Primary | null; refs: Ref[] }

export const primary = (over: Partial<Primary> = {}): Primary =>
  ({ id: 'p1', kind: 'image', ref: { threadId: 'thread-hero', versionId: 'v2' }, by: 'human', addedAt: now, label: 'hero.png', version: 'v2', thumb: null, missing: false, role: null, ...over });
export const ref = (id: string, label: string, over: Partial<Ref> = {}): Ref =>
  ({ id, kind: 'image', ref: { path: `assets/${label}` }, by: 'human', addedAt: now, label, version: null, thumb: null, missing: false, role: 'style', usedBy: ['edit'], ...over });

// Операции, которые берёт референс (зеркало AcceptedRefs звука): серость в строке и панели считается по операции действия
export const usedByOf = (kind: string, role: string | null): string[] =>
  kind === 'audio-voice' ? ['speak', 'dialogue', 'convertVoice']
    : (kind === 'audio' || kind === 'project-file') && role === 'reference' ? ['cloneVoice', 'convertVoice', 'cover', 'master']
    : (kind === 'audio' || kind === 'project-file') && role === 'piece' ? ['concat']
    : ['edit'];

export const voiceRef = (id = 'rv') =>
  ref(id, 'Марина', { kind: 'audio-voice', ref: { slug: 'marina' }, role: 'voice', usedBy: usedByOf('audio-voice', 'voice') });

export interface World {
  ctx: Ctx;
  flags: Record<string, boolean>;
  hands: boolean;
  savedFiles: { path: string; threadKind: string; savedAt: string }[];
  mutations: { method: string; path: string; body: unknown }[];
  invocations: { target: string; args: unknown[] }[];
  hubs: WebSocketRoute[];
  personal: boolean;
  // Записи ленты кроме первого сообщения человека (module_record и прочее)
  feed: Record<string, unknown>[];
  // Нити картинок чата (мок …/image-editor/sessions/S/threads); null — маршрута нет, как раньше
  threads: Record<string, unknown>[] | null;
  threadsRevision: number;
  // Нити звука чата (мок …/audio-editor/sessions/S/*); null — маршрута нет. edits — тела правок без ИИ
  audio: Record<string, unknown>[] | null;
  audioRevision: number;
  audioEdits: Record<string, unknown>[];
  // Котировки и запуски звука (тела как пришли): quote — JSON, jobs — поля формы
  audioQuotes: Record<string, unknown>[];
  audioJobs: Record<string, string>[];
}

let world: World;
export const w = () => world;

export function newWorld(o: Partial<Pick<World, 'flags' | 'hands' | 'savedFiles' | 'personal' | 'feed' | 'threads' | 'audio'>> & { ctx?: Partial<Ctx> } = {}): World {
  world = {
    ctx: { revision: 1, primary: null, refs: [], ...o.ctx },
    flags: { 'chat-context': true, ...o.flags },
    hands: o.hands ?? true, savedFiles: o.savedFiles ?? [], mutations: [], invocations: [], hubs: [], personal: o.personal ?? false, feed: o.feed ?? [],
    threads: o.threads ?? null, threadsRevision: 1, audio: o.audio ?? null, audioRevision: 1, audioEdits: [], audioQuotes: [], audioJobs: [],
  };
  return world;
}

export function hubSend(msg: Record<string, unknown>) {
  for (const h of world.hubs) h.send(JSON.stringify({ type: 1, target: 'message', arguments: [msg] }) + '\u001e');
}
const VOICE = {
  slug: 'marina', name: 'Марина', kind: 'samples', path: 'voices/marina', samples: [{ file: 'a.wav' }], transcript: null,
  createdAt: now, providers: [], needsAttention: false,
};

// Задача звука завершилась: «Стемы» дают новую версию со стемами и делают её текущей, как сервер
function finishAudioJob(jobId: string, op: string, threadId: string) {
  const t = world.audio?.find(x => x.id === threadId) as { versions: Record<string, unknown>[]; currentVersionId: string; settings?: Record<string, unknown> } | undefined;
  if (!t) return;
  const n = t.versions.length;
  const id = `v${n}`;
  const roles = op === 'separate' ? ['main', 'stem:vocals', 'stem:drums', 'stem:bass', 'stem:other'] : ['main'];
  t.versions.push({ id, number: n, jobId, variant: 1, baseVersionId: t.currentVersionId, license: null, createdAt: now, files: roles.map(role => ({ role, path: `${id}/${role}.mp3` })) });
  t.currentVersionId = id;
  world.audioRevision++;
  const state = { focus: threadId, revision: world.audioRevision, threads: world.audio };
  hubSend({ type: 'audio_thread_changed', sessionId: S, scopeKey: P, state });
  hubSend({ type: 'audio_edit_completed', sessionId: S, scopeKey: P, jobId, variants: [1], cost: null, error: null, chatSessionId: S, threadId, initiator: 'human' });
}

const pushCtx = () => hubSend({ type: 'chat_context_changed', sessionId: S, context: world.ctx });

const PROJECT = () => ({
  id: P, name: 'Сайт студии', rootPath: '/tmp/ctx', createdAt: now, updatedAt: now, ownerId: 'u1',
  handsEnabled: world.hands, handsRefusal: world.hands ? null : 'выключены',
});
const SESSION = () => ({
  id: S, projectId: world.personal ? null : P, mode: 'default', status: 'finished', messageCount: 1, createdAt: now, updatedAt: now,
  name: 'Контекст', ownerId: 'u1',
});
const GIT = {
  isRepo: true, branch: 'feat/video-editor', upstream: 'origin/feat/video-editor', ahead: 1, behind: 0, detached: false, isWorktree: false,
  staged: [], untracked: [],
  unstaged: ['a.ts', 'b.ts', 'c.ts'].map(path => ({ path, status: 'M', added: 4, deleted: 1 })),
};

export async function mockApi(page: Page) {
  // Только корневые /hubs/ и /api/: на dev-сервере Vite исходники лежат и под src/**/api/, их мок не трогает
  await page.route(u => u.pathname.startsWith('/hubs/'), async (r: Route) => {
    if (r.request().url().includes('negotiate')) {
      return r.fulfill({ json: { negotiateVersion: 1, connectionId: 'c', connectionToken: 'c', availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }] } });
    }
    return r.fulfill({ status: 404, body: '' });
  });
  await page.routeWebSocket(/\/hubs\//, ws => {
    if (ws.url().includes('/hubs/session')) world.hubs.push(ws);
    ws.onMessage(m => {
      if (typeof m !== 'string') return;
      if (m.includes('"protocol"')) ws.send('{}\u001e');
      for (const rec of m.split('\u001e')) {
        const id = /"invocationId":"([^"]+)"/.exec(rec)?.[1];
        if (!id) continue;
        const inv = JSON.parse(rec) as { target?: string; arguments?: unknown[] };
        if (inv.target) world.invocations.push({ target: inv.target, args: inv.arguments ?? [] });
        ws.send(JSON.stringify({ type: 3, invocationId: id, result: inv.target === 'SendMessage' ? 'started' : null }) + '\u001e');
      }
    });
  });
  await page.route(u => u.pathname.startsWith('/api/'), async (r: Route) => {
    const url = new URL(r.request().url());
    const p = url.pathname.replace(/^\/api/, '');
    const method = r.request().method();
    const json = (body: unknown) => r.fulfill({ json: body });
    if (p === '/auth/me') {
      return json({ id: 'u1', userId: 'u1', username: 'admin', displayName: 'Андрей', role: 'admin', executionEnvironment: 'local', featureFlags: world.flags, subsystems: [] });
    }
    if (p === '/projects' && method === 'GET') return json(world.personal ? [] : [PROJECT()]);
    if (p === `/projects/${P}`) return json(PROJECT());
    if (p === `/chats/${S}/history` || p === `/projects/${P}/sessions/${S}/history`) {
      return json([{ kind: 'user_message', text: 'Поправь hero', timestamp: Date.parse(now) - 60_000 }, ...world.feed]);
    }
    if (p === `/chats/${S}` || p === `/projects/${P}/sessions/${S}`) return json(SESSION());
    if (p === '/chats' && method === 'GET') return json([SESSION()]);
    if (p === `/projects/${P}/sessions` || p === `/projects/${P}/chats`) return json(world.personal ? [] : [SESSION()]);
    if (p === `/projects/${P}/git/status`) return json(GIT);
    if (p === `/sessions/${S}/hands-status`) return json({ state: 'active', reason: null, deviceName: 'Рабочий ПК' });
    // ── нити картинок (мок для вида «картинка»): чтение и любая запись отдают снимок состояния ──
    const tb = `/projects/${P}/image-editor/sessions/${S}/threads`;
    if (world.threads && p.startsWith(tb)) {
      if (method !== 'GET') {
        world.threadsRevision++;
        const sm = /\/threads\/([^/]+)\/settings$/.exec(p);
        const t = sm ? world.threads.find(x => x.id === decodeURIComponent(sm[1])) : null;
        if (t) t.settings = r.request().postDataJSON().settings;
      }
      return json({ focus: null, revision: world.threadsRevision, threads: world.threads });
    }
    // ── нити звука: состояние, правка без ИИ (новая версия), пики; прочее отдаёт снимок ──
    const ab = `/projects/${P}/audio-editor`;
    if (world.audio && p === `${ab}/voices`) return json({ available: true, voices: [VOICE] });
    if (world.audio && p.startsWith(ab)) {
      const snapshot = () => ({ focus: null, revision: world.audioRevision, threads: world.audio });
      if (p === `${ab}/quote` && method === 'POST') {
        const body = r.request().postDataJSON();
        world.audioQuotes.push(body);
        return json({
          quoteId: `q${world.audioQuotes.length}`, mode: body.mode, op: body.operation, provider: body.provider ?? 'local', model: body.model ?? 'auto',
          count: body.count ?? 1, voiceKind: null, price: { amount: null, unit: 'free', approx: false, source: 'local', eta: 40, queueLength: 0 },
          license: 'MIT', heavy: false, expiresAt: '2026-10-03T11:00:00Z',
        });
      }
      if (p === `${ab}/jobs` && method === 'POST') {
        const raw = r.request().postData() ?? '';
        const fields: Record<string, string> = {};
        for (const m of raw.matchAll(/name="([^"]+)"\r\n\r\n([^\r]*)\r\n/g)) fields[m[1]] = m[2];
        world.audioJobs.push(fields);
        const op = String(world.audioQuotes.at(-1)?.operation ?? '');
        const jobId = `job${world.audioJobs.length}`;
        setTimeout(() => finishAudioJob(jobId, op, String((world.ctx.primary?.ref as { threadId?: string } | undefined)?.threadId ?? '')), 80);
        return json({ jobId });
      }
      if (p.endsWith('/peaks')) return json({ peaks: Array.from({ length: 120 }, (_, i) => 0.2 + 0.7 * Math.abs(Math.sin(i / 7))), seconds: 12 });
      const em = /\/threads\/([^/]+)\/edit$/.exec(p);
      if (em && method === 'POST') {
        const body = r.request().postDataJSON();
        world.audioEdits.push(body);
        const t = world.audio.find(x => x.id === decodeURIComponent(em[1])) as { versions: { id: string; number: number }[]; currentVersionId: string };
        const n = t.versions.length;
        const id = `v${n}`;
        t.versions.push({ id, number: n, jobId: `e${n}`, variant: null, baseVersionId: t.currentVersionId, license: null, createdAt: now, files: [{ role: 'main', path: `${id}/main.mp3` }] } as never);
        t.currentVersionId = id;
        world.audioRevision++;
        return json({ threadId: t.id, versionId: id, number: n, jobId: `e${n}`, state: snapshot() });
      }
      if (p.endsWith('/state')) {
        return json({
          threads: snapshot(),
          catalog: AUDIO_CATALOG, prefs: { voice: null, music: null, process: null },
        });
      }
      return json(snapshot());
    }
    // ── контекст чата ──
    const base = `/chats/${S}/context`;
    if (p === base && method === 'GET') return json(world.ctx);
    if (p === `${base}/saved-files`) return json(world.savedFiles);
    if (p.startsWith(base)) {
      const body = r.request().postData() ? r.request().postDataJSON() : null;
      world.mutations.push({ method, path: p.slice(base.length) || '/', body });
      const c = world.ctx;
      if (p === `${base}/primary` && method === 'PUT') {
        c.primary = body.kind === null ? null : primary({ kind: body.kind, ref: body.ref, label: body.kind === 'audio' ? 'intro.mp3' : 'hero.png', version: 'v2' });
      } else if (p === `${base}/refs` && method === 'POST') {
        // Как у бэкенда (AcceptedRefs ImageContextKind): образец-картинка и файл проекта — style|object|face,
        // персонаж из «Персонажей» — character; чужая роль — 400 role_not_accepted
        if (c.primary?.kind === 'image') {
          const okRoles: Record<string, string[]> = { image: ['style', 'object', 'face'], 'project-file': ['style', 'object', 'face'], 'image-character': ['character'] };
          if (okRoles[body.kind] && !okRoles[body.kind].includes(body.role)) {
            return r.fulfill({ status: 400, json: { error: 'role_not_accepted', message: `Основной объект не принимает референс «${body.kind}» с ролью «${body.role}»` } });
          }
        }
        const rf = body.ref as { path?: string; slug?: string; upload?: string };
        const names: Record<string, string> = { anya: 'Аня', marina: 'Марина' };
        c.refs = [...c.refs, ref(`r${c.refs.length + 1}`, (rf.slug && names[rf.slug]) || (rf.upload ? 'образец' : String(rf.path ?? rf.slug ?? 'ref').split('/').pop()!),
          { kind: body.kind, ref: body.ref, role: body.role ?? null, usedBy: usedByOf(body.kind, body.role ?? null) })];
      } else if (p.startsWith(`${base}/refs/`) && method === 'DELETE') {
        const id = decodeURIComponent(p.slice(`${base}/refs/`.length));
        c.refs = c.refs.filter(x => x.id !== id);
      } else if (p === base && method === 'DELETE') {
        c.primary = null; c.refs = [];
      }
      c.revision++;
      setTimeout(pushCtx, 20);
      return json(c);
    }
    const OBJ: Record<string, unknown> = {
      '/modules': { items: [] }, '/models': { models: [] }, '/settings': {}, '/usage': { snapshots: [] }, '/home/summary': { active: [], recent: [] },
      '/chats/agents-presence': { agents: [], commands: [] }, '/watchdogs': { sessions: [], projects: [] },
      '/notifications/unread-count': { count: 0 }, '/subsystem-modules': { items: [] },
    };
    if (p in OBJ) return json(OBJ[p]);
    return method === 'GET' ? json([]) : json({});
  });
}

export async function openChat(page: Page, o: { vp: { width: number; height: number }; theme?: 'light' | 'dark' }) {
  await page.setViewportSize(o.vp);
  await page.addInitScript(([th]) => {
    localStorage.setItem('cc_token', 'e2e-token');
    localStorage.setItem('cc_user_id', 'u1');
    localStorage.setItem('theme-mode', th as string);
  }, [o.theme ?? 'light']);
  await mockApi(page);
  await page.goto(world.personal ? `/#/chats/${S}` : `/#/project/${P}/chat/${S}`);
}
