import type { Page, Route } from '@playwright/test';
import { CATALOG, hubSend, P, pushThreads, S, w } from './videoPanelMock';

// Слой «Формат» поверх мока «Видео» (videoPanelMock) и мока контекста (contextVideoMock): каталог со ступенями,
// котировка с форматом и временем ступени, замок фильма (409 film_format_locked), превью подрезки кадров и
// увеличение «Макс» local H3 (стадия upscaling, отмена, «Повторить»). Регистрируется ПОСЛЕ остальных моков
// (последний маршрут Playwright срабатывает первым), чужие пути отдаёт дальше через fallback.

const sizes = (a: number, b: number) => ({ '16:9': { w: a, h: b }, '9:16': { w: b, h: a } });

export const H3_CAPS = {
  modelLabel: 'MiniMax H3', aspects: ['16:9', '9:16'], wireParam: 'size',
  tiers: [
    { tier: 'standard', label: '480p', sizes: sizes(864, 480), wireValue: 'half' },
    { tier: 'high', label: '768p', sizes: sizes(1344, 768), wireValue: 'full' },
    { tier: 'max', label: '1440p', sizes: sizes(2528, 1440), wireValue: 'full', postStep: 'upscale' },
  ],
};
export const VEO_CAPS = {
  modelLabel: 'Veo 3.1', aspects: ['16:9', '9:16'], wireParam: 'resolution',
  tiers: [{ tier: 'standard', label: '720p', sizes: sizes(1280, 720), wireValue: '720p' }],
};
// Модель без размеров: «Как у модели» (сценарий 9 макета)
const CAPS: Record<string, typeof H3_CAPS | undefined> = { 'local/minimax-h3': H3_CAPS, 'fal/veo-3.1': VEO_CAPS };

// Время ступеней local H3 для ролика 5 с: съёмка 480p — 90 с, 768p — 330 с, «Макс» — 768p плюс увеличение 915 с
const ETA: Record<string, number> = { standard: 90, high: 330, max: 330 + 915 };
export const UPSCALE_ETA = 915;

export function applyFormatCatalog() {
  for (const p of CATALOG.providers) {
    for (const m of p.models as { id: string; format?: unknown }[]) m.format = CAPS[`${p.key}/${m.id}`];
  }
}

export interface FmtWorld {
  settingsPuts: { settings: Record<string, unknown>; force?: boolean }[];
  previews: Record<string, unknown>[];
  cancelUpscales: string[];
  retries: { sceneId: string; versionId: string }[];
  // Исходные кадры сцены: пропорции для превью подрезки
  sourceAspect: string;
}
let fw: FmtWorld;
export const fmt = () => fw;
export const newFmtWorld = (): FmtWorld => (fw = { settingsPuts: [], previews: [], cancelUpscales: [], retries: [], sourceAspect: '16:9' });

const ratio = (a: string) => { const [x, y] = a.split(':').map(Number); return x / y; };
const nearest = (aspects: string[], want: string) =>
  aspects.reduce((b, a) => (Math.abs(Math.log(ratio(a) / ratio(want))) < Math.abs(Math.log(ratio(b) / ratio(want))) ? a : b), aspects[0]);

type Body = Record<string, unknown>;
const sceneById = (id: string) => w().scenes.find(s => s.sceneId === id) as (ReturnType<typeof w>['scenes'][number] & Record<string, unknown>) | undefined;

