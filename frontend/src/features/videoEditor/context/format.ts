// Чип «Формат» вида «видео» (ADR-025, макет media-format-v1, сценарии 5, 6 и 10): модель для хоста по
// выбранной сцене. Пропорции и ступени берутся из каталога модели (`VideoModelInfo.format`), действующий
// формат, подмену и цену со временем ступени отдаёт котировка (`VideoQuote.format`, `price.eta`), подрезку
// кадров считает `frames/preview` (dryRun). Своей арифметики формата здесь нет: только выбор строки каталога,
// форматирование времени и сопоставление с замком фильма.

import {
  FORMAT_TEXT, TIER_NAME, getChatContextState, notifyKindChanged,
  type ContextKindCtx, type CropAnchor, type FormatFramesModel, type FormatModel, type FormatTierKey, type FormatTierOption,
  type MediaFormatCaps, type MediaFormatState, type MediaFormatWish, type ResolvedFormat,
} from 'aihome_shell/kit';
import { videoApi, type FrameAnchor, type VideoFramePreview, type VideoFramesPreview, type VideoQuote, type VideoScene } from '../api';
import { changeSettings, currentResolved, forceLockedFormat, setSceneFormat } from '../scene/actions';
import type { ResolvedScene } from '../scene/model';
import { isPersonalScope, videoScope } from '../scope';
import { frameRefOf } from '../store/frameRefs';
import { filmName, getCatalog, getLockAsk, setLockAsk } from '../store/videoStore';
import { sceneOfPrimary, SCENE_KIND } from './state';

const TIERS: readonly FormatTierKey[] = ['standard', 'high', 'max'];
// Потолок кэшей: котировки и превью копятся по ключам (сцена, формат, ревизия контекста)
const CACHE_MAX = 80;

// ── Время: «~5,5 мин», «~45 с» ──

export function etaText(sec: number | undefined | null): string | null {
  if (typeof sec !== 'number' || !(sec > 0)) return null;
  if (sec < 60) return `~${Math.round(sec)} с`;
  // До десяти минут — с половинами («~5,5 мин»), дольше — целые («~15 мин»), как в записке
  const m = sec < 600 ? Math.round(sec / 30) / 2 : Math.round(sec / 60);
  return `~${String(m).replace('.', ',')} мин`;
}

// ── Котировка: действующий формат, подмена и время ступени ──

type Slot<T> = { value?: T; failed?: boolean };
const _quotes = new Map<string, Slot<VideoQuote>>();
const _previews = new Map<string, Slot<VideoFramesPreview>>();

function remember<T>(cache: Map<string, Slot<T>>, key: string): Slot<T> {
  if (cache.size >= CACHE_MAX) cache.delete(cache.keys().next().value as string);
  const slot: Slot<T> = {};
  cache.set(key, slot);
  return slot;
}

const wishKey = (w: MediaFormatWish | null | undefined) => (w ? `${w.mode}:${w.aspect ?? ''}:${w.tier ?? ''}:${w.width ?? ''}x${w.height ?? ''}` : '-');

function quoteFor(ctx: ContextKindCtx, scope: string, r: ResolvedScene, wish: MediaFormatWish | null): VideoQuote | null {
  if (!r.provider || !r.model) return null;
  const revision = getChatContextState(ctx.sessionId).revision;
  const key = [ctx.sessionId, revision, r.provider.key, r.model.id, r.count, r.durationSec, r.sound, wishKey(wish)].join('|');
  const hit = _quotes.get(key);
  if (hit) return hit.value ?? null;
  const slot = remember(_quotes, key);
  void videoApi.quote(scope, ctx.sessionId, {
    sessionId: ctx.sessionId, sceneId: '', provider: r.provider.key, model: r.model.id,
    count: r.count, durationSec: r.durationSec, sound: r.sound, ...(wish ? { format: wish } : {}), contextRevision: revision,
  }).then(q => { slot.value = q; }, () => { slot.failed = true; }).finally(notifyKindChanged);
  return null;
}

