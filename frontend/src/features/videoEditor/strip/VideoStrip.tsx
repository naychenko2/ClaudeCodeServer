// Полоса «Видео» над композером (реестр composer-strip; макет v7, «Полоса «Видео»»): две сводки-чипа —
// «Сцена» и «Фильм»; настроек в себе не раскрывает — это вход в панель «Видео» на нужной вкладке.
// Заголовок-переключатель — от хоста. ▾ у чипа сцены — меню сцен чата (GenerationPickMenu), ✕ — снять
// выбор. Высота 48 px на десктопе, 42 на телефоне, свёрнутая строка — 30 px (на телефоне 42, ради «⌄» 40×40).

import { useRef, useState, useSyncExternalStore } from 'react';
import type { KeyboardEvent } from 'react';
import { ChevronDown, ChevronRight, ChevronUp, Clapperboard, Cpu, Film, Plus, X } from 'lucide-react';
import {
  Button, C, FS, GenerationPickMenu, IconButton, ICON_SIZE, ICON_STROKE, R, ReleaseNotice, SP, gitRelTime, pickRows,
  type GenerationPickExtra, type GenerationPickRow,
} from 'aihome_shell/kit';
import type { ComposerStripCtx } from '../../../lib/subsystems/registryCore';
import { createScene, currentResolved, openFilmPanel, openScenePanel, releaseFocus, selectSceneByHuman, undoRelease, videoReleaseUndo } from '../scene/actions';
import { isPersonalScope, videoScope } from '../scope';
import {
  filmName, getCatalog, getFocusedFilmPath, getFocusedScene, getJobsOf, getPriceHint, useFilm, useVideoStoreVersion, useVideoThreads,
} from '../store/videoStore';
import { filmChip, sceneChip, staleFilm } from './summary';
import type { VideoScene } from '../api';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// Сводка в меню переключателя полос — та же, что в чипе
export function videoStripStatus(projectId: string | null, sessionId: string | null): string {
  const scope = videoScope(projectId);
  const scene = getFocusedScene(sessionId);
  const r = currentResolved(sessionId, scope, scene);
  return sceneChip(scene, r, getPriceHint(sessionId, scene?.sceneId ?? null)).text;
}

function QueueBadge({ children }: { children: string }) {
  return (
    <span data-video-queue="" title="Очередь видеокарты для локальных задач" style={{
      display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 1, minWidth: 0, height: 22, padding: `0 ${SP.sm}px`,
      fontSize: FS.xs, color: C.textSecondary, background: C.bgInset, borderRadius: R.full, whiteSpace: 'nowrap',
      overflow: 'hidden', textOverflow: 'ellipsis',
    }}>{ic(Cpu)}<span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{children}</span></span>
  );
}

// «снимаем · 40 %», «GPU: 2-я в очереди», «собираем · 60 %»
function workText(sessionId: string | null, scene: VideoScene | null, build?: { state: string; progress: number }): string | null {
  const j = getJobsOf(sessionId, scene?.sceneId ?? null)[0];
  if (j) {
    if (j.stage === 'queued') return j.queuePosition ? `GPU: ${j.queuePosition}-я в очереди` : 'ждём очередь';
    return `снимаем · ${Math.round(((j.variant - 1) / Math.max(1, j.count)) * 100 + 50 / Math.max(1, j.count))} %`;
  }
  if (scene?.launches.some(l => l.status === 'running')) return 'снимаем';
  if (build?.state === 'waiting') return 'ждём очередь сборки';
  if (build?.state === 'running') return `собираем · ${Math.round(build.progress * 100)} %`;
  return null;
}

