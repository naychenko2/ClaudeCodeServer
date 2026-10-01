// Запуск генерации в нить (ADR-019 §4, прямой запуск без агента): котировка по
// настройкам нити, источник — текущий шаг или файл, пометки редактора — маской и
// снимком. Задача уходит с sessionId + threadId: сервер делает её pendingJobId нити и
// пишет в ленту тихую строку «Вы запустили: …». Цена для полосы и кнопки композера —
// отсюда же, чтобы полоса и кнопка не разошлись.

import { useCallback, useMemo } from 'react';
import { api as appApi, clearGenDraft, followChat, showToast } from 'aihome_shell/kit';
import { AUTO_MODEL, imageEditorApi, type ImageEditCatalog, type ImageEditEstimate, type ImageEditQuoteRequest } from '../api';
import {
  effectiveProvider, isRemovalPrompt, modelBlockReason, priceSum, priceText, providerTitle, variantsWord,
} from '../format';
import { currentModel } from '../ProviderModelPicker';
import { exportAnnotated, exportMask, hasAnnotationMark, hasMaskMark, marksToJson } from '../marks';
import {
  quickAvailability, quickPlan, quickUsesOwnModel, samplesToJobInput,
  type LaunchAction, type LaunchPlan, type QuickAction, type QuickRoute,
} from '../editorInputs';
import { isPersonalScope } from '../scope';
import {
  activeChoice, effectiveMode, footPrice, isOneVariant, opBlockReason, queueText, quickOf, resolveOp, runVerb,
  usePanelChoiceVersion, type PanelChoice,
} from '../panel/panelOp';
import { useQuote } from '../useQuote';
import { getCatalog, loadCatalog, useCatalog } from './catalog';
import { effectiveSettings, getPrefs, setPrefs, usePrefs } from './prefs';
import { activeStepOf } from './actions';
import { currentVersion, isLegacyThread, originFile, versionHasImage, versionStep } from './model';
import { getSamples, getThreadMarks, imageDraftKey, mutate, setThreadMarks, useThreadStoreVersion } from './threadStore';
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

export function threadHasImage(t: ImageThread | null): boolean {
  if (!t) return false;
  const v = isLegacyThread(t) ? null : currentVersion(t);
  return v ? versionHasImage(t, v) : !!t.currentStepId || !!t.file;
}

// Поставщик и модель по настройкам: модель не выбрана — умолчание админа у его поставщика
function resolveModel(catalog: ImageEditCatalog | null, settings: ImageThreadSettings) {
  const pv = catalog ? effectiveProvider(catalog, settings.provider ?? 'settings') : null;
  const fallback = pv && pv.key === catalog?.default.provider ? catalog.default.model : AUTO_MODEL;
  return { pv, m: currentModel(pv, settings.model ?? fallback) };
}

