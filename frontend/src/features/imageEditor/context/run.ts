// Параметры, цена и запуск действия картинки (ADR-023 §Д2, §Д2.1): тонкий слой над существующими
// котировкой (`api.quote`) и запуском в нить (`launchThread`) — своей логики запуска здесь нет.

import { getChatContextState, ReportedError } from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditCatalog, type ImageEditOp } from '../api';
import type { ActionQuote, ContextKindCtx, LaunchHandle, LaunchParam, LaunchRequest } from 'aihome_shell/kit';
import { isRemovalPrompt, priceSum } from '../format';
import { quickAvailability, type QuickAction } from '../editorInputs';
import { hasMaskMark } from '../marks';
import { buildQuoteBody } from '../thread/quoteBody';
import { footPrice, isOneVariant, launchMarks, modeOp, quickOf } from '../panel/panelOp';
import { loadCatalog } from '../thread/catalog';
import { getPrefs } from '../thread/prefs';
import { enterScope } from '../scope';
import { threadHasImage } from '../thread/model';
import { getSamples, getThreadMarks, isWholeImage, openEditor } from '../thread/threadStore';
import { launchThread, resolveModel } from '../thread/useThreadLaunch';
import { sharedQuote } from '../useQuote';
import { ASPECT_AUTO, ASPECT_OPTIONS } from './actions';
import { settingsOf } from './executors';
import { threadOfPrimary } from './state';

// Параметры панели: варианты — у всех операций с несколькими вариантами; пропорции — только у «Нарисовать»
// (у «Дорисовать» пропорции — вопрос под чипами)
export function paramsFor(catalog: ImageEditCatalog | null, settings: { provider: string | null; model: string | null; count: number }, op: ImageEditOp): LaunchParam[] {
  if (isOneVariant(op)) return [];
  const { m } = resolveModel(catalog, { ...settings, matchSourceSize: true });
  const max = Math.max(1, m?.caps?.maxCount ?? catalog?.limits.maxCount ?? 4);
  const out: LaunchParam[] = [{ kind: 'variants', min: 1, max, value: Math.min(max, Math.max(1, settings.count)) }];
  if (op === 'generate') out.push({ kind: 'aspect', options: [ASPECT_AUTO, ...ASPECT_OPTIONS], value: ASPECT_AUTO });
  return out;
}

const countOf = (req: LaunchRequest, fallback: number) =>
  typeof req.params.variants === 'number' ? req.params.variants : fallback;

// Пропорции запуска: у новой картинки «авто» — как решит модель
export function aspectOf(req: LaunchRequest): string | null {
  const v = req.params.aspect;
  return typeof v === 'string' && v !== ASPECT_AUTO ? v : null;
}

// Нить основного объекта чата и область картинок; нет её (контекст сменился) — запуск и цена отказывают
function currentThread(ctx: ContextKindCtx) {
  const thread = threadOfPrimary(ctx.sessionId, getChatContextState(ctx.sessionId).primary);
  if (!thread) throw new Error('Картинка недоступна');
  return { thread, scope: enterScope(ctx.projectId, ctx.sessionId) };
}