// ── Превью подрезки кадров ──

function previewFor(ctx: ContextKindCtx, scope: string, scene: VideoScene, aspect: string, anchor: FrameAnchor): VideoFramesPreview | null {
  const refs = getChatContextState(ctx.sessionId);
  const key = [ctx.sessionId, refs.revision, scene.sceneId, aspect, anchor].join('|');
  const hit = _previews.get(key);
  if (hit) return hit.value ?? null;
  const slot = remember(_previews, key);
  void videoApi.framesPreview(scope, ctx.sessionId, { sessionId: ctx.sessionId, sceneId: scene.sceneId, aspect, anchor })
    .then(p => { slot.value = p; }, () => { slot.failed = true; }).finally(notifyKindChanged);
  return null;
}

// «Кадры A и B — 16:9, сцена — 9:16: кадры будут подрезаны по бокам до модели.» (записка, «Кадры видео»)
export function framesText(target: string, frames: { slot: string; aspect: string; sides: boolean }[]): string {
  const where = (sides: boolean) => (sides ? 'по бокам' : 'сверху и снизу');
  if (frames.length === 2 && frames[0].aspect === frames[1].aspect && frames[0].sides === frames[1].sides) {
    return `Кадры A и B — ${frames[0].aspect}, сцена — ${target}: кадры будут подрезаны ${where(frames[0].sides)} до модели.`;
  }
  return frames.map(f => `Кадр ${f.slot} — ${f.aspect}, сцена — ${target}: кадр будет подрезан ${where(f.sides)} до модели.`).join(' ');
}

function framesModel(
  ctx: ContextKindCtx, scope: string, scene: VideoScene, r: ResolvedScene, target: string,
): FormatFramesModel | undefined {
  const pv = previewFor(ctx, scope, scene, target, r.frameAnchor);
  if (!pv) return undefined;
  const refs = getChatContextState(ctx.sessionId).refs;
  const cropped: { slot: string; aspect: string; sides: boolean; thumb: string | null }[] = [];
  const add = (slot: 'A' | 'B', f: VideoFramePreview | undefined) => {
    if (f?.cropped) cropped.push({ slot, aspect: f.sourceAspect, sides: f.resultWidth < f.sourceWidth, thumb: frameRefOf(refs, slot)?.thumb ?? null });
  };
  add('A', pv.frameA);
  add('B', pv.frameB);
  if (!cropped.length) return undefined;
  return {
    text: framesText(target, cropped),
    previews: cropped.map(c => ({ slot: c.slot, aspect: c.aspect, thumb: c.thumb })),
    target,
    anchor: r.frameAnchor === 'left' || r.frameAnchor === 'right' ? r.frameAnchor : 'center',
    anchors: cropped.some(c => c.sides),
    onAnchor: (a: CropAnchor) => changeSettings(scope, ctx.sessionId, { frameFit: { anchor: a } }, false, scene.sceneId),
  };
}

// ── Ступени ──

function priceOf(q: VideoQuote | null) {
  if (!q) return null;
  const p = q.price;
  if (p.unit === 'free') return { amount: 0, unit: 'free', approx: false };
  return typeof p.amount === 'number' ? { amount: p.amount, unit: p.unit, approx: p.approx } : null;
}

function tierOptions(
  caps: MediaFormatCaps, aspect: string, quotes: Map<FormatTierKey, VideoQuote | null>,
): FormatTierOption[] {
  if (caps.tiers.length === 0) return [];
  return TIERS.map(t => {
    const spec = caps.tiers.find(x => x.tier === t);
    if (!spec) return { tier: t, label: 'нет у модели', disabledReason: `«${TIER_NAME[t]}» нет у модели` };
    const q = quotes.get(t) ?? null;
    const upscale = !!spec.postStep;
    const size = spec.sizes[aspect] ?? null;
    // «Макс» с увеличением: время обоих прогонов в одной подписи; не замерено — так и пишем, число не выдумываем
    const extra = q ? (etaText(q.price.eta) ?? (upscale ? 'время не замерено' : null)) : null;
    return {
      tier: t, label: spec.label, size, wireValue: spec.wireValue, postStep: spec.postStep, price: priceOf(q), extra,
      ...(upscale ? { note: upscaleNote(caps, spec.label, aspect, q) } : null),
    };
  });
}