export function VideoStrip({ ctx }: { ctx: ComposerStripCtx }) {
  const { sessionId, isMobile, collapsed, setCollapsed, switcher } = ctx;
  const scope = videoScope(ctx.projectId);
  const personal = isPersonalScope(scope);
  const state = useVideoThreads(scope, sessionId);
  useVideoStoreVersion();
  const scene = getFocusedScene(sessionId);
  const r = currentResolved(sessionId, scope, scene);
  const chip = sceneChip(scene, r, getPriceHint(sessionId, scene?.sceneId ?? null));
  const filmPath = personal ? null : getFocusedFilmPath(sessionId);
  const film = useFilm(scope, filmPath ? sessionId : null, filmPath);
  const fchip = filmPath ? filmChip(filmName(filmPath), film.state) : null;
  const filmStale = staleFilm(film.state);
  const work = workText(sessionId, scene, film.state?.build);
  const [menuAt, setMenuAt] = useState<DOMRect | null>(null);
  const bar = useRef<HTMLDivElement>(null);
  const offer = useSyncExternalStore(videoReleaseUndo.subscribe, videoReleaseUndo.current, videoReleaseUndo.current);
  const undo = offer && offer.snapshot.sessionId === sessionId ? offer : null;
  const catalogReady = !!getCatalog(scope);
  const open = () => openScenePanel(sessionId);
  const release = () => { if (sessionId) void releaseFocus(scope, sessionId, scene); };
  const title = switcher ?? (
    <span title="Видео" style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
      {ic(Clapperboard)}{!isMobile && 'Видео'}
    </span>
  );

  const rows: GenerationPickRow[] = menuAt
    ? pickRows(state.scenes.map(s => ({ id: s.sceneId, at: Date.parse(s.createdAt) || 0, s })), { excludeId: scene?.sceneId ?? null })
      .map(x => ({
        id: x.id, name: x.s.name,
        sub: [x.s.versions.length ? `${x.s.versions.length} вар.` : 'без клипа', gitRelTime(x.s.createdAt)].join(' · '),
        icon: ic(Film),
      }))
    : [];
  const extras: GenerationPickExtra[] = [{
    key: 'new', label: 'Новая сцена', icon: ic(Plus), onClick: () => { setMenuAt(null); if (sessionId) void createScene(scope, sessionId); },
  }];
  const menu = menuAt && (
    <GenerationPickMenu title="Сцены чата" subtitle="Выбрать сцену для панели «Видео»" rows={rows} extras={extras} isMobile={isMobile}
      emptyText="В этом чате других сцен пока нет" onClose={() => setMenuAt(null)} anchor={menuAt}
      onPick={id => { setMenuAt(null); if (sessionId) void selectSceneByHuman(scope, sessionId, id); }} />
  );

  if (collapsed) {
    const expand = () => setCollapsed(false);
    return (
      <div role="button" tabIndex={0} data-composer-strip="video" data-video-strip="mini" title="Развернуть полосу «Видео»"
        onClick={expand}
        onKeyDown={(e: KeyboardEvent) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); expand(); } }}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, height: isMobile ? 42 : 30, margin: isMobile ? '6px 0' : '4px 0 6px',
          padding: isMobile ? '0 0 0 4px' : '0 6px 0 4px', boxSizing: 'border-box', minWidth: 0, cursor: 'pointer',
          background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.lg,
        }}>
        {title}
        <span data-video-summary="" title="Открыть сцену в панели «Видео»" onClick={e => { e.stopPropagation(); open(); }}
          style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {isMobile ? chip.short : chip.text}
        </span>
        {fchip && (
          <span data-video-film-summary="" title="Открыть фильм в панели «Видео»" onClick={e => { e.stopPropagation(); openFilmPanel(sessionId); }}
            style={{ flexShrink: 0, fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'nowrap' }}>
            🎞 {isMobile ? fchip.short : `${filmName(filmPath!)} · ${fchip.short}`}
          </span>
        )}
        <span data-video-mini-expand="" style={{
          display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, color: C.textMuted,
          ...(isMobile && { width: 40, height: 40 }),
        }}>{ic(ChevronDown, ICON_SIZE.sm)}</span>
      </div>
    );
  }

  return (
    <>
      {undo && (
        <div style={{ position: 'relative', height: 0 }}>
          <div data-video-release="" style={{ position: 'absolute', left: 0, right: 0, bottom: SP.xs }}>
            <ReleaseNotice text={undo.text} onUndo={() => { void undoRelease(); }} isMobile={isMobile} />
          </div>
        </div>
      )}
      <div ref={bar} data-composer-strip="video" data-video-strip="full" style={{
        position: 'relative', display: 'flex', alignItems: 'center', gap: isMobile ? 0 : 8, boxSizing: 'border-box', minWidth: 0,
        height: isMobile ? 42 : 48, margin: isMobile ? '6px 0' : '10px 0 8px', padding: isMobile ? 0 : '0 8px',
        background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
      }}>
        {title}
        <span data-video-chip="scene" style={{ display: 'inline-flex', alignItems: 'center', minWidth: 0, flex: isMobile ? '1 1 0' : '0 1 auto', gap: 2 }}>
          <Button size="xs" variant="secondary" title="Открыть сцену в панели «Видео»" onClick={open}
            style={{ minWidth: 0, flex: '0 1 auto', height: 28, border: `1px solid ${scene ? C.accent : C.border}`, background: C.bgWhite }}>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
              {ic(Clapperboard)}
              <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{isMobile ? chip.short : chip.text}</span>
              {chip.warn && scene && <span title="Снять пока нельзя: нет кадра или текста" style={{ color: C.warningText }}>⚠</span>}
              {ic(isMobile ? ChevronUp : ChevronRight)}
            </span>
          </Button>
          {state.scenes.length > 0 && (
            <IconButton size="xs" title="Сцены чата" ariaLabel="Сцены чата" onClick={e => setMenuAt((e.currentTarget as HTMLElement).getBoundingClientRect())}>{ic(ChevronDown)}</IconButton>
          )}
          {scene && !isMobile && <IconButton size="xs" title="Снять выбор — новая сцена" ariaLabel="Снять выбор — новая сцена" onClick={release}>{ic(X)}</IconButton>}
        </span>
        {fchip && (
          <span data-video-chip="film" style={{ display: 'inline-flex', minWidth: 0, flex: '0 1 auto', position: 'relative' }}>
            <Button size="xs" variant="secondary" title={filmStale ? 'Фильм изменён после сборки — пересоберите' : 'Открыть фильм в панели «Видео»'}
              onClick={() => openFilmPanel(sessionId)}
              style={{ minWidth: 0, flex: '0 1 auto', height: 28, border: `1px solid ${C.border}`, background: C.bgWhite }}>
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                {ic(Film)}
                <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{isMobile ? fchip.short : fchip.text}</span>
                {filmStale && <span data-video-film-dot="" aria-label="Фильм изменён после сборки" style={{ width: 7, height: 7, borderRadius: R.full, background: C.info, flexShrink: 0 }} />}
                {ic(isMobile ? ChevronUp : ChevronRight)}
              </span>
            </Button>
          </span>
        )}
        {work && !isMobile && <QueueBadge>{work}</QueueBadge>}
        {!isMobile && <span style={{ flex: 1 }} />}
        {!catalogReady && !isMobile && <span style={{ fontSize: FS.xs, color: C.textMuted }}>Загружаем…</span>}
        {!isMobile && (
          <IconButton size="sm" title="Свернуть полосу в строку" ariaLabel="Свернуть полосу в строку" onClick={() => setCollapsed(true)}>
            {ic(ChevronUp, ICON_SIZE.sm)}
          </IconButton>
        )}
      </div>
      {menu}
    </>
  );
}