// Поставщик, модель и ориентир цены без подписки — для строк вне рендера полосы
// (меню переключателя полос): каталог берётся из кэша, котировки нет
export function launchSummaryParts(projectId: string, thread: ImageThread | null) {
  const settings = effectiveSettings(getPrefs(projectId), thread?.settings);
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
export function panelRoute(choice: PanelChoice, hasImage: boolean, hasMask: boolean) {
  const op = resolveOp(choice.op, hasImage, hasMask);
  return { op, quick: quickOf(op), one: isOneVariant(op), reason: opBlockReason(choice.op, hasImage, hasMask) };
}

// Запуск в нить; true — задача запущена, причина отказа уже показана тостом.
// provider — поставщик только на этот запуск («Взять fal»), выбор в полосе не меняется
export async function launchThread(
  projectId: string, sessionId: string, thread: ImageThread, requested: LaunchAction, opts?: { provider?: string },
): Promise<boolean> {
  let action = requested;
  const api = imageEditorApi();
  const prefs = getPrefs(projectId);
  const settings = effectiveSettings(prefs, thread.settings);
  const catalog = await loadCatalog(projectId);
  const { pv, m } = resolveModel(catalog, settings);
  if (!pv || !m) {
    showToast('Рисовать нечем: администратор не настроил поставщиков картинок', '', 'error');
    return false;
  }
  const { marks, size } = getThreadMarks(thread.id);
  const hasImage = threadHasImage(thread);
  const hasMask = hasImage && hasMaskMark(marks);
  // Операция панели «Картинки» (без флага — всегда «Авто»): у промпта она решает, что делать
  const choice = activeChoice(projectId);
  const pr = panelRoute(choice, hasImage, hasMask);
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
  const withMarks = !fromScratch && (action.kind === 'prompt' || action.kind === 'removeMarked') && hasImage && marks.length > 0;
  try {
    const q = await api.quote(projectId, {
      provider: route?.provider ?? pv.key, model: route?.model ?? m.id,
      mode: route ? 'auto' : effectiveMode(m, choice.mode), op: plan.op,
      count: one ? 1 : route?.count ?? settings.count,
      hasMask: withMask, hasAnnotations: withMarks && hasAnnotationMark(marks), removal: plan.removal,
      references: noSamples ? 0 : getSamples(projectId).length, hasCharacter: !noSamples && !!prefs.characterSlug,
      width: size?.w ?? null, height: size?.h ?? null,
    });
    const legacy = isLegacyThread(thread);
    const version = legacy ? null : currentVersion(thread);
    const stepId = activeStepOf(thread);
    const src = fromScratch ? null : activeSrc(projectId, thread);
    // Байты картинки — всегда с фронта: сам сервер подставляет только шаг версии, а исходник
    // без правок (файл проекта) по sourcePath не читает — тот лишь сторож пути и родословная
    const source = src ? await fetch(src).then(r => r.blob()) : undefined;
    const file = version ? (version.id === 'origin' ? originFile(thread) : null) : thread.file;
    const samples = noSamples ? { references: [], referencePaths: [] } : samplesToJobInput(getSamples(projectId));
    let mask: Blob | undefined;
    let annotated: Blob | undefined;
    if (src && size && withMask) mask = (await exportMask(marks, size.w, size.h)) ?? undefined;
    if (src && size && withMarks) {
      const img = await loadImage(src).catch(() => null);
      if (img) annotated = (await exportAnnotated(img, marks, size.w, size.h).catch(() => null)) ?? undefined;
    }
    await api.startJob(projectId, {
      quoteId: q.quoteId, prompt: plan.prompt,
      marks: withMarks && size ? marksToJson(marks, size.w, size.h) : undefined,
      sourcePath: !fromScratch && !stepId && file ? file : undefined,
      source, mask, annotated, ...samples,
      characterSlug: noSamples ? undefined : prefs.characterSlug ?? undefined,
      matchSourceSize: settings.matchSourceSize,
      ...(plan.aspectRatio ? { aspectRatio: plan.aspectRatio } : null),
      sessionId, threadId: thread.id, baseStepId: stepId ?? undefined, versionId: version?.id,
    });
    // Пометки ушли с запуском
    if (withMarks || withMask) setThreadMarks(thread.id, [], null);
    // Запуск забрал черновик элемента — пометка «черновик» уходит
    clearGenDraft(imageDraftKey(thread.id));
    // Запуск здесь всегда от человека (агент запускает через MCP мимо фронта): строка
    // запуска и новые версии ложатся внизу — лента едет к ним и держит низ
    followChat(sessionId);
    return true;
  } catch (e) {
    showToast(`Генерация не запущена: ${(e as Error).message}`, '', 'error');
    return false;
  }
}

export function useThreadLaunch(projectId: string, sessionId: string | null, thread: ImageThread | null) {
  const api = useMemo(() => imageEditorApi(), []);
  const catalog = useCatalog(projectId);
  const prefs = usePrefs(projectId);
  useThreadStoreVersion();
  const settings = effectiveSettings(prefs, thread?.settings);
  const { pv, m } = resolveModel(catalog, settings);
  const { marks, size } = getThreadMarks(thread?.id ?? null);
  const hasImage = threadHasImage(thread);
  const hasMask = hasImage && hasMaskMark(marks);
  const hasAnnotations = hasImage && hasAnnotationMark(marks);
  // Операция и режим панели «Картинки»
  usePanelChoiceVersion();
  const choice = activeChoice(projectId);
  const pr = panelRoute(choice, hasImage, hasMask);
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

  const quoteReq: ImageEditQuoteRequest | null = pr.reason ? null
    : route ? {
      provider: route.provider, model: route.model, mode, op: pr.op, count,
      hasMask: false, hasAnnotations: false, references, hasCharacter: !own && !!prefs.characterSlug,
      width: size?.w ?? null, height: size?.h ?? null,
    }
    : pv && m && !blocked && !pr.quick ? {
      provider: pv.key, model: m.id, mode, op: pr.op, count,
      hasMask: withMask, hasAnnotations: !fromScratch && hasAnnotations, references, hasCharacter: !!prefs.characterSlug,
      width: size?.w ?? null, height: size?.h ?? null,
    } : null;
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
    setPrefs(projectId, patch);
    if (thread && sessionId) {
      void mutate(projectId, sessionId, rev => threadsApi.settings(projectId, sessionId, thread.id, next, rev));
    }
  }, [projectId, sessionId, thread, settings]);

  const launch = useCallback((action: LaunchAction) =>
    (thread && sessionId ? launchThread(projectId, sessionId, thread, action) : Promise.resolve(false)),
  [projectId, sessionId, thread]);

  return {
    catalog, settings, prefs, provider: pv, model: m, blocked, quote, quoteLoading: loading,
    priceLabel, price, marks, hasImage, hasMask, setSettings, launch,
    op: pr.op, quickAction: pr.quick, choice, count, maxCount, reason, priceLines, queue, runLabel: runVerb(pr.op),
  };
}
