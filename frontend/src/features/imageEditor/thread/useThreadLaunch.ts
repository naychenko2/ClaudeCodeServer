// Запуск генерации в нить (ADR-019 §4, прямой запуск без агента): котировка по
// настройкам нити, источник — текущий шаг или файл, пометки редактора — маской и
// снимком. Задача уходит с sessionId + threadId: сервер делает её pendingJobId нити и
// пишет в ленту тихую строку «Вы запустили: …». Цена для полосы и кнопки композера —
// отсюда же, чтобы полоса и кнопка не разошлись.

import { useCallback, useMemo } from 'react';
import { api as appApi, showToast } from 'aihome_shell/kit';
import { AUTO_MODEL, imageEditorApi, type ImageEditCatalog, type ImageEditQuoteRequest } from '../api';
import {
  effectiveProvider, isRemovalPrompt, modelBlockReason, pickOp, priceSum, priceText, variantsWord,
} from '../format';
import { currentModel } from '../ProviderModelPicker';
import { exportAnnotated, exportMask, hasAnnotationMark, hasMaskMark, marksToJson } from '../marks';
import { quickPlan, quickUsesOwnModel, type LaunchAction, type LaunchPlan } from '../editorInputs';
import { useQuote } from '../useQuote';
import { loadCatalog, useCatalog } from './catalog';
import { effectiveSettings, getPrefs, setPrefs, usePrefs } from './prefs';
import { getThreadMarks, mutate, setThreadMarks, useThreadStoreVersion } from './threadStore';
import { threadsApi, type ImageThread, type ImageThreadSettings } from './threadsApi';

// Картинка позиции нити: шаг — из рабочей папки редактора, исходник — файл проекта
export function imageSrc(projectId: string, t: ImageThread, stepId: string | null): string | null {
  if (stepId) return imageEditorApi().stepUrl(projectId, stepId);
  return t.file ? appApi.files.fileUrl(projectId, t.file) : null;
}

export const threadHasImage = (t: ImageThread | null) => !!t && (!!t.currentStepId || !!t.file);

// Поставщик и модель по настройкам: модель не выбрана — умолчание админа у его поставщика
function resolveModel(catalog: ImageEditCatalog | null, settings: ImageThreadSettings) {
  const pv = catalog ? effectiveProvider(catalog, settings.provider ?? 'settings') : null;
  const fallback = pv && pv.key === catalog?.default.provider ? catalog.default.model : AUTO_MODEL;
  return { pv, m: currentModel(pv, settings.model ?? fallback) };
}

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('Картинка не загрузилась'));
    img.src = src;
  });
}

// Запуск в нить; true — задача запущена, причина отказа уже показана тостом
export async function launchThread(
  projectId: string, sessionId: string, thread: ImageThread, action: LaunchAction,
): Promise<boolean> {
  const api = imageEditorApi();
  const prefs = getPrefs(projectId);
  const settings = effectiveSettings(prefs, thread.settings);
  const { pv, m } = resolveModel(await loadCatalog(projectId), settings);
  if (!pv || !m) {
    showToast('Рисовать нечем: администратор не настроил поставщиков картинок', '', 'error');
    return false;
  }
  const { marks, size } = getThreadMarks(thread.id);
  const hasImage = threadHasImage(thread);
  const hasMask = hasImage && hasMaskMark(marks);
  const prompt = action.kind === 'prompt' ? action.prompt.trim() : '';
  const plan: LaunchPlan = action.kind === 'prompt'
    ? { op: pickOp(hasImage, hasMask), prompt, useMask: true, removal: hasMask && isRemovalPrompt(prompt) }
    : quickPlan(action.kind, action.ratio ?? '16:9');
  const own = action.kind !== 'prompt' && quickUsesOwnModel(action.kind);
  const blocked = own ? '' : modelBlockReason(m, hasImage, hasMask);
  if (blocked) {
    showToast(`${m.label}: ${blocked}`, '', 'error');
    return false;
  }
  const withMask = plan.useMask && hasMask;
  const withMarks = (action.kind === 'prompt' || action.kind === 'removeMarked') && hasImage && marks.length > 0;
  try {
    const q = await api.quote(projectId, {
      provider: pv.key, model: own ? AUTO_MODEL : m.id, mode: 'auto', op: plan.op, count: own ? 1 : settings.count,
      hasMask: withMask, hasAnnotations: withMarks && hasAnnotationMark(marks), removal: plan.removal,
      references: 0, hasCharacter: !own && !!prefs.characterSlug, width: size?.w ?? null, height: size?.h ?? null,
    });
    const stepId = thread.currentStepId;
    const src = imageSrc(projectId, thread, stepId);
    const source = stepId && src ? await fetch(src).then(r => r.blob()) : undefined;
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
      sourcePath: !stepId && thread.file ? thread.file : undefined,
      source, mask, annotated,
      characterSlug: own ? undefined : prefs.characterSlug ?? undefined,
      matchSourceSize: settings.matchSourceSize,
      ...(plan.aspectRatio ? { aspectRatio: plan.aspectRatio } : null),
      sessionId, threadId: thread.id, baseStepId: stepId ?? undefined,
    });
    // Пометки ушли с запуском
    if (withMarks || withMask) setThreadMarks(thread.id, [], null);
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

  const quoteReq: ImageEditQuoteRequest | null = pv && m && !blocked ? {
    provider: pv.key, model: m.id, mode: 'auto', op: pickOp(hasImage, hasMask), count,
    hasMask, hasAnnotations, references: 0, hasCharacter: !!prefs.characterSlug,
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
