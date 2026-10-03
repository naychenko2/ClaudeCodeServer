// Чтение состояния «Видео» для вида контекста: основной объект → сцена или фильм из сторов → вход каталога
// действий. Вне React: хост зовёт `actions` на каждый рендер, поэтому всё берётся из кэшей сторов.

import {
  getChatContextState, notifyKindChanged,
  type ChatContextPrimary, type ChatContextRef, type ContextAction, type ContextKindCtx,
} from 'aihome_shell/kit';
import type { FilmState, VideoScene } from '../api';
import { lastBuild } from '../film/model';
import { drawInImages, editFrame, frameBOf, openFilmPanel, wireFrameBinding } from '../scene/actions';
import { hasClip } from '../scene/model';
import { isPersonalScope, videoScope } from '../scope';
import { frameInputOf, frameOfRef, frameRefOf, setFrameRef, type FrameSlot } from '../store/frameRefs';
import { ensureVideoThreads, getFilm, getThreadsState, loadFilm, subscribeVideoStore } from '../store/videoStore';
import { buildFilmActions, buildSceneActions } from './actions';
import { buildFrameMenu } from './frameMenu';
import { openFramePicker } from './framePicker';

export const SCENE_KIND = 'video-scene';
export const FILM_KIND = 'video-film';

// Подписка одна на вкладку; сцены и фильм запрашиваются один раз на чат (каталог и префы приходят с состоянием)
let _bridged = false;
const _warmed = new Set<string>();

export function warm(ctx: ContextKindCtx, primary: ChatContextPrimary) {
  if (!_bridged) {
    _bridged = true;
    subscribeVideoStore(notifyKindChanged);
  }
  // «Нарисовать в „Картинках“»: нить для кадра, получив картинку, сама встаёт кадром — панель «Видео» может быть закрыта
  wireFrameBinding();
  const scope = videoScope(ctx.projectId);
  if (!_warmed.has(ctx.sessionId)) {
    _warmed.add(ctx.sessionId);
    void ensureVideoThreads(scope, ctx.sessionId);
  }
  const path = filmPathOf(primary);
  if (path && !isPersonalScope(scope)) void loadFilm(scope, ctx.sessionId, path);
}

export function sceneOfPrimary(sessionId: string, primary: ChatContextPrimary | null): VideoScene | null {
  const id = primary?.kind === SCENE_KIND ? primary.ref.sceneId : null;
  return typeof id === 'string' ? getThreadsState(sessionId).scenes.find(s => s.sceneId === id) ?? null : null;
}

export function filmPathOf(primary: ChatContextPrimary | null): string | null {
  const path = primary?.kind === FILM_KIND ? primary.ref.filmPath : null;
  return typeof path === 'string' ? path : null;
}

// Сцена до выбранной в порядке чата: её кадр B — естественный кадр A новой («стык без скачка»)
function previousScene(sessionId: string, scene: VideoScene): VideoScene | null {
  const scenes = getThreadsState(sessionId).scenes;
  const idx = scenes.findIndex(s => s.sceneId === scene.sceneId);
  return idx > 0 ? scenes[idx - 1] : null;
}

function sceneMenu(ctx: ContextKindCtx, scene: VideoScene, refs: readonly ChatContextRef[]) {
  const scope = videoScope(ctx.projectId);
  const personal = isPersonalScope(scope);
  return (slot: FrameSlot) => () => {
    const ref = frameRefOf(refs, slot);
    const prev = previousScene(ctx.sessionId, scene);
    const prevB = prev ? frameBOf(prev) : null;
    const prevInput = prevB ? frameInputOf(prevB, personal) : null;
    return buildFrameMenu({
      slot, personal, frame: ref ? { label: ref.label } : null,
      prev: { exists: !!prev, hasFrameB: !!prevB, usable: !!prevInput },
      editInImages: () => {
        const frame = ref ? frameOfRef(ref) : null;
        if (frame) void editFrame(scope, ctx.sessionId, scene, slot, frame);
      },
      drawInImages: () => { void drawInImages(scope, ctx.sessionId, slot); },
      fromProject: () => openFramePicker({ sessionId: ctx.sessionId, scope, slot, folder: scene.folder ? `${scene.folder}/кадры` : '' }),
      fromPrevious: () => { if (prevInput) void setFrameRef(ctx.sessionId, slot, prevInput); },
      clear: () => { void setFrameRef(ctx.sessionId, slot, null); },
    });
  };
}

// Сцену или фильм не нашли (ещё грузятся) — действий нет, поле остаётся на прежнем «Чат | X»
export function videoActions(
  ctx: ContextKindCtx, s: { primary: ChatContextPrimary; refs: readonly ChatContextRef[] },
): readonly ContextAction[] {
  warm(ctx, s.primary);
  if (s.primary.kind === SCENE_KIND) {
    const scene = sceneOfPrimary(ctx.sessionId, s.primary);
    if (!scene) return [];
    return buildSceneActions({
      shot: hasClip(scene),
      hasFrameA: !!frameRefOf(s.refs, 'A'),
      hasFrameB: !!frameRefOf(s.refs, 'B'),
      sceneTextEmpty: !scene.settings.text.trim(),
      frameMenu: sceneMenu(ctx, scene, s.refs),
    });
  }
  const path = filmPathOf(s.primary);
  if (!path) return [];
  const film: FilmState | null = getFilm(ctx.sessionId, path).state;
  return buildFilmActions({
    built: !!film && lastBuild(film.document) !== null,
    empty: !!film && film.document.items.length === 0,
    openMontage: () => openFilmPanel(ctx.sessionId, path),
  });
}

// Действие по id для текущего основного объекта чата: «Чем», параметры и цена зовутся с actionId
export function actionOf(ctx: ContextKindCtx, actionId: string) {
  const { primary, refs } = getChatContextState(ctx.sessionId);
  if (!primary) return null;
  const action = videoActions(ctx, { primary, refs }).find(a => a.id === actionId);
  return action ? { action, primary, refs, scope: videoScope(ctx.projectId) } : null;
}
