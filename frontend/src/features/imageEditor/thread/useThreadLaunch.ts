// Запуск генерации в нить (ADR-019 §4, прямой запуск без агента): котировка по
// настройкам нити, источник — текущий шаг или файл, пометки редактора — маской и
// снимком. Задача уходит с sessionId + threadId: сервер делает её pendingJobId нити и
// пишет в ленту тихую строку «Вы запустили: …». Цена для полосы и кнопки композера —
// отсюда же, чтобы полоса и кнопка не разошлись.

import { useCallback, useMemo } from 'react';
import { api as appApi, followChat, showToast } from 'aihome_shell/kit';
import { AUTO_MODEL, imageEditorApi, type ImageEditCatalog, type ImageEditQuoteRequest } from '../api';
import {
  effectiveProvider, isRemovalPrompt, modelBlockReason, pickOp, priceSum, priceText, variantsWord,
} from '../format';
import { currentModel } from '../ProviderModelPicker';
import { exportAnnotated, exportMask, hasAnnotationMark, hasMaskMark, marksToJson } from '../marks';
import {
  quickAvailability, quickPlan, quickUsesOwnModel, samplesToJobInput,
  type LaunchAction, type LaunchPlan, type QuickAction, type QuickRoute,
} from '../editorInputs';
import { useQuote } from '../useQuote';
import { getCatalog, loadCatalog, useCatalog } from './catalog';
import { effectiveSettings, getPrefs, setPrefs, usePrefs } from './prefs';
import { activeStepOf } from './actions';
import { currentVersion, isLegacyThread, originFile, versionHasImage, versionStep } from './model';
import { getSamples, getThreadMarks, mutate, setThreadMarks, useThreadStoreVersion } from './threadStore';
import { threadsApi, type ImageThread, type ImageThreadSettings, type ImageThreadVersion } from './threadsApi';

// Картинка позиции нити: шаг — из рабочей папки редактора, исходник — файл проекта
export function imageSrc(projectId: string, t: ImageThread, stepId: string | null): string | null {
  if (stepId) return imageEditorApi().stepUrl(projectId, stepId);
  return t.file ? appApi.files.fileUrl(projectId, t.file) : null;
}

// Картинка версии: её шаг или файл-исходник
export function versionSrc(projectId: string, t: ImageThread, v: ImageThreadVersion): string | null {
  const stepId = versionStep(t, v);
  if (stepId) return imageEditorApi().stepUrl(projectId, stepId);
  const file = v.id === 'origin' ? originFile(t) : null;
  return file ? appApi.files.fileUrl(projectId, file) : null;
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
  return { provider: pv?.label ?? null, model: m?.label ?? null, count: settings.count, price };
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

// Запуск в нить; true — задача запущена, причина отказа уже показана тостом.
// provider — поставщик только на этот запуск («Взять fal»), выбор в полосе не меняется
export async function launchThread(
  projectId: string, sessionId: string, thread: ImageThread, action: LaunchAction, opts?: { provider?: string },
): Promise<boolean> {
  const api = imageEditorApi();
  const prefs = getPrefs(projectId);
  const settings = effectiveSettings(prefs, thread.settings);
  const catalog = await loadCatalog(projectId);
  const { pv, m } = resolveModel(catalog, settings);
  if (!pv || !m) {
    showToast('Рисовать нечем: администратор не настроил поставщиков картинок', '', 'error');
    return false;
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
  const { marks, size } = getThreadMarks(thread.id);
  const hasImage = threadHasImage(thread);
  const hasMask = hasImage && hasMaskMark(marks);
  const prompt = action.kind === 'prompt' ? action.prompt.trim() : '';
  const plan: LaunchPlan = action.kind === 'prompt'
    ? { op: pickOp(hasImage, hasMask), prompt, useMask: true, removal: hasMask && isRemovalPrompt(prompt) }
    : quickPlan(action.kind, action.ratio ?? '16:9');
  const own = action.kind !== 'prompt' && quickUsesOwnModel(action.kind);
  const blocked = route ? '' : modelBlockReason(m, hasImage, hasMask);
  if (blocked) {
    showToast(`${m.label}: ${blocked}`, '', 'error');
    return false;
  }
  // Модели без канала образцов (Bria Expand, убрать фон) образцы и персонажа не шлём
  const noSamples = own || route?.maxReferences === 0;
  const withMask = plan.useMask && hasMask;
  const withMarks = (action.kind === 'prompt' || action.kind === 'removeMarked') && hasImage && marks.length > 0;
  try {
    const q = await api.quote(projectId, {
      provider: route?.provider ?? pv.key, model: route?.model ?? m.id, mode: 'auto', op: plan.op,
      count: route?.count ?? settings.count,
      hasMask: withMask, hasAnnotations: withMarks && hasAnnotationMark(marks), removal: plan.removal,
      references: noSamples ? 0 : getSamples(projectId).length, hasCharacter: !noSamples && !!prefs.characterSlug,
      width: size?.w ?? null, height: size?.h ?? null,
    });
    const legacy = isLegacyThread(thread);
    const version = legacy ? null : currentVersion(thread);
    const stepId = activeStepOf(thread);
    const src = activeSrc(projectId, thread);
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
      sourcePath: !stepId && file ? file : undefined,
      source, mask, annotated, ...samples,
      characterSlug: noSamples ? undefined : prefs.characterSlug ?? undefined,
      matchSourceSize: settings.matchSourceSize,
      ...(plan.aspectRatio ? { aspectRatio: plan.aspectRatio } : null),
      sessionId, threadId: thread.id, baseStepId: stepId ?? undefined, versionId: version?.id,
    });
    // Пометки ушли с запуском
    if (withMarks || withMask) setThreadMarks(thread.id, [], null);
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
  const blocked = m ? modelBlockReason(m, hasImage, hasMask) : '';
  const count = settings.count;
  const references = getSamples(projectId).length;

  const quoteReq: ImageEditQuoteRequest | null = pv && m && !blocked ? {
    provider: pv.key, model: m.id, mode: 'auto', op: pickOp(hasImage, hasMask), count,
    hasMask, hasAnnotations, references, hasCharacter: !!prefs.characterSlug,
    width: size?.w ?? null, height: size?.h ?? null,
  } : null;
  const { quote, loading } = useQuote(api, projectId, quoteReq);
  const unit = quote?.estimate.unit ?? pv?.priceUnit ?? 'usd';
  // Пока котировка едет — ориентир из каталога, чтобы цена не мигала
  const priceLabel = quote
    ? priceText(quote.estimate.amount, unit, quote.estimate.approx, count, quote.estimate)
    : m?.priceHint ? priceText(m.priceHint.amount * count, m.priceHint.unit, true, count) : variantsWord(count);
  const price = quote
    ? priceSum(quote.estimate.amount, unit, quote.estimate.approx, quote.estimate)
    : m?.priceHint ? priceSum(m.priceHint.amount * count, m.priceHint.unit, true) : null;

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
  };
}