// «„Макс“ — два прогона на вашей видеокарте: съёмка 1344×768 (~5,5 мин), затем увеличение до 2528×1440 (~15 мин).
// Увеличение можно отменить.» Время съёмки — котировка минус увеличение; нет замера — без скобок
function upscaleNote(caps: MediaFormatCaps, _label: string, aspect: string, q: VideoQuote | null): string {
  const max = caps.tiers.find(t => t.postStep);
  const shot = max ? caps.tiers.find(t => !t.postStep && t.wireValue === max.wireValue) : null;
  const size = (s: { w: number; h: number } | undefined) => (s ? `${s.w}×${s.h}` : '');
  const up = q?.price.upscaleEta;
  const shotSec = q?.price.eta !== undefined && up !== undefined ? q.price.eta - up : undefined;
  const t = (sec: number | undefined) => { const e = etaText(sec); return e ? ` (${e})` : ''; };
  return `«${TIER_NAME.max}» — два прогона на вашей видеокарте: съёмка ${size(shot?.sizes[aspect])}${t(shotSec)}, затем увеличение до ${size(max?.sizes[aspect])}${t(up)}. Увеличение можно отменить.`;
}

// ── Запись желания ──

const humanWish = (w: Omit<MediaFormatWish, 'by' | 'at'>): MediaFormatWish => ({ ...w, by: 'human', at: new Date().toISOString() });

// Правка одного из двух (пропорции или ступень) берёт второе из текущего желания, затем из ответа котировки
function mergeWish(cur: MediaFormatWish | null, resolved: ResolvedFormat | undefined, caps: MediaFormatCaps, w: Omit<MediaFormatWish, 'by' | 'at'>): Omit<MediaFormatWish, 'by' | 'at'> {
  if (w.mode !== 'aspect') return w;
  return {
    mode: 'aspect',
    aspect: w.aspect ?? cur?.aspect ?? resolved?.aspect ?? caps.aspects[0] ?? null,
    tier: w.tier ?? cur?.tier ?? resolved?.tier ?? 'standard',
  };
}

// ── Сборка модели ──

