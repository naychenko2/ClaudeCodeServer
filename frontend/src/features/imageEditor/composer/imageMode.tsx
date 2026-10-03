// Режим композера «Картинка» (слот composer-mode, записка v3 «Композер: «Чат» и
// «Картинка»»): есть только при выбранной картинке, текст уходит промптом прямо в модель,
// без агента. Включённый режим возвращает полосу «Картинки»: запуск платный, цена и модель
// должны быть на виду до нажатия.
// С флагом image-panel-v5 режим есть и без выбранной картинки, если чат в «Создать»: первая
// отправка заводит черновик «Новая картинка» и запускает в него. В «Создать» пустое поле
// после генерации даёт «↻ Ещё N · цена» — повтор прошлого запуска (слот emptySubmit).

import { useEffect } from 'react';
import { Image as ImageIcon, RotateCw, Sparkles } from 'lucide-react';
import { requestStrip, useIsMobile, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { ComposerEmptySubmit, ComposerModeApi, ComposerModeCtx } from '../../../lib/subsystems/registryCore';
import { enterScope } from '../scope';
import { createDraft } from '../thread/actions';
import { getStoredImageMode, modeAware } from '../thread/modeState';
import { currentVersion, isEmptyThread, isLegacyThread, lastLaunchPrompt, ORIGIN, threadName, versionName } from '../thread/model';
import { getFocusedThread, getImageModeRequest, imageDraftKey, IMAGES_STRIP, useThreads } from '../thread/threadStore';
import { launchMode, launchThread, useThreadLaunch } from '../thread/useThreadLaunch';
import { mobileSendPrice } from '../strip/settingsToggle';
import { setImageComposerText } from './composerText';

function useFocused(ctx: ComposerModeCtx) {
  const state = useThreads(enterScope(ctx.projectId, ctx.sessionId), ctx.sessionId);
  return state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
}

// Чат в «Создать» (флаг image-panel-v5): режим поля есть и без выбранной картинки
const createMode = (sessionId: string | null) => modeAware() && getStoredImageMode(sessionId) === 'create';

// Режим поля есть: выбрана картинка или чат в «Создать»
export const imageModeAvailable = (ctx: ComposerModeCtx) => !!getFocusedThread(ctx.sessionId) || createMode(ctx.sessionId);

// Цена на кнопке: с флагом image-panel-v5 на телефоне — без времени и очереди
function useSendPrice(price: string | null | undefined, v5: boolean): string | null {
  const mobile = useIsMobile();
  if (!price) return null;
  return v5 && mobile ? mobileSendPrice(price) : price;
}

// «✦ Изменить · ≈ $0.15», у черновика «✦ Сгенерировать · …», у локальной модели «бесплатно»
function SubmitLabel({ ctx }: { ctx: ComposerModeCtx }) {
  const thread = useFocused(ctx);
  const L = useThreadLaunch(enterScope(ctx.projectId, ctx.sessionId), ctx.sessionId, thread);
  const price = useSendPrice(L.price, !!L.imageMode);
  // С режимом глагол — по режиму: в «Создать» и при выбранной картинке рисуем новую
  const verb = L.imageMode
    ? L.imageMode === 'create' ? 'Сгенерировать' : 'Изменить'
    : thread && (thread.file || !isEmptyThread(thread)) ? 'Изменить' : 'Сгенерировать';
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap' }}>
      <Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      {verb}{price ? ` · ${price}` : ''}
    </span>
  );
}

// «↻ Ещё 2 · бесплатно» — повтор прошлого запуска с текущим выбором «Создать»
function AgainLabel({ ctx }: { ctx: ComposerModeCtx }) {
  const thread = useFocused(ctx);
  const L = useThreadLaunch(enterScope(ctx.projectId, ctx.sessionId), ctx.sessionId, thread);
  const price = useSendPrice(L.price, !!L.imageMode);
  return (
    <span data-images-again="" style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap' }}>
      <RotateCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      Ещё {L.count}{price ? ` · ${price}` : ''}
    </span>
  );
}

// Повтор прошлого запуска: чат в «Создать», у выбранной картинки есть промпт прошлого запуска.
// null — повторять нечего (кнопка при пустом поле гаснет, как раньше)
export function againPrompt(sessionId: string | null): string | null {
  const t = getFocusedThread(sessionId);
  if (!t || launchMode(sessionId, t) !== 'create') return null;
  return lastLaunchPrompt(t);
}