function quote(b: Body) {
  w().quotes.push(b);
  const key = `${b.provider}/${b.model}`;
  const caps = CAPS[key];
  // Без желания в запросе сервер берёт настройки основной сцены контекста (в том числе зеркало aspect)
  const own = w().runScene ? sceneById(w().runScene!)?.settings as { format?: { aspect?: string; tier?: string }; aspect?: string } | undefined : undefined;
  const wish = (b.format ?? own?.format ?? (own?.aspect ? { aspect: own.aspect } : undefined)) as { aspect?: string; tier?: string } | undefined;
  const local = b.provider === 'local';
  const count = (b.count as number) ?? 1;
  let format: Record<string, unknown> = { mode: 'model', substituted: false, rounded: false };
  let tier = wish?.tier ?? (local ? 'high' : 'standard');
  if (caps && wish?.aspect) {
    const aspect = caps.aspects.includes(wish.aspect) ? wish.aspect : nearest(caps.aspects, wish.aspect);
    const spec = caps.tiers.find(t => t.tier === tier) ?? caps.tiers[0];
    tier = spec.tier;
    const size = (spec.sizes as Record<string, { w: number; h: number }>)[aspect];
    const substituted = aspect !== wish.aspect;
    format = {
      mode: 'aspect', aspect, tier, width: size.w, height: size.h, wireValue: spec.wireValue, ...('postStep' in spec ? { postStep: spec.postStep } : null),
      substituted, rounded: false, ...(substituted ? { note: `${wish.aspect} нет у ${caps.modelLabel} — взяли ${aspect}.` } : null),
    };
  }
  const max = local && tier === 'max';
  return {
    quoteId: `q${w().quotes.length}`, provider: b.provider, model: b.model, count, durationSec: b.durationSec ?? 8, format,
    price: local
      ? { unit: 'free', approx: false, source: 'local', eta: ETA[tier] ?? 330, queueLength: 0, ...(max ? { upscaleEta: UPSCALE_ETA, postStep: 'upscale' } : null) }
      : b.provider === 'higgsfield' ? { amount: 16 * count, unit: 'credits', approx: false, source: 'get_cost' }
        : { amount: 1.6 * count, unit: 'usd', approx: true, source: 'pricing', eta: 240 },
    license: 'watermark', heavy: false, expiresAt: '2030-01-01T00:00:00Z',
  };
}

// Съёмка 768p как версия с пометкой «увеличение не удалось» (отмена или сбой)
export function shotWithoutUpscale(sceneId: string, jobId: string) {
  const s = sceneById(sceneId);
  if (!s) return;
  const launch = (s.launches as Body[]).find(l => l.jobId === jobId);
  if (launch) launch.status = 'done';
  const n = s.versions.length + 1;
  s.versions.push({
    versionId: `ver-${jobId}-${n}`, number: n, jobId, variant: 0, provider: 'local', model: 'minimax-h3', durationSec: 5, sizeBytes: 36026, hasSound: true,
    cost: { currency: 'local', amount: 0 }, initiator: 'human', inputs: { text: 'т' }, createdAt: '2026-10-07T10:00:00Z', width: 1344, height: 768,
    upscaleError: 'Увеличение до 1440p не удалось: отменено',
  });
  s.currentVersionId = s.versions[n - 1].versionId as string;
  w().revision++;
  hubSend({ type: 'video_edit_completed', sessionId: S, scopeKey: P, jobId, sceneId, variants: [1], initiator: 'human' });
  pushThreads();
}

// Удачное увеличение: новая версия 1440p без пометки сбоя
export function upscaleDone(sceneId: string, jobId: string) {
  const s = sceneById(sceneId);
  if (!s) return;
  const launch = (s.launches as Body[]).find(l => l.jobId === jobId);
  if (launch) launch.status = 'done';
  const n = s.versions.length + 1;
  s.versions.push({
    versionId: `ver-${jobId}-${n}`, number: n, jobId, variant: 0, provider: 'local', model: 'minimax-h3', durationSec: 5, sizeBytes: 36026, hasSound: true,
    cost: { currency: 'local', amount: 0 }, initiator: 'human', inputs: { text: 'т' }, createdAt: '2026-10-07T10:30:00Z', width: 2528, height: 1440,
  });
  s.currentVersionId = s.versions[n - 1].versionId as string;
  w().revision++;
  hubSend({ type: 'video_edit_completed', sessionId: S, scopeKey: P, jobId, sceneId, variants: [1], initiator: 'human' });
  pushThreads();
}

export function progress(sceneId: string, jobId: string, extra: Body) {
  hubSend({ type: 'video_edit_progress', sessionId: S, scopeKey: P, jobId, sceneId, variant: 1, count: 1, initiator: 'human', stage: 'running', ...extra });
}

