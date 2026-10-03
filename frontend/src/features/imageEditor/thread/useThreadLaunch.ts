// Запуск генерации в нить (ADR-019 §4, прямой запуск без агента): котировка по
// настройкам нити, источник — текущий шаг или файл, пометки редактора — маской и
// снимком. Задача уходит с sessionId + threadId: сервер делает её pendingJobId нити и
// пишет в ленту тихую строку «Вы запустили: …». Цена для полосы и кнопки композера —
// отсюда же, чтобы полоса и кнопка не разошлись.

import { useCallback, useMemo } from 'react';
import { api as appApi, clearGenDraft, FLAGS, followChat, showToast, useFeature } from 'aihome_shell/kit';
import {
  AUTO_MODEL, imageEditorApi, type ImageEditCatalog, type ImageEditEstimate, type ImageEditOp, type ImageEditQuoteRequest,
} from '../api';
import {
  effectiveProvider, isRemovalPrompt, modelBlockReason, priceSum, priceText, providerTitle, variantsWord,
} from '../format';
import { currentModel } from '../ProviderModelPicker';
import { contextInputCounts } from '../context/samples';
import { exportAnnotated, exportMask, hasMaskMark, marksToJson } from '../marks';
import {
  quickAvailability, quickPlan, quickUsesOwnModel, samplesToJobInput,
  type LaunchAction, type LaunchPlan, type OutpaintRatio, type QuickAction, type QuickRoute,
} from '../editorInputs';
import { isPersonalScope } from '../scope';
import {
  activeChoice, DEFAULT_CHOICE, effectiveMode, footPrice, getCreateRatio, isOneVariant, launchMarks, modeOp, opBlockReason, queueText, quickOf,
  resolveOp, runVerb, usePanelChoiceVersion, type PanelChoice,
} from '../panel/panelOp';
import { useQuote } from '../useQuote';
import { buildQuoteBody, marksSent } from './quoteBody';
import { getCatalog, loadCatalog, useCatalog } from './catalog';
import { effectiveSettings, getPrefs, modeSettings, setModeSettings, setPrefs, usePrefs, type ProjectPrefs } from './prefs';
import { effectiveImageMode, getStoredImageMode, modeAware, noteLastEdited, useImageModeVersion, type ImageMode } from './modeState';
import { activeStepOf } from './actions';
import { currentVersion, isLegacyThread, originFile, threadHasImage, versionStep } from './model';
import { getSamples, getThreadMarks, imageDraftKey, isWholeImage, mutate, setThreadMarks, useThreadStoreVersion } from './threadStore';
import { threadsApi, type ImageThread, type ImageThreadSettings, type ImageThreadVersion } from './threadsApi';

// Картинка позиции нити: шаг — из рабочей папки редактора, исходник — файл проекта
// (у личной области файлов нет: сервер не заводит ей нить по файлу)
export function imageSrc(projectId: string, t: ImageThread, stepId: string | null): string | null {
  if (stepId) return imageEditorApi().stepUrl(projectId, stepId);
  return t.file && !isPersonalScope(projectId) ? appApi.files.fileUrl(projectId, t.file) : null;
}

// Картинка версии: её шаг или файл-исходник
export function versionSrc(projectId: string, t: ImageThread, v: ImageThreadVersion): string | null {
  const stepId = versionStep(t, v);
  if (stepId) return imageEditorApi().stepUrl(projectId, stepId);
  const file = v.id === 'origin' ? originFile(t) : null;
  return file && !isPersonalScope(projectId) ? appApi.files.fileUrl(projectId, file) : null;
}

// Картинка, от которой пойдёт следующая правка
export function activeSrc(projectId: string, t: ImageThread): string | null {
  const v = isLegacyThread(t) ? null : currentVersion(t);
  return v ? versionSrc(projectId, t, v) : imageSrc(projectId, t, t.currentStepId);
}

export { threadHasImage } from './model';