// Цена по op действия: тот же запрос, что у запуска, но без запуска. Отказ — исключение (цены просто нет)
export async function quoteAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<ActionQuote> {
  const { thread, scope } = currentThread(ctx);
  const catalog = await loadCatalog(scope);
  if (!catalog) throw new Error('Каталог поставщиков недоступен');
  const op = req.op as ImageEditOp;
  const prefs = getPrefs(scope);
  const settings = settingsOf(scope, thread, op);
  const { pv, m } = resolveModel(catalog, settings);
  if (!pv || !m) throw new Error('Рисовать нечем');
  const hasImage = threadHasImage(thread);
  const { marks: drawn, size } = getThreadMarks(thread.id);
  const marks = launchMarks(drawn, isWholeImage(thread.id));
  const hasMask = hasImage && hasMaskMark(marks);
  // «Изменить» с маской — инпейнт, как у запуска
  const eff = op === 'generate' ? op : modeOp(op, hasImage, hasMask).op;
  const quick = quickOf(eff) as QuickAction | null;
  const route = quick ? quickAvailability(quick, catalog, pv.key, m.id, settings.count).route : null;
  if (quick && !route) throw new Error('Нет поставщика для этой операции');
  const one = isOneVariant(eff);
  const count = one ? 1 : route ? Math.min(route.count, countOf(req, settings.count)) : countOf(req, settings.count);
  const own = eff === 'enhanceFaces' || route?.maxReferences === 0;
  const body = buildQuoteBody({
    provider: route?.provider ?? pv.key, model: route?.model ?? m.id, mode: 'auto', op: eff, count,
    hasImage, marks, withMask: eff === 'inpaint' && hasMask, removal: eff === 'inpaint' && isRemovalPrompt(req.text),
    references: own ? 0 : getSamples(scope).length, hasCharacter: !own && !!prefs.characterSlug,
    size, context: { sessionId: ctx.sessionId, contextRevision: req.contextRevision },
  });
  const q = await sharedQuote(imageEditorApi(), scope, body);
  const e = q.estimate;
  const free = e.unit === 'free';
  return {
    price: free ? 'бесплатно' : priceSum(e.amount, e.unit, e.approx, e),
    detail: footPrice(e, count).join(' · '),
  };
}

// Запуск: ждём, пока контекст примет задачу, и отдаём дескриптор; прогресс и итог — по событиям шины
export async function launchAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<LaunchHandle> {
  const { thread, scope } = currentThread(ctx);
  const settings = settingsOf(scope, thread, req.op as ImageEditOp);
  let jobId: string | null = null;
  const watcher = jobWatcher(ctx.sessionId, thread.id);
  const ok = await launchThread(scope, ctx.sessionId, thread, { kind: 'prompt', prompt: req.text }, {
    ctx: {
      op: req.op as ImageEditOp, count: countOf(req, settings.count), aspect: aspectOf(req),
      contextRevision: req.contextRevision, onJob: id => { jobId = id; watcher.attach(id); },
    },
  });
  if (!ok || !jobId) { watcher.dispose(); throw new ReportedError('Генерация не запущена'); }
  return { id: jobId, watch: watcher.watch };
}

// События задачи начинаем слушать, как только известен jobId, — до возврата дескриптора: быстрая
// задача могла завершиться раньше, чем хост подпишется
type Ev = { progress?: number; result?: { summary: string; open?: () => void }; error?: string };

function jobWatcher(sessionId: string, threadId: string) {
  let off: (() => void) | null = null;
  let listener: ((e: Ev) => void) | null = null;
  const buffered: Ev[] = [];
  const emit = (e: Ev) => { if (listener) listener(e); else buffered.push(e); };
  return {
    attach(jobId: string) {
      off = imageEditorApi().subscribe(ev => {
        if (ev.jobId !== jobId) return;
        // Стадии грубые: точного процента у поставщиков нет
        if (ev.type === 'image_edit_progress') emit({ progress: ev.stage === 'queued' ? 0.05 : ev.stage === 'running' ? 0.5 : 0.9 });
        else if (ev.type === 'image_edit_completed') {
          const n = ev.variants.length;
          emit({ result: { summary: n > 1 ? `Готово: ${n} варианта` : 'Готово', open: () => openThread(sessionId, threadId) } });
          off?.();
        } else {
          emit({ error: ev.error ?? 'Картинка не нарисовалась' });
          off?.();
        }
      });
    },
    watch(on: (e: Ev) => void) {
      listener = on;
      buffered.splice(0).forEach(on);
      return () => { listener = null; off?.(); };
    },
    dispose() { off?.(); },
  };
}

const openThread = (sessionId: string, threadId: string) => openEditor(sessionId, threadId);
