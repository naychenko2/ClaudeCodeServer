// Чип «Формат» вида «картинка» (ADR-025, макет media-format-v1): модель для хоста по текущему действию.
// Данные приходят с сервера (ручка …/image-editor/format, желание — в префах проекта); здесь кэш ответа,
// запись желания, память «других пропорций» правки и сборка FormatModel. Арифметики формата нет:
// размеры, округление и «ближайшее» считает сервер. Исключения, и только они:
//  — геометрия превью и «дорисовать» (поля холста по исходнику и пропорции, та же формула, что
//    OutpaintSpec.ForAspect на сервере);
//  — `aspectLabel`: подпись исходника правки («4:3» у файла 400×300), а не формат — в модель не уходит.

import {
  FLAGS, FORMAT_TEXT, generateChip, getChatContextState, getFlag, notifyKindChanged, objectKey, presetAction,
  type CropAnchor, type FormatEditModel, type FormatModel, type MediaFormatState, type MediaFormatWish, type ContextKindCtx,
} from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditOp } from '../api';
import { enterScope, isPersonalScope } from '../scope';
import { getCatalog } from '../thread/catalog';
import { threadName } from '../thread/model';
import { formatSig, getPrefs, prefsSettled, setPrefs } from '../thread/prefs';
import { getThreadMarks, mutate } from '../thread/threadStore';
import { threadsApi, type ImageThread } from '../thread/threadsApi';
import { activeSrc, loadImage, resolveModel } from '../thread/useThreadLaunch';
import { settingsOf } from './executors';
import { quoteAction } from './run';
import { editOf, resetEdit, setEdit } from './formatChoice';
import { actionOf, IMAGE_KIND, threadOfPrimary } from './state';

// ── Ответ ручки: кэш по (область, поставщик, модель, операция, эпоха) ──
// Эпоха растёт, когда желание изменилось: своя запись — после того, как префы дошли до сервера (он считает формат
// по сохранённому желанию), чужая (агент, другая вкладка) — сразу по событию префов

const _state = new Map<string, MediaFormatState>();
const _last = new Map<string, MediaFormatState>();
const _loading = new Set<string>();
const _epoch = new Map<string, number>();
const _settling = new Set<string>();
const _seen = new Map<string, string>();

// Сервер недоступен или ручки нет: чип честно показывает «Как у модели» и не долбит запросами
const FALLBACK: MediaFormatState = { caps: null, wish: null, resolved: { mode: 'model', substituted: false, rounded: false }, tiers: [] };

function bumpEpoch(scope: string) {
  _epoch.set(scope, (_epoch.get(scope) ?? 0) + 1);
  notifyKindChanged();
}

function stateFor(scope: string, provider: string, model: string, op: ImageEditOp): MediaFormatState | null {
  const base = `${scope}|${provider}|${model}|${op}`;
  const key = `${base}|${_epoch.get(scope) ?? 0}`;
  const hit = _state.get(key);
  if (hit) return hit;
  if (!_loading.has(key)) {
    _loading.add(key);
    void imageEditorApi().format(scope, { provider, model, op })
      .then(s => { _state.set(key, s); _last.set(base, s); }, () => { _state.set(key, FALLBACK); })
      .finally(() => { _loading.delete(key); notifyKindChanged(); });
  }
  // Пока грузится новый ответ, показываем прежний: чип не мигает
  return _last.get(base) ?? null;
}

// Чужая правка желания (агент через MCP, другая вкладка): префы обновились без нас — ответ ручки устарел
function watchWish(scope: string) {
  const sig = formatSig(getPrefs(scope).format);
  const prev = _seen.get(scope);
  if (prev !== sig) {
    _seen.set(scope, sig);
    if (prev !== undefined && !_settling.has(scope)) bumpEpoch(scope);
  }
}

// Запись желания человека: в префы проекта сразу (оптимистично), ответ ручки — когда PUT дошёл
export function setWish(scope: string, wish: Omit<MediaFormatWish, 'by' | 'at'>) {
  _settling.add(scope);
  setPrefs(scope, { format: { ...wish, by: 'human', at: new Date().toISOString() } });
  void prefsSettled(scope).then(() => { _settling.delete(scope); bumpEpoch(scope); });
}


// ── Исходник правки: размер читаем из картинки (или из холста редактора) ──

const _sizes = new Map<string, { w: number; h: number } | null>();
const KNOWN_ASPECTS = ['1:1', '4:3', '3:4', '3:2', '2:3', '16:9', '9:16', '21:9', '5:4', '4:5'];

const gcd = (a: number, b: number): number => (b ? gcd(b, a % b) : a);

// Подпись пропорции исходника: известная из списка в пределах 2 %, иначе сокращённая дробь
export function aspectLabel(w: number, h: number): string {
  const r = w / h;
  const hit = KNOWN_ASPECTS.find(a => { const [x, y] = a.split(':').map(Number); return Math.abs(r / (x / y) - 1) <= 0.02; });
  if (hit) return hit;
  const g = gcd(w, h);
  return `${w / g}:${h / g}`;
}

