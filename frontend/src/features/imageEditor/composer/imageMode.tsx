// Режим композера «Картинка» (слот composer-mode, записка v3 «Композер: «Чат» и
// «Картинка»»): есть только при выбранной картинке, текст уходит промптом прямо в модель,
// без агента. Включённый режим возвращает полосу «Картинки»: запуск платный, цена и модель
// должны быть на виду до нажатия.

import { useEffect } from 'react';
import { Image as ImageIcon, Sparkles } from 'lucide-react';
import { requestStrip, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { ComposerModeApi, ComposerModeCtx } from '../../../lib/subsystems/registryCore';
import { currentVersion, isEmptyThread, isLegacyThread, lastLaunchPrompt, ORIGIN, threadName, versionName } from '../thread/model';
import { getFocusedThread, getImageModeRequest, IMAGES_STRIP, useThreads } from '../thread/threadStore';
import { launchThread, useThreadLaunch } from '../thread/useThreadLaunch';

function useFocused(ctx: ComposerModeCtx) {
  const state = useThreads(ctx.projectId, ctx.sessionId);
  return state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
}

// «✦ Изменить · ≈ $0.15», у черновика «✦ Сгенерировать · …», у локальной модели «бесплатно»
function SubmitLabel({ ctx }: { ctx: ComposerModeCtx }) {
  const thread = useFocused(ctx);
  const L = useThreadLaunch(ctx.projectId ?? '', ctx.sessionId, thread);
  const verb = thread && (thread.file || !isEmptyThread(thread)) ? 'Изменить' : 'Сгенерировать';
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap' }}>
      <Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      {verb}{L.price ? ` · ${L.price}` : ''}
    </span>
  );
}

// «Промпт модели · уходит прямо в FLUX Fill, без агента»
function Hint({ ctx }: { ctx: ComposerModeCtx }) {
  const thread = useFocused(ctx);
  const L = useThreadLaunch(ctx.projectId ?? '', ctx.sessionId, thread);
  useEffect(() => { if (ctx.sessionId) requestStrip(ctx.sessionId, IMAGES_STRIP); }, [ctx.sessionId]);
  return (
    <span style={{ fontSize: FS.xs, color: C.textMuted }}>
      <b style={{ color: C.accent, fontWeight: 600 }}>Промпт модели</b> · уходит прямо в {L.model?.label ?? 'модель'}, без агента
    </span>
  );
}

export const imageMode: ComposerModeApi = {
  title: 'Картинка',
  icon: <ImageIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
  isAvailable: ctx => !!getFocusedThread(ctx.sessionId),
  // Черновик: у пустой картинки режим «Чат» бессмыслен. Иначе — по просьбе входа
  // «Редактировать» / «Нарисовать»; выбор картинки агентом режим сам не меняет
  autoSelect: ctx => {
    const t = getFocusedThread(ctx.sessionId);
    if (!t) return null;
    if (!t.file && isEmptyThread(t)) return `draft:${t.id}`;
    const n = getImageModeRequest(ctx.sessionId);
    return n ? `request:${n}` : null;
  },
  // Промпт последнего запуска выбранной картинки — чтобы поправить, а не набирать заново.
  // Ключ нити — и без запусков: первый запуск после отправки не новый повод
  prefill: ctx => {
    const t = getFocusedThread(ctx.sessionId);
    return t ? { key: t.id, text: lastLaunchPrompt(t) || null } : null;
  },
  placeholder: ctx => {
    const t = getFocusedThread(ctx.sessionId);
    if (!t || (!t.file && isEmptyThread(t))) return 'Опишите новую картинку — например, «Аня в кафе у окна»';
    const v = isLegacyThread(t) ? null : currentVersion(t);
    const what = v && v.id !== ORIGIN ? versionName(v).replace('версия', 'версии') : threadName(t);
    return `Что изменить в ${what}? Опишите словами или отметьте место в редакторе`;
  },
  submitLabel: ctx => <SubmitLabel ctx={ctx} />,
  hint: ctx => <Hint ctx={ctx} />,
  onSubmit: async (ctx, text) => {
    const t = getFocusedThread(ctx.sessionId);
    if (!t || !ctx.projectId || !ctx.sessionId) return;
    const ok = await launchThread(ctx.projectId, ctx.sessionId, t, { kind: 'prompt', prompt: text });
    // Текст остаётся в поле, если запуск не прошёл: ядро чистит поле только без исключения
    if (!ok) throw new Error('Генерация не запущена');
  },
};