export async function mockFormat(page: Page) {
  const vb = `/projects/${P}/video-editor`;
  const cb = `${vb}/sessions/${S}`;
  await page.route(u => u.pathname.startsWith('/api/'), async (r: Route) => {
    const url = new URL(r.request().url());
    const p = decodeURIComponent(url.pathname.replace(/^\/api/, ''));
    const method = r.request().method();
    const json = (body: unknown, status = 200) => r.fulfill({ status, json: body });
    const body = () => (r.request().postDataJSON() ?? {}) as Body;

    if (p === `${vb}/quote` && method === 'POST') return json(quote(body()));

    // Замок формата фильма держит сервер: смена пропорций сцены фильма без force — 409
    const sm = /\/scenes\/([^/]+)\/settings$/.exec(p);
    if (sm && p.startsWith(cb) && method === 'PUT') {
      const b = body() as { settings: Record<string, unknown> & { format?: { aspect?: string }; aspect?: string }; force?: boolean; revision: number };
      fw.settingsPuts.push({ settings: b.settings, force: b.force });
      const s = sceneById(sm[1]);
      const lock = s?.lockedBy as { filmPath: string; aspect: string } | undefined;
      const want = b.settings.format?.aspect ?? b.settings.aspect;
      if (s && lock && want && want !== lock.aspect && !b.force) {
        return json({ error: `Фильм «утро-в-горах» — ${lock.aspect}. Сцена в ${want} даст чёрные полосы по бокам при склейке.`, code: 'film_format_locked', lockedBy: lock }, 409);
      }
      if (s && lock) s.formatMismatch = !!want && want !== lock.aspect;
      // Метка строки фильма — по сцене на этой позиции
      const film = s?.filmRef ? w().films.get((s.filmRef as { path: string }).path) : undefined;
      if (s && film) {
        const pos = (s.filmRef as { position: number }).position;
        film.marks = film.marks.filter(m => m.index !== pos);
        if (s.formatMismatch) film.marks.push({ index: pos, claude: false, updated: false, stale: false, formatMismatch: true } as never);
        film.revision = `r${Number(film.revision.slice(1)) + 1}`;
        setTimeout(() => hubSend({ type: 'video_film_changed', scopeKey: P, path: film.path, state: film }), 40);
      }
      return r.fallback();
    }

    if (p === `${vb}/frames/preview` && method === 'POST') {
      const b = body() as { aspect?: string; anchor?: string };
      fw.previews.push(b);
      const target = b.aspect ?? '16:9';
      const src = fw.sourceAspect;
      const [sw, sh] = src === '16:9' ? [1920, 1080] : [1080, 1920];
      const cropped = ratio(target) !== ratio(src);
      const frame = () => ({
        sourceWidth: sw, sourceHeight: sh, sourceAspect: src, cropped,
        resultWidth: cropped ? (ratio(target) < ratio(src) ? Math.round(sh * ratio(target)) : sw) : sw,
        resultHeight: cropped ? (ratio(target) < ratio(src) ? sh : Math.round(sw / ratio(target))) : sh,
        x: 0, y: 0, w: 1, h: 1,
      });
      return json({ aspect: target, anchor: b.anchor ?? 'center', frameA: frame(), frameB: frame() });
    }

    const cu = /\/jobs\/([^/]+)\/upscale$/.exec(p);
    if (cu && method === 'DELETE') {
      fw.cancelUpscales.push(cu[1]);
      const s = w().scenes.find(x => (x.launches as Body[]).some(l => l.jobId === cu[1]));
      if (s) shotWithoutUpscale(s.sceneId, cu[1]);
      return json({ jobId: cu[1], status: 'completed' });
    }

    const ru = /\/scenes\/([^/]+)\/versions\/([^/]+)\/upscale$/.exec(p);
    if (ru && method === 'POST') {
      fw.retries.push({ sceneId: ru[1], versionId: ru[2] });
      const s = sceneById(ru[1]);
      const jobId = `job-retry-${fw.retries.length}`;
      if (s) {
        (s.launches as Body[]).push({ jobId, at: '2026-10-07T10:20:00Z', status: 'running', interrupted: false, initiator: 'human', provider: 'local', model: 'minimax-h3', count: 1 });
        w().revision++;
        pushThreads();
        setTimeout(() => progress(s.sceneId, jobId, { stage: 'upscaling', etaSeconds: UPSCALE_ETA }), 60);
      }
      return json({ jobId, sourceVersionId: ru[2] }, 202);
    }

    return r.fallback();
  });
}
