// Параметры, цена и запуск действия картинки (ADR-023 §Д2, §Д2.1): тонкий слой над существующими
// котировкой (`api.quote`) и запуском в нить (`launchThread`) — своей логики запуска здесь нет.

import { etaTicker, getChatContextState, ReportedError, showToast } from 'aihome_shell/kit';
import { imageEditorApi, type ImageEditCatalog, type ImageEditOp } from '../api';
import type { ActionQuote, ContextKindCtx, LaunchHandle, LaunchParam, LaunchRequest } from 'aihome_shell/kit';
import { isRemovalPrompt, priceSum } from '../format';
import { quickAvailability, type QuickAction } from '../editorInputs';
import { hasMaskMark } from '../marks';
import { buildQuoteBody } from '../thread/quoteBody';
import { footPrice, isOneVariant, launchMarks, modeOp, quickOf } from './ops';
import { loadCatalog } from '../thread/catalog';
import { enterScope } from '../scope';
import { threadHasImage } from '../thread/model';
import { getThreadMarks, isWholeImage, openEditor } from '../thread/threadStore';
import { launchThread, resolveModel } from '../thread/useThreadLaunch';
import { sharedQuote } from '../useQuote';
import { contextInputCounts } from './samples';
import { ASPECT_AUTO, ASPECT_OPTIONS } from './actions';
import { settingsOf } from './executors';
import { threadOfPrimary } from './state';

// Параметры панели: варианты — у всех операций с несколькими вариантами; пропорции — только у «Нарисовать»
// (у «Дорисовать» пропорции — вопрос под чипами)
export function paramsFor(catalog: ImageEditCatalog | null, settings: { provider: string | null; model: string | null; count: number }, op: ImageEditOp): LaunchParam[] {
  // Умолчание — один вариант (макет composer-actions-v1); выбранное человеком хост помнит между запусками чата
  if (isOneVariant(op)) return [];
  const { m } = resolveModel(catalog, { ...settings, matchSourceSize: true });
  const max = Math.max(1, m?.caps?.maxCount ?? catalog?.limits.maxCount ?? 4);
  const out: LaunchParam[] = [{ kind: 'variants', min: 1, max, value: 1 }];
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
    ...(own ? { references: 0, hasCharacter: false } : contextInputCounts(ctx.sessionId)),
    size, context: { sessionId: ctx.sessionId, contextRevision: req.contextRevision },
  });
  const q = await sharedQuote(imageEditorApi(), scope, body);
  const e = q.estimate;
  const free = e.unit === 'free';
  return {
    price: free ? 'бесплатно' : priceSum(e.amount, e.unit, e.approx, e),
    // Первая строка пары — та же цена, что жирным над ней; расшифровка — вторая («~40 с · очередь GPU: 0»)
    detail: footPrice(e, count)[1],
  };
}

// Запуск: ждём, пока контекст примет задачу, и отдаём дескриптор; прогресс и итог — по событиям шины
export async function launchAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<LaunchHandle> {
  const { thread, scope } = currentThread(ctx);
  const settings = settingsOf(scope, thread, req.op as ImageEditOp);
  let jobId: string | null = null;
  const watcher = jobWatcher(ctx.sessionId, thread.id, scope);
  const ok = await launchThread(scope, ctx.sessionId, thread, { kind: 'prompt', prompt: req.text }, {
    ctx: {
      op: req.op as ImageEditOp, count: countOf(req, settings.count), aspect: aspectOf(req),
      contextRevision: req.contextRevision, onJob: id => { jobId = id; watcher.attach(id); },
    },
  });
  if (!ok || !jobId) { watcher.dispose(); throw new ReportedError('Генерация не запущена'); }
  return { id: jobId, watch: watcher.watch, cancel: watcher.cancel };
}

// События задачи начинаем слушать, как только известен jobId, — до возврата дескриптора: быстрая
// задача могла завершиться раньше, чем хост подпишется
type Ev = { progress?: number; result?: { summary: string; open?: () => void }; error?: string; cancelled?: boolean };

function jobWatcher(sessionId: string, threadId: string, scope: string) {
  let off: (() => void) | null = null;
  let jobId = '';
  const ticker = etaTicker(f => emit({ progress: f }));
  let listener: ((e: Ev) => void) | null = null;
  const buffered: Ev[] = [];
  const emit = (e: Ev) => { if (listener) listener(e); else buffered.push(e); };
  return {
    attach(id: string) {
      jobId = id;
      off = imageEditorApi().subscribe(ev => {
        if (ev.jobId !== jobId) return;
        // Точного процента у поставщиков нет: полоса идёт от ожидаемой длительности прогона
        if (ev.type === 'image_edit_progress') {
          ticker.update({ run: ev.run, runs: ev.runs, etaSeconds: ev.etaSeconds, queued: ev.stage === 'queued' }, ev.stage === 'running');
        } else if (ev.type === 'image_edit_completed') {
          const n = ev.variants.length;
          ticker.stop();
          emit({ result: { summary: n > 1 ? `Готово: ${n} варианта` : 'Готово', open: () => openThread(sessionId, threadId) } });
          off?.();
        } else {
          ticker.stop();
          emit(ev.outcome === 'cancelled' ? { cancelled: true } : { error: ev.error ?? 'Картинка не нарисовалась' });
          off?.();
        }
      });
    },
    cancel() {
      return imageEditorApi().cancelJob(scope, jobId).then(() => {}, e => { showToast(`Не удалось отменить: ${(e as Error).message}`, '', 'error'); });
    },
    watch(on: (e: Ev) => void) {
      listener = on;
      buffered.splice(0).forEach(on);
      return () => { listener = null; ticker.stop(); off?.(); };
    },
    dispose() { ticker.stop(); off?.(); },
  };
}

const openThread = (sessionId: string, threadId: string) => openEditor(sessionId, threadId);