// Поставщик и модель по настройкам: модель не выбрана — умолчание админа у его поставщика
export function resolveModel(catalog: ImageEditCatalog | null, settings: ImageThreadSettings) {
  const pv = catalog ? effectiveProvider(catalog, settings.provider ?? 'settings') : null;
  const fallback = pv && pv.key === catalog?.default.provider ? catalog.default.model : AUTO_MODEL;
  return { pv, m: currentModel(pv, settings.model ?? fallback) };
}

// Режим запуска чата (флаг image-panel-v5); null — флаг выключен, режимов нет
export function launchMode(sessionId: string | null, thread: ImageThread | null): ImageMode | null {
  return modeAware() ? effectiveImageMode(getStoredImageMode(sessionId), threadHasImage(thread)) : null;
}

// Настройки запуска: с режимом — выбор режима, без него — как раньше
export const launchSettings = (mode: ImageMode | null, prefs: ProjectPrefs, own: ImageThreadSettings | null | undefined) =>
  mode ? modeSettings(mode, prefs, own) : effectiveSettings(prefs, own);

// Поставщик, модель и ориентир цены без подписки — для строк вне рендера полосы
// (меню переключателя полос): каталог берётся из кэша, котировки нет
export function launchSummaryParts(projectId: string, thread: ImageThread | null, sessionId: string | null = null) {
  const settings = launchSettings(launchMode(sessionId, thread), getPrefs(projectId), thread?.settings);
  const { pv, m } = resolveModel(getCatalog(projectId), settings);
  const price = m?.priceHint ? priceSum(m.priceHint.amount * settings.count, m.priceHint.unit, true) : null;
  return { provider: pv ? providerTitle(pv) : null, model: m?.label ?? null, count: settings.count, price };
}

export function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('Картинка не загрузилась'));
    img.src = src;
  });
}

// Быстрое действие в полосе нити: поставщик и модель, которыми оно пойдёт, — до запуска
export function quickAvailabilityFor(catalog: ImageEditCatalog | null, settings: ImageThreadSettings, action: QuickAction) {
  const { pv, m } = resolveModel(catalog, settings);
  return quickAvailability(action, catalog, pv?.key ?? null, m?.id ?? AUTO_MODEL, settings.count);
}

// Что запустит промпт или кнопка низа при операции панели «Картинки»: «Авто» — pickOp,
// операция без промпта идёт путём быстрого действия редактора
// byMode — выбор по режиму (флаг image-panel-v5): «Авто» нет, «Изменить» сам решает по отметкам
export function panelRoute(choice: PanelChoice, hasImage: boolean, hasMask: boolean, byMode = false) {
  const { op, reason } = byMode
    ? modeOp(choice.op, hasImage, hasMask)
    : { op: resolveOp(choice.op, hasImage, hasMask), reason: opBlockReason(choice.op, hasImage, hasMask) };
  return { op, quick: quickOf(op), one: isOneVariant(op), reason };
}

// Запуск действия чипа (ADR-023 §Д2): операция, число вариантов и пропорции решены действием,
// а не выбором панели; contextRevision уходит в котировку и запуск. Отказ ревизии (409
// context_changed) пробрасывается вызывающему: контекст перечитывает хост, запуск не повторяется
export interface ContextLaunch {
  op: ImageEditOp;
  count: number;
  // Пропорции дорисовки или новой картинки; null — как решит модель
  aspect: string | null;
  contextRevision: number;
  onJob?: (jobId: string) => void;
}