function sourceSize(scope: string, thread: ImageThread): { w: number; h: number } | null {
  const marks = getThreadMarks(thread.id).size;
  if (marks) return marks;
  const src = activeSrc(scope, thread);
  if (!src) return null;
  if (_sizes.has(src)) return _sizes.get(src) ?? null;
  _sizes.set(src, null);
  void loadImage(src).then(img => { _sizes.set(src, { w: img.naturalWidth, h: img.naturalHeight }); notifyKindChanged(); }, () => { /* картинки нет: исходник без размера */ });
  return null;
}

// Холст «дорисовать»: растёт по недостающей стороне до пропорции (как OutpaintSpec.ForAspect на сервере)
export function outpaintSize(w: number, h: number, aspect: string): { w: number; h: number } | null {
  const [a, b] = aspect.split(':').map(Number);
  if (!(a > 0 && b > 0)) return null;
  const target = a / b;
  return w / h < target ? { w: Math.round(h * target), h } : { w, h: Math.round(w / target) };
}

// Размер обрезки (dryRun) и цена «дорисовать» — кэш на (нить, версия, пропорции)
const _cropSize = new Map<string, string | null>();
const _outQuote = new Map<string, string | null>();

const versionKey = (thread: ImageThread) => `${thread.id}|${thread.currentVersionId ?? ''}|${thread.currentStepId ?? ''}`;

function cropSizeOf(scope: string, sessionId: string, thread: ImageThread, aspect: string, anchor: CropAnchor): string | null {
  const key = `${versionKey(thread)}|${aspect}|${anchor}`;
  if (_cropSize.has(key)) return _cropSize.get(key) ?? null;
  _cropSize.set(key, null);
  void threadsApi.crop(scope, sessionId, thread.id, { aspect, anchor, dryRun: true })
    .then(r => { _cropSize.set(key, `${r.width}×${r.height}`); notifyKindChanged(); }, () => { /* размер не покажем, обрезка от этого не ломается */ });
  return null;
}

function outpaintPrice(ctx: ContextKindCtx, thread: ImageThread, modelKey: string): string | null {
  const key = `${versionKey(thread)}|${modelKey}`;
  if (_outQuote.has(key)) return _outQuote.get(key) ?? null;
  _outQuote.set(key, null);
  void quoteAction(ctx, { op: 'outpaint', text: '', params: {}, contextRevision: getChatContextState(ctx.sessionId).revision })
    .then(q => { _outQuote.set(key, q.price); notifyKindChanged(); }, () => { /* цены нет — строка без неё */ });
  return null;
}

// ── Действие и «Обрезать» ──

function switchAction(ctx: ContextKindCtx, actionId: string) {
  const primary = getChatContextState(ctx.sessionId).primary;
  if (primary) presetAction(ctx.sessionId, objectKey(primary), { actionId });
}

async function cropNow(ctx: ContextKindCtx, scope: string, thread: ImageThread) {
  const { aspect, anchor } = editOf(thread.id);
  if (!aspect) return;
  setEdit(thread.id, { how: 'crop', cropping: true });
  const done = await mutate(scope, ctx.sessionId, async () => (await threadsApi.crop(scope, ctx.sessionId, thread.id, { aspect, anchor })).state);
  if (done) resetEdit(thread.id);
  else setEdit(thread.id, { how: null, cropping: false });
}

// ── Сборка модели ──

const projectNoun = (scope: string, projectName: string | null, by: string | null) =>
  `Запомнится для картинок ${isPersonalScope(scope) || !projectName ? 'личных чатов' : `проекта «${projectName}»`}${by === 'agent' ? ' · поставил Claude ✦' : ''}.`;

// Правка одного из двух (пропорции или ступень) берёт второе из оптимистичного желания в префах, затем из ответа
// ручки: быстрые клики подряд не теряют друг друга, пока ответ ещё не пришёл
function mergeWish(scope: string, wish: Omit<MediaFormatWish, 'by' | 'at'>, state: MediaFormatState | null): Omit<MediaFormatWish, 'by' | 'at'> {
  if (wish.mode !== 'aspect') return wish;
  const cur = getPrefs(scope).format;
  return {
    mode: 'aspect',
    aspect: wish.aspect ?? cur?.aspect ?? state?.resolved.aspect ?? state?.caps?.aspects[0] ?? null,
    tier: wish.tier ?? cur?.tier ?? state?.resolved.tier ?? 'standard',
  };
}