export function videoFormat(ctx: ContextKindCtx, actionId: string | null): FormatModel | null {
  const state = getChatContextState(ctx.sessionId);
  if (!state.primary || state.primary.kind !== SCENE_KIND) return null;
  // Чип у «Снять» и в «Чате»; у меню кадров и редакторов формата нет
  if (actionId && actionId !== 'shoot') return null;
  const scene = sceneOfPrimary(ctx.sessionId, state.primary);
  const scope = videoScope(ctx.projectId);
  const catalog = getCatalog(scope);
  if (!scene || !catalog) return null;
  const r = currentResolved(ctx.sessionId, scope, scene);
  const personal = isPersonalScope(scope);
  const filmLabel = scene.lockedBy ? filmName(scene.lockedBy.filmPath) : scene.filmRef ? filmName(scene.filmRef.path) : null;
  const title = filmLabel ? `Формат сцены · фильм «${filmLabel}»` : 'Формат сцены';
  const caps = r.model?.format ?? null;
  const by = r.format?.by ?? null;

  if (!caps || !r.provider || !r.model) {
    const none: MediaFormatState = { caps: null, wish: r.format, resolved: { mode: 'model', substituted: false, rounded: false }, tiers: [] };
    return {
      kind: 'generate', title, state: none, by: null,
      chip: { prefix: FORMAT_TEXT.chat, main: FORMAT_TEXT.asModel, tone: 'dim' },
      footer: () => '',
      ...(r.model && r.provider ? { hint: FORMAT_TEXT.noCaps(r.provider.label, r.model.label) } : null),
      onChange: () => {},
    };
  }

  const wish = r.format;
  const q = quoteFor(ctx, scope, r, wish);
  const resolved: ResolvedFormat = q?.format ?? (wish
    ? { mode: wish.mode, aspect: wish.aspect ?? null, tier: wish.tier ?? null, substituted: false, rounded: false }
    : { mode: 'model', substituted: false, rounded: false });
  const aspect = resolved.aspect ?? (caps.aspects[0] ?? '');

  // Время и цена каждой ступени — котировка с этой ступенью; текущая берётся из ответа по желанию
  const tierQuotes = new Map<FormatTierKey, VideoQuote | null>();
  for (const spec of caps.tiers) {
    tierQuotes.set(spec.tier, quoteFor(ctx, scope, r, humanWish({ mode: 'aspect', aspect, tier: spec.tier })));
  }
  const tiers = tierOptions(caps, aspect, tierQuotes);
  const fmt: MediaFormatState = { caps, wish, resolved, tiers };

  const tierLabel = resolved.tier ? caps.tiers.find(t => t.tier === resolved.tier)?.label : null;
  const lock = scene.lockedBy;
  const mismatchText = scene.formatMismatch && lock && filmLabel
    ? `Сцена в ${aspect}, а фильм «${filmLabel}» — ${lock.aspect}. При склейке пойдут чёрные полосы.`
    : undefined;
  const ask = getLockAsk(ctx.sessionId, scene.sceneId);
  const frames = resolved.aspect ? framesModel(ctx, scope, scene, r, resolved.aspect) : undefined;

  return {
    kind: 'generate', title, state: fmt, by,
    chip: {
      main: resolved.mode === 'model' ? FORMAT_TEXT.asModel : aspect,
      ...(resolved.mode !== 'model' && tierLabel ? { sub: `· ${tierLabel}`, subShort: `· ${tierLabel}` } : null),
      ...(resolved.mode === 'model' ? { prefix: FORMAT_TEXT.chat } : null),
      tone: resolved.mode === 'model' ? 'dim' : mismatchText ? 'warn' : 'normal',
    },
    footer: name => {
      if (filmLabel) return `Один формат на все сцены фильма «${filmLabel}».`;
      const who = personal || !name ? 'личных чатов' : `проекта «${name}»`;
      return `Запомнится для видео ${who}${by === 'agent' ? ' · поставил Claude ✦' : ''}.`;
    },
    ...(lock && filmLabel
      ? { locked: { filmPath: lock.filmPath, aspect: lock.aspect, note: `Формат фильма «${filmLabel}» — ${lock.aspect}, его задала сцена 1. Остальные сцены следуют за ней, иначе при склейке пойдут чёрные полосы.` } }
      : null),
    ...(ask
      ? {
        lockAsk: {
          text: ask.text || `Фильм «${filmLabel ?? ''}» — ${ask.lockedBy.aspect}. Сцена в ${ask.aspect} даст чёрные полосы по бокам при склейке.`,
          keepLabel: `Оставить ${ask.lockedBy.aspect}`, forceLabel: `Всё равно ${ask.aspect}`,
          onKeep: () => setLockAsk(ctx.sessionId, null),
          onForce: () => { void forceLockedFormat(scope, ctx.sessionId, ask); },
        },
      }
      : null),
    ...(mismatchText && lock
      ? {
        mismatch: mismatchText,
        mismatchAction: {
          label: `Привести к ${lock.aspect}`,
          onClick: () => setSceneFormat(scope, ctx.sessionId, scene.sceneId, humanWish({ mode: 'aspect', aspect: lock.aspect, tier: resolved.tier ?? wish?.tier ?? 'standard' })),
        },
      }
      : null),
    ...(frames ? { frames } : null),
    onChange: w => setSceneFormat(scope, ctx.sessionId, scene.sceneId, humanWish(mergeWish(wish, q?.format, caps, w))),
  };
}