// Запуск в нить; true — задача запущена, причина отказа уже показана тостом.
// provider — поставщик только на этот запуск («Взять fal»), выбор в полосе не меняется
export async function launchThread(
  projectId: string, sessionId: string, thread: ImageThread, requested: LaunchAction,
  opts?: { provider?: string; ctx?: ContextLaunch },
): Promise<boolean> {
  let action = requested;
  const api = imageEditorApi();
  const prefs = getPrefs(projectId);
  // Быстрое действие — правка выбранной картинки: в любом режиме идёт выбором «Править»
  const cx = opts?.ctx;
  // Действие чипа: «Нарисовать» — режим «Создать», остальное — «Править»
  const chatMode = cx ? (cx.op === 'generate' ? 'create' : 'edit') as ImageMode : launchMode(sessionId, thread);
  const mode = chatMode && requested.kind !== 'prompt' && threadHasImage(thread) ? 'edit' : chatMode;
  const baseSettings = launchSettings(mode, prefs, thread.settings);
  const settings = cx ? { ...baseSettings, count: cx.count } : baseSettings;
  const catalog = await loadCatalog(projectId);
  const { pv, m } = resolveModel(catalog, settings);
  if (!pv || !m) {
    showToast('Рисовать нечем: администратор не настроил поставщиков картинок', '', 'error');
    return false;
  }
  const { marks: drawn, size } = getThreadMarks(thread.id);
  // С режимом «Где менять: Вся картинка» маску не шлём, даже если она закрашена
  const marks = mode ? launchMarks(drawn, isWholeImage(thread.id)) : drawn;
  const hasImage = threadHasImage(thread);
  const hasMask = hasImage && hasMaskMark(marks);
  // Операция панели «Картинки» (без флага — всегда «Авто»): у промпта она решает, что делать
  const choice: PanelChoice = cx
    ? { op: cx.op, mode: 'auto', ratio: (cx.aspect ?? DEFAULT_CHOICE.ratio) as OutpaintRatio }
    : activeChoice(projectId, mode);
  const pr = panelRoute(choice, hasImage, hasMask, !!mode);
  let one = false;
  if (action.kind === 'prompt') {
    if (pr.reason) {
      showToast(pr.reason, '', 'error');
      return false;
    }
    if (pr.quick) {
      action = pr.quick === 'outpaint' ? { kind: pr.quick, ratio: choice.ratio } : { kind: pr.quick };
      one = pr.one;
    }
  }
  // Быстрое действие идёт моделью, которая его умеет: выбранная в полосе может не уметь
  let route: QuickRoute | null = null;
  if (action.kind !== 'prompt') {
    const r = opts?.provider
      ? quickAvailability(action.kind, catalog, opts.provider, AUTO_MODEL, settings.count)
      : quickAvailabilityFor(catalog, settings, action.kind);
    if (!r.route) {
      showToast(r.reason, '', 'error');
      return false;
    }
    route = r.route;
  }
  const prompt = action.kind === 'prompt' ? action.prompt.trim() : '';
  // Маска уходит только инпейнту: у «Авто» это ровно «кисть есть», как раньше
  const plan: LaunchPlan = action.kind === 'prompt'
    ? { op: pr.op, prompt, useMask: pr.op === 'inpaint', removal: pr.op === 'inpaint' && isRemovalPrompt(prompt) }
    : quickPlan(action.kind, action.ratio ?? '16:9');
  // «По тексту» при выбранной картинке рисует с нуля: исходник и пометки не шлём
  const fromScratch = plan.op === 'generate';
  const own = action.kind !== 'prompt' && quickUsesOwnModel(action.kind);
  const blocked = route ? '' : modelBlockReason(m, !fromScratch && hasImage, plan.useMask && hasMask);
  if (blocked) {
    showToast(`${m.label}: ${blocked}`, '', 'error');
    return false;
  }
  // Модели без канала образцов (Bria Expand, убрать фон) образцы и персонажа не шлём
  const noSamples = own || route?.maxReferences === 0;
  const withMask = plan.useMask && hasMask;
  const withMarks = marksSent(plan.op, hasImage, marks);
  // Пропорции новой картинки из «Ещё настроек» «Создать»; у дорисовки — свои
  const aspectRatio = plan.aspectRatio
    ?? (cx ? (fromScratch ? cx.aspect : null) : mode === 'create' && fromScratch ? getCreateRatio(projectId) : null);
  try {
    // Запуск по ревизии берёт образцы и персонажа из контекста чата; без неё — из памяти вкладки и prefs
    const inputCounts = () => noSamples ? { references: 0, hasCharacter: false }
      : cx ? contextInputCounts(sessionId) : { references: getSamples(projectId).length, hasCharacter: !!prefs.characterSlug };
    const q = await api.quote(projectId, buildQuoteBody({
      provider: route?.provider ?? pv.key, model: route?.model ?? m.id,
      mode: route ? 'auto' : effectiveMode(m, choice.mode), op: plan.op,
      count: one ? 1 : route?.count ?? settings.count,
      hasImage, marks, withMask, removal: plan.removal,
      ...inputCounts(),
      size, context: cx ? { sessionId, contextRevision: cx.contextRevision } : null,
    }));
    const legacy = isLegacyThread(thread);
    const version = legacy ? null : currentVersion(thread);
    const stepId = activeStepOf(thread);
    const src = fromScratch ? null : activeSrc(projectId, thread);
    // Байты картинки — всегда с фронта: сам сервер подставляет только шаг версии, а исходник
    // без правок (файл проекта) по sourcePath не читает — тот лишь сторож пути и родословная
    const source = src ? await fetch(src).then(r => r.blob()) : undefined;
    const file = version ? (version.id === 'origin' ? originFile(thread) : null) : thread.file;
    const samples = noSamples || cx ? { references: [], referencePaths: [] } : samplesToJobInput(getSamples(projectId));
    let mask: Blob | undefined;
    let annotated: Blob | undefined;
    if (src && size && withMask) mask = (await exportMask(marks, size.w, size.h)) ?? undefined;
    if (src && size && withMarks) {
      const img = await loadImage(src).catch(() => null);
      if (img) annotated = (await exportAnnotated(img, marks, size.w, size.h).catch(() => null)) ?? undefined;
    }
    const started = await api.startJob(projectId, {
      quoteId: q.quoteId, prompt: plan.prompt,
      marks: withMarks && size ? marksToJson(marks, size.w, size.h) : undefined,
      sourcePath: !fromScratch && !stepId && file ? file : undefined,
      source, mask, annotated, ...samples,
      characterSlug: noSamples || cx ? undefined : prefs.characterSlug ?? undefined,
      matchSourceSize: settings.matchSourceSize,
      ...(aspectRatio ? { aspectRatio } : null),
      sessionId, threadId: thread.id, baseStepId: stepId ?? undefined, versionId: version?.id,
      ...(cx ? { contextRevision: cx.contextRevision } : null),
    });
    cx?.onJob?.(started.jobId);
    // Пометки ушли с запуском
    if (withMarks || withMask) setThreadMarks(thread.id, [], null);
    // Правили эту картинку — отметка «правили последней» в меню «Что править?»
    if (!fromScratch) noteLastEdited(sessionId, thread.id);
    // Запуск забрал черновик элемента — пометка «черновик» уходит
    clearGenDraft(imageDraftKey(thread.id));
    // Запуск здесь всегда от человека (агент запускает через MCP мимо фронта): строка
    // запуска и новые версии ложатся внизу — лента едет к ним и держит низ
    followChat(sessionId);
    return true;
  } catch (e) {
    // Контекст сменился: причину показывает хост (свежий DTO уже в сторе), запуск не повторяется
    if (cx) throw e;
    showToast(`Генерация не запущена: ${(e as Error).message}`, '', 'error');
    return false;
  }
}

