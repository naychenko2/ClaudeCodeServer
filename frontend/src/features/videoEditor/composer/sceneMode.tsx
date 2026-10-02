// Режим композера «Сцена» (слот composer-mode; макет v7): есть только при выбранной сцене, текст поля —
// просьба к съёмке: она добавляется к тексту сцены, кадры и настройки берутся из панели. Запуск идёт мимо
// агента, по свежей цене (деньги — только quote → job).

import { Clapperboard, Sparkles } from 'lucide-react';
import { FLAGS, getFlag, showToast, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { ComposerModeApi, ComposerModeCtx } from '../../../lib/subsystems/registryCore';
import { errorText, videoApi } from '../api';
import { currentResolved, runScene } from '../scene/actions';
import { useSceneQuote } from '../panel/useScene';
import { PRICE_UNKNOWN } from '../strip/summary';
import { hasClip, runReason } from '../scene/model';
import { videoScope, isPersonalScope } from '../scope';
import { getCatalog, getFocusedScene, getPriceHint, sceneDraftKey, useVideoThreads, useVideoStoreVersion } from '../store/videoStore';

function useModel(ctx: ComposerModeCtx) {
  const scope = videoScope(ctx.projectId);
  useVideoThreads(scope, ctx.sessionId);
  useVideoStoreVersion();
  const scene = getFocusedScene(ctx.sessionId);
  return { scope, scene, r: currentResolved(ctx.sessionId, scope, scene), price: getPriceHint(ctx.sessionId, scene?.sceneId ?? null) };
}

function SubmitLabel({ ctx }: { ctx: ComposerModeCtx }) {
  const { scope, scene, r, price } = useModel(ctx);
  useSceneQuote(scope, ctx.sessionId, scene, getCatalog(scope), r);
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap' }}>
      <Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      {hasClip(scene) ? 'Переснять' : 'Снять'} · {price ?? PRICE_UNKNOWN}
    </span>
  );
}

function Hint({ ctx }: { ctx: ComposerModeCtx }) {
  const { scene } = useModel(ctx);
  return (
    <span style={{ fontSize: FS.xs, color: C.textMuted }}>
      <b style={{ color: C.accent, fontWeight: 600 }}>Режим «Сцена»</b> · {scene?.name ?? 'сцена'} · просьба добавится к тексту сцены, уходит модели, не Claude
    </span>
  );
}

export const sceneMode: ComposerModeApi = {
  title: 'Сцена',
  icon: <Clapperboard size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
  isAvailable: ctx => getFlag(FLAGS.videoEditor) && !!getFocusedScene(ctx.sessionId),
  // Черновик выбранной сцены: клик по другой карточке уносит его с собой
  draftKey: ctx => {
    const s = getFocusedScene(ctx.sessionId);
    return s ? sceneDraftKey(s.sceneId) : null;
  },
  placeholder: () => 'Просьба к съёмке — например «медленнее, закат теплее»; пусто — снять по тексту сцены',
  submitLabel: ctx => <SubmitLabel ctx={ctx} />,
  hint: ctx => <Hint ctx={ctx} />,
  emptySubmit: ctx => {
    const scene = getFocusedScene(ctx.sessionId);
    if (!scene || !ctx.sessionId) return null;
    return { label: <SubmitLabel ctx={ctx} />, run: () => submit(ctx, '') };
  },
  onSubmit: (ctx, text) => submit(ctx, text),
};

async function submit(ctx: ComposerModeCtx, text: string) {
  const scene = getFocusedScene(ctx.sessionId);
  const sessionId = ctx.sessionId;
  if (!scene || !sessionId) throw new Error('Сцена не выбрана');
  const scope = videoScope(ctx.projectId);
  const r = currentResolved(sessionId, scope, scene);
  const catalog = getCatalog(scope);
  const reason = runReason({
    sessionId, r, personal: isPersonalScope(scope), providerOk: r.auto ? !!catalog?.providers.some(p => p.available) : !!r.provider?.available,
    providerReason: r.provider?.reason, running: false,
  });
  if (reason) { showToast(reason, '', 'info'); throw new Error(reason); }
  try {
    const quote = await videoApi.quote(scope, sessionId, {
      sessionId, sceneId: scene.sceneId, ...(r.provider && r.model ? { provider: r.provider.key, model: r.model.id } : {}),
      count: r.count, durationSec: r.durationSec, aspect: r.aspect, sound: r.sound,
    });
    if (!await runScene({ scope, sessionId, scene, r, quote }, text)) throw new Error('Съёмка не запущена');
  } catch (e) {
    if ((e as Error).message !== 'Съёмка не запущена') showToast(errorText(e, 'Цена не посчиталась'), '', 'error');
    throw e;
  }
}
