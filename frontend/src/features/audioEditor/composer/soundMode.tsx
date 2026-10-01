// Режим композера «Звук» (слот composer-mode; макет audio-editor-v2-proposal.md, раздел
// «Композер»): есть только при выбранном звуке, текст уходит прямо в модель, без агента.
// Плейсхолдер и глагол кнопки — по операции из настроек нити; включённый режим возвращает
// полосу «Звук»: запуск может быть платным, цена и модель должны быть на виду.

import { useEffect } from 'react';
import { AudioLines, Sparkles } from 'lucide-react';
import { FLAGS, getFlag, requestStrip, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { ComposerModeApi, ComposerModeCtx } from '../../../lib/subsystems/registryCore';
import { MODE_LABEL, opInfo } from '../ops';
import { audioScope } from '../scope';
import { stripModel } from '../strip/SoundStrip';
import { launchFromComposer } from '../thread/actions';
import { getFocusedThread, getSoundModeRequest, SOUND_STRIP, useAudioThreads } from '../thread/threadStore';

function useModel(ctx: ComposerModeCtx) {
  const state = useAudioThreads(audioScope(ctx.projectId), ctx.sessionId);
  const thread = state.focus ? state.threads.find(t => t.id === state.focus) ?? null : null;
  return stripModel(ctx.projectId, ctx.sessionId, thread);
}

// «✦ Озвучить · бесплатно»
function SubmitLabel({ ctx }: { ctx: ComposerModeCtx }) {
  const { launch } = useModel(ctx);
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap' }}>
      <Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      {opInfo(launch.op)?.run ?? 'Запустить'}{launch.price ? ` · ${launch.price}` : ''}
    </span>
  );
}

// «Режим «Звук» · Голос → Озвучить · текст уходит модели, не Claude»
function Hint({ ctx }: { ctx: ComposerModeCtx }) {
  const { launch } = useModel(ctx);
  useEffect(() => { if (ctx.sessionId) requestStrip(ctx.sessionId, SOUND_STRIP); }, [ctx.sessionId]);
  return (
    <span style={{ fontSize: FS.xs, color: C.textMuted }}>
      <b style={{ color: C.accent, fontWeight: 600 }}>Режим «Звук»</b> · {MODE_LABEL[launch.mode]} → {opInfo(launch.op)?.label} · текст уходит модели, не Claude
    </span>
  );
}

export function soundPlaceholder(projectId: string | null, sessionId: string | null): string {
  const { launch } = stripModel(projectId, sessionId, getFocusedThread(sessionId));
  return opInfo(launch.op)?.placeholder ?? 'Текст для модели';
}

export const soundMode: ComposerModeApi = {
  title: 'Звук',
  icon: <AudioLines size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
  isAvailable: ctx => getFlag(FLAGS.audioEditor) && !!getFocusedThread(ctx.sessionId),
  // Только по явной просьбе человека («✦ Новый звук»): выбор звука агентом режим не меняет
  autoSelect: ctx => {
    if (!getFocusedThread(ctx.sessionId)) return null;
    const n = getSoundModeRequest(ctx.sessionId);
    return n ? `request:${n}` : null;
  },
  placeholder: ctx => soundPlaceholder(ctx.projectId, ctx.sessionId),
  submitLabel: ctx => <SubmitLabel ctx={ctx} />,
  hint: ctx => <Hint ctx={ctx} />,
  onSubmit: async (ctx, text) => {
    const t = getFocusedThread(ctx.sessionId);
    if (!t || !ctx.sessionId) return;
    const ok = await launchFromComposer(audioScope(ctx.projectId), ctx.sessionId, t, text);
    // Текст остаётся в поле, если запуск не прошёл: ядро чистит поле только без исключения
    if (!ok) throw new Error('Звук не запущен');
  },
};
