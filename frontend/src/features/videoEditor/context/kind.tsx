// Вклад «видео» в слот context-kind (ADR-023, шаг 3ф-1): чипы действий сцены и фильма, превью основного
// объекта, «Чем» и параметры панели «Контекст», цена и запуск. Входы запуска бэкенд читает из стора контекста
// по ревизии, поэтому здесь только `op`, текст, `params` и `contextRevision`. Редакторы «Сцена» и «Монтаж»
// — шаг 3ф-2 (editor/): «Монтаж» и «Редактор сцены» открывают окна, а не прежнюю панель «Видео».

import { Clapperboard, Film } from 'lucide-react';
import {
  C, ICON_SIZE, ICON_STROKE, R, SP, showToast,
  type ChatContextItem, type ContextKindApi, type ContextKindCtx,
} from 'aihome_shell/kit';
import { videoApi } from '../api';
import { createScene, currentResolved } from '../scene/actions';
import { isPersonalScope, videoScope } from '../scope';
import { getCatalog, openVideoEditor, useVideoStoreVersion } from '../store/videoStore';
import { NO_AI_MODEL, shootExecutors } from './executors';
import { paramsFor, quoteAction, launchAction } from './run';
import { videoRefRoles } from './roles';
import { actionOf, FILM_KIND, SCENE_KIND, sceneOfPrimary, videoActions } from './state';

const PREVIEW_W = 160;

// Текущий клип сцены; нет клипа или фильм — значок
function VideoPreview({ ctx, item }: { ctx: ContextKindCtx; item: ChatContextItem }) {
  useVideoStoreVersion();
  const scene = sceneOfPrimary(ctx.sessionId, item as never);
  const versionId = scene ? scene.currentVersionId ?? scene.versions[scene.versions.length - 1]?.versionId : undefined;
  const box = { width: PREVIEW_W, borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgPanel, flexShrink: 0 } as const;
  if (scene && versionId) {
    return (
      <video
        data-ctx-preview="video" controls muted playsInline preload="metadata" aria-label={item.label}
        src={videoApi.versionFileUrl(videoScope(ctx.projectId), ctx.sessionId, scene.sceneId, versionId)}
        style={{ ...box, aspectRatio: '16 / 9', objectFit: 'cover', display: 'block' }}
      />
    );
  }
  const Icon = item.kind === FILM_KIND ? Film : Clapperboard;
  return (
    <div data-ctx-preview="video" style={{ ...box, aspectRatio: '16 / 9', display: 'flex', alignItems: 'center', justifyContent: 'center', color: C.textMuted, padding: SP.xs }}>
      <Icon size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
    </div>
  );
}

export const videoKindApi: ContextKindApi = {
  kinds: [SCENE_KIND, FILM_KIND],
  icon: kind => kind === FILM_KIND
    ? <Film size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    : <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />,
  actions: (ctx, s) => videoActions(ctx, s),
  refRoles: (_ctx, primary, candidateKind) => (primary.kind === SCENE_KIND ? videoRefRoles(candidateKind) : []),
  preview: (ctx, item) => <VideoPreview ctx={ctx} item={item} />,
  // «Открыть редактор» в «С чем»: у сцены — текст, пропорции и звук, у фильма — монтаж
  editor: (ctx, item) => item.kind === FILM_KIND
    ? { label: 'Открыть монтаж', hint: 'Порядок сцен, склейки, подрезка, музыка, сценарий', open: () => openVideoEditor(ctx.sessionId, 'film') }
    : sceneOfPrimary(ctx.sessionId, item as never)
      ? { label: 'Редактор сцены', hint: 'Текст сцены, пропорции, звук клипа', open: () => openVideoEditor(ctx.sessionId, 'scene') }
      : null,
  executors: (ctx, actionId) => {
    const found = actionOf(ctx, actionId);
    if (found?.action.op === 'build') return NO_AI_MODEL;
    const scene = found ? sceneOfPrimary(ctx.sessionId, found.primary) : null;
    if (!found || !scene) return null;
    return shootExecutors({
      scope: found.scope, sessionId: ctx.sessionId, catalog: getCatalog(found.scope),
      personal: isPersonalScope(found.scope), r: currentResolved(ctx.sessionId, found.scope, scene),
    });
  },
  params: (ctx, actionId) => {
    const found = actionOf(ctx, actionId);
    const op = found?.action.op;
    const scene = found ? sceneOfPrimary(ctx.sessionId, found.primary) : null;
    if (!found || !op || !scene) return [];
    return paramsFor(getCatalog(found.scope), currentResolved(ctx.sessionId, found.scope, scene), op);
  },
  create: {
    title: 'Видео',
    hint: 'новая сцена между двумя кадрами',
    icon: <Clapperboard size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />,
    // Сцена становится основным объектом сама (фокус «Видео» → контекст), панель не прыгает
    run: ctx => {
      void createScene(videoScope(ctx.projectId), ctx.sessionId).then(ok => { if (!ok) showToast('Не удалось завести сцену', '', 'error'); });
    },
  },
  quote: quoteAction,
  launch: launchAction,
};
