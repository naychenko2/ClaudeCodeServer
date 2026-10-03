import type { Page, Route, WebSocketRoute } from '@playwright/test';

// Мок API для context-row.spec.ts: бэкенд не нужен, собранный dist раздаётся статикой, а /api/**
// и хабы — моки. Контекст чата (ADR-023) ведёт мир теста: мутации меняют ревизию и рассылают
// событие chat_context_changed, как на бою. ЖИВОГО API КОНТЕКСТА ещё нет — проверка на моке.

export const P = 'proj-ctx';
export const S = 'chat-ctx';
export const now = new Date('2026-10-03T10:00:00Z').toISOString();

type Item = { id: string; kind: string; ref: Record<string, unknown>; by: 'human' | 'agent'; addedAt: string; label: string; version: string | null; thumb: string | null; missing: boolean };
export type Primary = Item & { role: null };
export type Ref = Item & { role: string | null; usedBy: string[] };
export interface Ctx { revision: number; primary: Primary | null; refs: Ref[] }

export const primary = (over: Partial<Primary> = {}): Primary =>
  ({ id: 'p1', kind: 'image', ref: { threadId: 'thread-hero', versionId: 'v2' }, by: 'human', addedAt: now, label: 'hero.png', version: 'v2', thumb: null, missing: false, role: null, ...over });
export const ref = (id: string, label: string, over: Partial<Ref> = {}): Ref =>
  ({ id, kind: 'image', ref: { path: `assets/${label}` }, by: 'human', addedAt: now, label, version: null, thumb: null, missing: false, role: 'style', usedBy: ['edit'], ...over });

export interface World {
  ctx: Ctx;
  flags: Record<string, boolean>;
  hands: boolean;
  savedFiles: { path: string; threadKind: string; savedAt: string }[];
  mutations: { method: string; path: string; body: unknown }[];
  invocations: { target: string; args: unknown[] }[];
  hubs: WebSocketRoute[];
  personal: boolean;
}

let world: World;
export const w = () => world;

export function newWorld(o: Partial<Pick<World, 'flags' | 'hands' | 'savedFiles' | 'personal'>> & { ctx?: Partial<Ctx> } = {}): World {
  world = {
    ctx: { revision: 1, primary: null, refs: [], ...o.ctx },
    flags: { 'composer-context-row': true, 'chat-context': true, ...o.flags },
    hands: o.hands ?? true, savedFiles: o.savedFiles ?? [], mutations: [], invocations: [], hubs: [], personal: o.personal ?? false,
  };
  return world;
}

export function hubSend(msg: Record<string, unknown>) {
  for (const h of world.hubs) h.send(JSON.stringify({ type: 1, target: 'message', arguments: [msg] }) + '\u001e');
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
  await page.route('**/hubs/**', async (r: Route) => {
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
  await page.route('**/api/**', async (r: Route) => {
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
      return json([{ kind: 'user_message', text: 'Поправь hero', timestamp: Date.parse(now) - 60_000 }]);
    }
    if (p === `/chats/${S}` || p === `/projects/${P}/sessions/${S}`) return json(SESSION());
    if (p === '/chats' && method === 'GET') return json([SESSION()]);
    if (p === `/projects/${P}/sessions` || p === `/projects/${P}/chats`) return json(world.personal ? [] : [SESSION()]);
    if (p === `/projects/${P}/git/status`) return json(GIT);
    if (p === `/sessions/${S}/hands-status`) return json({ state: 'active', reason: null, deviceName: 'Рабочий ПК' });
    // ── контекст чата ──
    const base = `/chats/${S}/context`;
    if (p === base && method === 'GET') return json(world.ctx);
    if (p === `${base}/saved-files`) return json(world.savedFiles);
    if (p.startsWith(base)) {
      const body = r.request().postData() ? r.request().postDataJSON() : null;
      world.mutations.push({ method, path: p.slice(base.length) || '/', body });
      const c = world.ctx;
      if (p === `${base}/primary` && method === 'PUT') {
        c.primary = body.kind === null ? null : primary({ kind: body.kind, ref: body.ref, label: 'hero.png', version: 'v2' });
      } else if (p === `${base}/refs` && method === 'POST') {
        c.refs = [...c.refs, ref(`r${c.refs.length + 1}`, String((body.ref as { path?: string }).path ?? 'ref').split('/').pop()!, { role: body.role ?? null })];
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