export function useThreadLaunch(projectId: string, sessionId: string | null, thread: ImageThread | null) {
  const api = useMemo(() => imageEditorApi(), []);
  const catalog = useCatalog(projectId);
  const prefs = usePrefs(projectId);
  useThreadStoreVersion();
  useFeature(FLAGS.imagePanelV5);
  useImageModeVersion();
  const imgMode = launchMode(sessionId, thread);
  const settings = launchSettings(imgMode, prefs, thread?.settings);
  const { pv, m } = resolveModel(catalog, settings);
  const { marks: drawn, size } = getThreadMarks(thread?.id ?? null);
  const whole = isWholeImage(thread?.id ?? null);
  const marks = imgMode ? launchMarks(drawn, whole) : drawn;
  const hasImage = threadHasImage(thread);
  const hasMask = hasImage && hasMaskMark(marks);
  // Операция и режим панели «Картинки»
  usePanelChoiceVersion();
  const choice = activeChoice(projectId, imgMode);
  const pr = panelRoute(choice, hasImage, hasMask, !!imgMode);
  const quick = pr.quick ? quickAvailabilityFor(catalog, settings, pr.quick) : null;
  const route = quick?.route ?? null;
  const fromScratch = pr.op === 'generate';
  const withMask = pr.op === 'inpaint' && hasMask;
  const blocked = m && !pr.quick ? modelBlockReason(m, !fromScratch && hasImage, withMask) : '';
  // Потолок вариантов — как у секции «Варианты и цена»; одновариантная операция — ровно 1
  const maxCount = pr.one ? 1 : m?.caps?.maxCount ?? catalog?.limits.maxCount ?? 4;
  const count = pr.one ? 1 : route ? route.count : settings.count;
  const own = pr.quick === 'enhanceFaces' || route?.maxReferences === 0;
  const references = own ? 0 : getSamples(projectId).length;
  const mode = route ? 'auto' : effectiveMode(m, choice.mode);
  // Почему запуск сейчас невозможен — для закреплённого низа панели
  const reason = pr.reason
    || (pr.quick ? quick?.reason ?? '' : '')
    || (!pv || !m ? 'Рисовать нечем: администратор не настроил поставщиков картинок' : '')
    || (blocked ? `${m!.label}: ${blocked.charAt(0).toLowerCase()}${blocked.slice(1)}` : '');

  const body = (provider: string, model: string, withMaskNow: boolean, refs: number, character: boolean) => buildQuoteBody({
    provider, model, mode, op: pr.op, count, hasImage, marks, withMask: withMaskNow, removal: false,
    references: refs, hasCharacter: character, size, context: null,
  });
  const quoteReq: ImageEditQuoteRequest | null = pr.reason ? null
    : route ? body(route.provider, route.model, false, references, !own && !!prefs.characterSlug)
    : pv && m && !blocked && !pr.quick ? body(pv.key, m.id, withMask, references, !!prefs.characterSlug)
    : null;
  const { quote, loading, stale } = useQuote(api, projectId, quoteReq);
  const hint = route ? route.priceHint : m?.priceHint ?? null;
  // Пока котировка едет — прошлая цена той же модели, затем ориентир из каталога, чтобы цена
  // не мигала «уточняется»
  const estimate: Pick<ImageEditEstimate, 'amount' | 'unit' | 'approx' | 'etaSeconds' | 'queueLength'> | null =
    quote?.estimate ?? stale ?? (hint ? { amount: hint.amount * count, unit: hint.unit, approx: true } : null);
  const priceLabel = estimate
    ? priceText(estimate.amount, estimate.unit, estimate.approx, count, estimate)
    : variantsWord(count);
  const price = estimate ? priceSum(estimate.amount, estimate.unit, estimate.approx, estimate) : null;
  const priceLines = footPrice(estimate, count);
  const queue = queueText(quote?.estimate ?? null);

  const setSettings = useCallback((patch: Partial<ImageThreadSettings>) => {
    const next = { ...settings, ...patch };
    // С режимом: «Создать» пишет только свой выбор проекта, нить не трогает
    if (imgMode) setModeSettings(projectId, imgMode, patch);
    else setPrefs(projectId, patch);
    if (thread && sessionId && imgMode !== 'create') {
      void mutate(projectId, sessionId, rev => threadsApi.settings(projectId, sessionId, thread.id, next, rev));
    }
  }, [projectId, sessionId, thread, settings, imgMode]);

  const launch = useCallback((action: LaunchAction) =>
    (thread && sessionId ? launchThread(projectId, sessionId, thread, action) : Promise.resolve(false)),
  [projectId, sessionId, thread]);

  return {
    catalog, settings, prefs, imageMode: imgMode, provider: pv, model: m, blocked, quote, quoteLoading: loading,
    priceLabel, price, marks, drawn, whole, hasImage, hasMask, setSettings, launch, route,
    op: pr.op, quickAction: pr.quick, choice, count, maxCount, reason, priceLines, queue, runLabel: runVerb(pr.op),
  };
}
