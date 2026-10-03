import { expect, type Page, type Route } from '@playwright/test';
import { FILM, hubSend, mockApi, newWorld, now, P, S, standardFilm, standardScenes, w } from './videoPanelMock';

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

// Запуск съёмки идёт без sceneId: мок видео берёт сцену из основного объекта контекста
function trackScene(primary: { kind: string; ref: Record<string, unknown> } | null) {
  w().runScene = primary?.kind === 'video-scene' ? String(primary.ref.sceneId) : undefined;
}

export function newCtxWorld(primary: ReturnType<typeof primaryOf> | null = null, refs: unknown[] = []): CtxWorld {
  ctxWorld = { ctx: { revision: 1, primary, refs }, mutations: [] };
  trackScene(primary);
  w().onSceneCreated = sceneId => {
    ctxWorld.ctx.primary = primaryOf('video-scene', { sceneId });
    ctxWorld.ctx.revision++;
    trackScene(ctxWorld.ctx.primary);
    setTimeout(pushCtx, 30);
  };
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
        featureFlags: { 'video-editor': true, 'image-editor': true, 'audio-editor': true, 'chat-context': true }, subsystems: [],
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
      if (p === `${base}/refs` && method === 'POST') {
        // Как у бэкенда: роли кадров принимает только основная сцена
        if (/^frame-[ab]$/.test(body.role) && c.primary?.kind !== 'video-scene') {
          return r.fulfill({ status: 400, json: { error: 'Роль не принимается основным объектом', code: 'role_not_accepted' } });
        }
        c.refs.push({
          id: `rf-${c.refs.length + 1}-${body.role}`, kind: body.kind, ref: body.ref, by: 'human', addedAt: new Date().toISOString(), label: body.ref.path ?? body.ref.threadId,
          version: null, thumb: null, missing: false, role: body.role, usedBy: [],
        });
      } else if (p.startsWith(`${base}/refs/`) && method === 'DELETE') {
        c.refs = (c.refs as { id: string }[]).filter(x => x.id !== p.slice(`${base}/refs/`.length));
      } else if (p === `${base}/primary` && method === 'PUT') {
        c.primary = body.kind === null ? null : primaryOf(body.kind, body.ref);
        trackScene(c.primary);
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

// Манифест модуля «Видео» подключается со страницы: /auth/me мока отдаёт пустой список подсистем и модулей
export async function registerVideo(page: Page) {
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/videoEditor/manifest.tsx');
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-video', core: true, tab: undefined });
  });
}

// Манифесты «Картинок» и «Звука»: без них чипы черновика, куда ведёт передача, не появятся
export async function registerNeighbours(page: Page) {
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    for (const [k, f] of [['e2e-image', 'imageEditor'], ['e2e-audio', 'audioEditor']] as const) {
      const mod = await import(/* @vite-ignore */ `/src/features/${f}/manifest.tsx`);
      core.registerSubsystem({ ...mod.manifest, key: k, core: true, tab: undefined });
    }
  });
}

// Кадры сцены как референсы контекста (роли frame-a/frame-b): без них «Снять» серое — «Нужны оба кадра»
export function frameRefsOf(sceneId: string): unknown[] {
  const sc = w().scenes.find(x => x.sceneId === sceneId);
  return (['frameA', 'frameB'] as const).flatMap(slot => {
    const f = sc?.settings[slot];
    if (!f || f.kind !== 'file') return [];
    const role = slot === 'frameA' ? 'frame-a' : 'frame-b';
    return [{
      id: `rf-${role}`, kind: 'project-file', ref: { path: f.path }, by: 'human' as const, addedAt: now, label: f.path.split('/').pop(),
      version: null, thumb: null, missing: false, role, usedBy: ['shoot'],
    }];
  });
}

export interface OpenVideoOptions {
  vp: { width: number; height: number };
  theme?: 'light' | 'dark';
  feed?: Record<string, unknown>[];
  scenes?: ReturnType<typeof standardScenes>;
  films?: ReturnType<typeof standardFilm>[];
  dsp?: boolean;
  autoFinish?: boolean;
  focus?: { sceneId?: string; filmPath?: string };
  // Кадры основной сцены лежат в контексте референсами frame-a/frame-b (иначе слоты пусты)
  frames?: boolean;
}

// Чат проекта с видео-API мока, контекстом хода и манифестами вертикалей; ждёт строку чипов действий
// primary — фабрика: подпись объекта берётся из мира, который создаётся здесь же
export async function openVideoChat(page: Page, primary: (() => ReturnType<typeof primaryOf>) | null, o: OpenVideoOptions) {
  newWorld({
    scenes: o.scenes ?? standardScenes(), films: o.films ?? [standardFilm()], focus: o.focus ?? {}, feed: o.feed ?? [],
    dsp: o.dsp, autoFinish: o.autoFinish,
  });
  const p = primary ? primary() : null;
  newCtxWorld(p, o.frames && p?.kind === 'video-scene' ? frameRefsOf(String(p.ref.sceneId)) : []);
  await page.setViewportSize(o.vp);
  await page.addInitScript(th => {
    localStorage.setItem('cc_token', 'e2e-token');
    localStorage.setItem('cc_user_id', 'u1');
    localStorage.setItem('theme-mode', th as string);
  }, o.theme ?? 'light');
  await mockApi(page);
  await mockContext(page);
  await page.goto(`/#/project/${P}/chat/${S}`);
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
  await registerVideo(page);
  await registerNeighbours(page);
  if (primary) await expect(page.locator('[data-composer-actions]')).toBeVisible({ timeout: 15_000 });
}