export function imageFormat(ctx: ContextKindCtx, actionId: string | null): FormatModel | null {
  const primary = getChatContextState(ctx.sessionId).primary;
  if (!primary || primary.kind !== IMAGE_KIND) return null;
  const thread = threadOfPrimary(ctx.sessionId, primary);
  if (!thread) return null;
  const scope = enterScope(ctx.projectId, ctx.sessionId);
  const catalog = getCatalog(scope);
  if (!catalog) return null;
  const found = actionId ? actionOf(ctx, actionId) : null;
  if (actionId && (!found || found.action.kind !== 'run')) return null;
  const op = (found?.action.op ?? 'generate') as ImageEditOp;
  watchWish(scope);

  if (op === 'upscale') {
    return {
      kind: 'readonly', title: `Формат · ${threadName(thread)}`, state: null, by: null,
      chip: { main: FORMAT_TEXT.upscaleChip, subShort: '', tone: 'dim' },
      readonlyNote: FORMAT_TEXT.upscaleNote, footer: () => '', onChange: () => {},
    };
  }
  if (op === 'removeBackground' || op === 'enhanceFaces') {
    return {
      kind: 'readonly', title: `Формат · ${threadName(thread)}`, state: null, by: null,
      chip: { main: FORMAT_TEXT.asSource, tone: 'dim' },
      readonlyNote: op === 'removeBackground' ? 'У «Убрать фон» размер всегда как у исходника.' : 'У «Улучшить лица» размер всегда как у исходника.',
      footer: () => '', onChange: () => {},
    };
  }

  const { pv, m } = resolveModel(catalog, settingsOf(scope, thread, op));
  if (!pv || !m) return null;
  const state = stateFor(scope, pv.key, m.id, op);

  if (op === 'generate') {
    const by = state?.wish?.by ?? null;
    return {
      kind: 'generate', title: 'Формат новой картинки', state, by,
      chip: generateChip(state),
      footer: name => projectNoun(scope, name, by),
      hint: FORMAT_TEXT.noCaps(pv.label, m.label),
      onChange: wish => setWish(scope, mergeWish(scope, wish, state)),
    };
  }

  // Правка: edit, inpaint, outpaint
  const choice = editOf(thread.id);
  const outpaintAction = op === 'outpaint';
  const how = outpaintAction ? 'outpaint' : choice.how;
  const sz = sourceSize(scope, thread);
  const source = sz ? { width: sz.w, height: sz.h, aspect: aspectLabel(sz.w, sz.h), thumb: activeSrc(scope, thread) } : null;
  const aspect = choice.aspect;
  const out = source && aspect ? outpaintSize(source.width, source.height, aspect) : null;
  const outSize = out ? `${out.w}×${out.h}` : null;
  const cropSize = source && aspect ? cropSizeOf(scope, ctx.sessionId, thread, aspect, choice.anchor) : null;
  const outPrice = source && aspect && getFlag(FLAGS.mediaFormat) ? outpaintPrice(ctx, thread, `${pv.key}/${m.id}`) : null;

  const edit: FormatEditModel = {
    source, aspect, how, anchor: choice.anchor,
    outpaint: {
      detail: ['ИИ', m.label, outPrice, outSize].filter(Boolean).join(' · '),
      size: outSize, ready: !!aspect,
    },
    crop: { detail: ['Без ИИ', 'бесплатно', cropSize, 'новой версией'].filter(Boolean).join(' · '), size: cropSize, ready: !!aspect },
    cropping: choice.cropping,
    onSource: () => { resetEdit(thread.id); if (outpaintAction) switchAction(ctx, 'edit'); },
    onAspect: a => setEdit(thread.id, { aspect: a, how: outpaintAction ? 'outpaint' : null }),
    onHow: h => {
      if (h === 'outpaint') { setEdit(thread.id, { how: 'outpaint' }); switchAction(ctx, 'outpaint'); } else { void cropNow(ctx, scope, thread); }
    },
    onAnchor: a => setEdit(thread.id, { anchor: a }),
    onCancel: () => { resetEdit(thread.id); if (outpaintAction) switchAction(ctx, 'edit'); },
  };

  let chip: FormatModel['chip'];
  if (outpaintAction && !aspect) chip = { main: 'Выберите пропорции', tone: 'warn' };
  else if (!aspect) chip = { main: FORMAT_TEXT.asSource, ...(source ? { sub: `· ${source.aspect}`, mainBare: source.aspect, subBare: '' } : null), tone: 'normal' };
  else if (how === 'outpaint') chip = { main: aspect, sub: `· дорисовать${outSize ? ` · ${outSize}` : ''}`, subShort: '· дорисовать', tone: 'normal' };
  else chip = { main: aspect, sub: choice.cropping ? '· обрезаем…' : '· выберите способ', tone: 'normal' };

  return {
    kind: 'edit', title: `Формат · ${threadName(thread)}`, state, by: null, chip, edit,
    footer: () => 'Только для этой правки — исходник не меняется, пока не сохраните.',
    needsChoice: outpaintAction && !aspect,
    hint: outpaintAction && !aspect ? FORMAT_TEXT.outpaintNeedsFormat : undefined,
    onChange: () => {},
  };
}