// Тот же запуск в ту же нить; true — задача запущена
export async function launchAgain(projectId: string | null, sessionId: string | null): Promise<boolean> {
  const t = getFocusedThread(sessionId);
  const prompt = againPrompt(sessionId);
  if (!t || !sessionId || !prompt) return false;
  return launchThread(enterScope(projectId, sessionId), sessionId, t, { kind: 'prompt', prompt });
}

// «Промпт модели · уходит прямо в FLUX Fill, без агента»
function Hint({ ctx }: { ctx: ComposerModeCtx }) {
  const thread = useFocused(ctx);
  const L = useThreadLaunch(enterScope(ctx.projectId, ctx.sessionId), ctx.sessionId, thread);
  useEffect(() => { if (ctx.sessionId) requestStrip(ctx.sessionId, IMAGES_STRIP); }, [ctx.sessionId]);
  return (
    <span style={{ fontSize: FS.xs, color: C.textMuted }}>
      <b style={{ color: C.accent, fontWeight: 600 }}>Промпт модели</b> · уходит прямо в {L.model?.label ?? 'модель'}, без агента
    </span>
  );
}

// Имя вклада composer-mode: по нему низ панели «Картинки» просит поле ввода отправить текст
export const IMAGE_COMPOSER_MODE = 'image';

export const imageMode: ComposerModeApi = {
  title: 'Картинка',
  icon: <ImageIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
  isAvailable: imageModeAvailable,
  strip: IMAGES_STRIP,
  // Только по явной просьбе человека: «Нарисовать новую», «Редактировать» / «Нарисовать»
  // из дерева. Выбор картинки агентом — и черновика тоже — режим сам не меняет: человек
  // продолжает разговор с агентом, а не пишет промпт модели
  autoSelect: ctx => {
    if (!imageModeAvailable(ctx)) return null;
    const n = getImageModeRequest(ctx.sessionId);
    return n ? `request:${n}` : null;
  },
  // Промпт последнего запуска выбранной картинки — чтобы поправить, а не набирать заново.
  // Ключ нити — и без запусков: первый запуск после отправки не новый повод
  prefill: ctx => {
    const t = getFocusedThread(ctx.sessionId);
    return t ? { key: t.id, text: lastLaunchPrompt(t) || null } : null;
  },
  // Промпт — черновик выбранной картинки: клик по другой карточке уносит его с собой
  draftKey: ctx => {
    const t = getFocusedThread(ctx.sessionId);
    return t ? imageDraftKey(t.id) : null;
  },
  placeholder: ctx => {
    const t = getFocusedThread(ctx.sessionId);
    if (!t || (!t.file && isEmptyThread(t)) || launchMode(ctx.sessionId, t) === 'create') return 'Опишите новую картинку — например, «Аня в кафе у окна»';
    const v = isLegacyThread(t) ? null : currentVersion(t);
    const what = v && v.id !== ORIGIN ? versionName(v).replace('версия', 'версии') : threadName(t);
    return `Что изменить в ${what}? Опишите словами или отметьте место в редакторе`;
  },
  submitLabel: ctx => <SubmitLabel ctx={ctx} />,
  hint: ctx => <Hint ctx={ctx} />,
  onSubmit: async (ctx, text) => {
    if (!ctx.sessionId) return;
    const scope = enterScope(ctx.projectId, ctx.sessionId);
    let t = getFocusedThread(ctx.sessionId);
    // «Создать» без выбранной картинки: сначала черновик, потом запуск в него — строго по
    // очереди, нить берётся из свежего состояния стора после ответа на создание
    if (!t && createMode(ctx.sessionId)) {
      if (!await createDraft(scope, ctx.sessionId, '', 'none')) throw new Error('Черновик не создан');
      t = getFocusedThread(ctx.sessionId);
    }
    if (!t) return;
    const ok = await launchThread(scope, ctx.sessionId, t, { kind: 'prompt', prompt: text });
    // Текст остаётся в поле, если запуск не прошёл: ядро чистит поле только без исключения
    if (!ok) throw new Error('Генерация не запущена');
  },
  emptySubmit: (ctx): ComposerEmptySubmit | null => {
    if (!againPrompt(ctx.sessionId)) return null;
    return {
      label: <AgainLabel ctx={ctx} />,
      run: async () => { if (!await launchAgain(ctx.projectId, ctx.sessionId)) throw new Error('Генерация не запущена'); },
    };
  },
  // Текст поля — низу панели «Картинки»: на пустом поле его кнопка тоже «↻ Ещё N»
  onTextChange: (ctx, text) => { if (ctx.sessionId) setImageComposerText(ctx.sessionId, text); },
};
