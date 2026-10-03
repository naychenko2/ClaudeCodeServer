import type { Page, Route } from '@playwright/test';
import { FILM, hubSend, now, P, S, w } from './videoPanelMock';

// Слой «контекст хода» поверх мока «Видео» (videoPanelMock): контекст чата (ADR-023) ведёт свой мир, видео-API,
// хабы и лента — прежний мок. Регистрируется ПОСЛЕ mockApi (последний маршрут Playwright срабатывает первым),
// чужие пути отдаёт дальше через fallback. Манифест модуля подключается со страницы (registerVideo), поэтому
// /auth/me отдаёт пустой список подсистем и модулей — иначе прежний мок подтянул бы собранный remote второй раз.

type Item = { id: string; kind: string; ref: Record<string, unknown>; by: 'human' | 'agent'; addedAt: string; label: string; version: string | null; thumb: string | null; missing: boolean };
export interface CtxWorld {
  ctx: { revision: number; primary: (Item & { role: null }) | null; refs: unknown[] };
  mutations: { method: string; path: string; body: unknown }[];
}

let ctxWorld: CtxWorld;
export const cw = () => ctxWorld;

const filmName = (p: string) => (p.split('/').pop() ?? p).replace(/\.film$/i, '');

function labelOf(kind: string, ref: Record<string, unknown>): string {
  if (kind === 'video-film') return filmName(String(ref.filmPath ?? FILM));
  const s = w().scenes.find(x => x.sceneId === ref.sceneId);
  return s?.name ?? 'Сцена';
}

export const primaryOf = (kind: string, ref: Record<string, unknown>, by: 'human' | 'agent' = 'human') => ({
  id: `p-${kind}`, kind, ref, by, addedAt: now, label: labelOf(kind, ref), version: null, thumb: null, missing: false, role: null as null,
});

export function newCtxWorld(primary: ReturnType<typeof primaryOf> | null = null): CtxWorld {
  ctxWorld = { ctx: { revision: 1, primary, refs: [] }, mutations: [] };
  return ctxWorld;
}

const pushCtx = () => hubSend({ type: 'chat_context_changed', sessionId: S, context: ctxWorld.ctx });

export async function mockContext(page: Page) {
  await page.route(u => u.pathname.startsWith('/api/'), async (r: Route) => {
    const url = new URL(r.request().url());
    const p = decodeURIComponent(url.pathname.replace(/^\/api/, ''));
    const method = r.request().method();
    const json = (body: unknown) => r.fulfill({ json: body });
    if (p === '/auth/me') {
      return json({
        id: 'u1', userId: 'u1', username: 'admin', displayName: 'Андрей', role: 'admin', executionEnvironment: 'local',
        featureFlags: { 'video-editor': true, 'image-editor': true, 'audio-editor': true, 'composer-context-row': true, 'chat-context': true }, subsystems: [],
      });
    }
    if (p === '/subsystem-modules') return json({ items: [] });
    const base = `/chats/${S}/context`;
    if (p === `${base}/saved-files`) return json([]);
    if (p === base || p.startsWith(`${base}/`)) {
      if (method === 'GET') return json(ctxWorld.ctx);
      const body = r.request().postData() ? r.request().postDataJSON() : null;
      ctxWorld.mutations.push({ method, path: p.slice(base.length) || '/', body });
      const c = ctxWorld.ctx;
      if (p === `${base}/primary` && method === 'PUT') {
        c.primary = body.kind === null ? null : primaryOf(body.kind, body.ref);
      } else if (p === base && method === 'DELETE') {
        c.primary = null; c.refs = [];
      }
      c.revision++;
      setTimeout(pushCtx, 20);
      return json(c);
    }
    return r.fallback();
  });
}

export const primaryPuts = () => ctxWorld.mutations.filter(m => m.method === 'PUT' && m.path === '/primary').map(m => m.body as { kind: string | null; ref: Record<string, unknown> });
export { FILM, P };
