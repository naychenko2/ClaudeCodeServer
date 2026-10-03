// Полоса «Видео» над композером (реестр composer-strip; макет v7, «Полоса «Видео»»): две сводки-чипа —
// «Сцена» и «Фильм»; настроек в себе не раскрывает — это вход в панель «Видео» на нужной вкладке.
// Заголовок-переключатель — от хоста. ▾ у чипа сцены — меню сцен чата (GenerationPickMenu), там же «Снять
// выбор». Вид телефона (короткие чипы, тач-цели 44 px) включает ширина самой полосы, а не только окна:
// «Файлы» и соседние панели сужают колонку и на широком экране. Высота 48 px, свёрнутая строка — 30 px.

import { useRef, useState, useSyncExternalStore, type ReactNode } from 'react';
import { ChevronDown, ChevronRight, ChevronUp, Clapperboard, Cpu, Film, Plus, TriangleAlert, X } from 'lucide-react';
import {
  Button, C, Dot, FS, GenerationPickMenu, IconButton, ICON_SIZE, ICON_STROKE, R, ReleaseNotice, SP, gitRelTime, pickRows,
  type GenerationPickExtra, type GenerationPickRow,
} from 'aihome_shell/kit';
import type { ComposerStripCtx } from '../../../lib/subsystems/registryCore';
import { createScene, currentResolved, openFilmPanel, openScenePanel, releaseFocus, selectSceneByHuman, undoRelease, videoReleaseUndo } from '../scene/actions';
import { isPersonalScope, videoScope } from '../scope';
import {
  filmName, getCatalog, getFocusedFilmPath, getFocusedScene, getJobsOf, getPriceHint, useFilm, useVideoStoreVersion, useVideoThreads,
} from '../store/videoStore';
import { useSceneQuote } from '../panel/useScene';
import { TOUCH, useBoxWidth } from '../useBoxWidth';
import { ic } from '../panel/primitives';
import { filmChip, sceneChip, staleFilm } from './summary';
import type { VideoScene } from '../api';


// Сводка в меню переключателя полос — та же, что в чипе
export function videoStripStatus(projectId: string | null, sessionId: string | null): string {
  const scope = videoScope(projectId);
  const scene = getFocusedScene(sessionId);
  const r = currentResolved(sessionId, scope, scene);
  return sceneChip(scene, r, getPriceHint(sessionId, scene?.sceneId ?? null)).text;
}

// Полоса уже этой ширины (CSS px) рисуется как на телефоне: короткие чипы
const NARROW_W = 580;
// Уже этой ширины модель съёмки прячем целиком вместе с «·»: от неё остаётся висячая точка, а номер сцены режется
const MODEL_W = 800;
const CHIP_H = 28;
// Высота полосы и свёрнутой строки на десктопе (на телефоне — тач-цель плюс рамка)
const FULL_H = 48;
// Колонка уже этой ширины (открыты «Файлы»): у чипа фильма прячем время, чтобы цена сцены осталась целой
const TINY_W = 340;
const MINI_H = 30;

// Значок чипа не сжимается до точки, когда имя не влезает
const icFixed = (I: typeof Film, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />;

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
  useSceneQuote(scope, sessionId, scene, getCatalog(scope), r);
  const chip = sceneChip(scene, r, getPriceHint(sessionId, scene?.sceneId ?? null));
  const filmPath = personal ? null : getFocusedFilmPath(sessionId);
  const film = useFilm(scope, filmPath ? sessionId : null, filmPath);
  const fchip = filmPath ? filmChip(filmName(filmPath), film.state) : null;
  const filmStale = staleFilm(film.state);
  const work = workText(sessionId, scene, film.state?.build);
  const [menuAt, setMenuAt] = useState<DOMRect | null>(null);
  const bar = useRef<HTMLDivElement>(null);
  const host = useRef<HTMLDivElement>(null);
  const hostW = useBoxWidth(host);
  // Узкая колонка: телефон или фактическая ширина меньше порога (до замера — по окну)
  const narrow = isMobile || (hostW > 0 && hostW < NARROW_W);
  const tiny = hostW > 0 && hostW < TINY_W;
  const showModel = hostW === 0 || hostW >= MODEL_W;
  const h = isMobile ? TOUCH : CHIP_H;
  const offer = useSyncExternalStore(videoReleaseUndo.subscribe, videoReleaseUndo.current, videoReleaseUndo.current);
  const undo = offer && offer.snapshot.sessionId === sessionId ? offer : null;
  const catalogReady = !!getCatalog(scope);
  const open = () => openScenePanel(sessionId);
  const release = () => { if (sessionId) void releaseFocus(scope, sessionId, scene); };
  const title = switcher ?? (
    <span title="Видео" style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
      {ic(Clapperboard)}{!narrow && 'Видео'}
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
  const extras: GenerationPickExtra[] = [
    { key: 'new', label: 'Новая сцена', icon: ic(Plus), onClick: () => { setMenuAt(null); if (sessionId) void createScene(scope, sessionId); } },
    ...(scene ? [{ key: 'release', label: 'Снять выбор', icon: ic(X), onClick: () => { setMenuAt(null); release(); } }] : []),
  ];
  const menu = menuAt && (
    <GenerationPickMenu title="Сцены чата" subtitle="Выбрать сцену для панели «Видео»" rows={rows} extras={extras} isMobile={isMobile}
      emptyText="В этом чате других сцен пока нет" onClose={() => setMenuAt(null)} anchor={menuAt}
      onPick={id => { setMenuAt(null); if (sessionId) void selectSceneByHuman(scope, sessionId, id); }} />
  );

  // Кнопка-сводка: прозрачная, без рамки — строка свёрнутой полосы
  const summaryBtn = (props: { dataKey: string; title: string; onClick: () => void; children: ReactNode; grow?: boolean }) => (
    <Button size="xs" variant="ghost" title={props.title} onClick={e => { e.stopPropagation(); props.onClick(); }}
      style={{ minWidth: 0, height: h, border: 'none', flex: props.grow ? '1 1 0' : '0 0 auto', justifyContent: 'flex-start', color: C.textSecondary }}>
      <span data-video-summary={props.dataKey} style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 400 }}>{props.children}</span>
    </Button>
  );

  const frame = (node: ReactNode) => <div ref={host} style={{ minWidth: 0 }}>{node}</div>;

  if (collapsed) {
    const expand = () => setCollapsed(false);
    return frame(
      <div data-composer-strip="video" data-video-strip="mini" title="Развернуть полосу «Видео»" onClick={expand}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.xs, height: isMobile ? TOUCH : MINI_H, margin: isMobile ? `${SP.sm}px 0` : `${SP.xs}px 0 ${SP.sm}px`,
          padding: isMobile ? `0 0 0 ${SP.xs}px` : `0 ${SP.sm}px 0 ${SP.xs}px`, boxSizing: 'border-box', minWidth: 0, cursor: 'pointer',
          background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.lg,
        }}>
        {title}
        {summaryBtn({ dataKey: 'scene', title: 'Открыть сцену в панели «Видео»', onClick: open, grow: true, children: narrow ? chip.short : chip.text })}
        {fchip && summaryBtn({
          dataKey: 'film', title: 'Открыть фильм в панели «Видео»', onClick: () => openFilmPanel(sessionId),
          children: <>{ic(Film)} {narrow ? fchip.short : `${filmName(filmPath!)} · ${fchip.short}`}</>,
        })}
        <IconButton size={isMobile ? 'lg' : 'xs'} title="Развернуть полосу «Видео»" ariaLabel="Развернуть полосу «Видео»" onClick={e => { e.stopPropagation(); expand(); }}
          style={isMobile ? { width: TOUCH, height: TOUCH } : undefined}>{ic(ChevronDown, ICON_SIZE.sm)}</IconButton>
      </div>,
    );
  }

  const sceneLabel = narrow
    ? (
      <>
        <span style={{ minWidth: 0, flex: '0 0 auto', whiteSpace: 'nowrap' }}>{chip.shortName}</span>
        {chip.shortPrice && <span data-video-chip-price="" style={{ whiteSpace: 'nowrap', flexShrink: 0 }}>· {chip.shortPrice}</span>}
      </>
    )
    : (
      <>
        <span style={{ whiteSpace: 'nowrap', flex: '0 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{chip.name}</span>
        {showModel && <span data-video-chip-model="" style={{ flex: '0 1000 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>· {chip.model}</span>}
        {chip.meta && <span data-video-chip-meta="" style={{ whiteSpace: 'nowrap', flexShrink: 0 }}>· {chip.meta}</span>}
        {chip.price && <span data-video-chip-price="" style={{ whiteSpace: 'nowrap', flexShrink: 0 }}>· {chip.price}</span>}
      </>
    );
  const filmLabel = !fchip || tiny ? null : isMobile
    ? <span style={{ whiteSpace: 'nowrap' }}>{fchip.short}</span>
    : (
      <>
        <span style={{ whiteSpace: 'nowrap', flex: '0 1 auto', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{filmName(filmPath!)}</span>
        {fchip.meta && <span style={{ whiteSpace: 'nowrap', flexShrink: 0 }}>· {narrow ? fchip.short : fchip.meta}</span>}
      </>
    );

  return frame(
    <>
      {undo && (
        <div style={{ position: 'relative', height: 0 }}>
          <div data-video-release="" style={{ position: 'absolute', left: 0, right: 0, bottom: SP.xs }}>
            <ReleaseNotice text={undo.text} onUndo={() => { void undoRelease(); }} isMobile={isMobile} />
          </div>
        </div>
      )}
      {work && narrow && <div style={{ marginTop: SP.xs }}><QueueBadge>{work}</QueueBadge></div>}
      <div ref={bar} data-composer-strip="video" data-video-strip="full" style={{
        position: 'relative', display: 'flex', alignItems: 'center', gap: narrow ? SP.xxs : SP.sm, boxSizing: 'border-box', minWidth: 0,
        height: isMobile ? TOUCH + SP.xs : FULL_H, margin: isMobile ? `${SP.sm}px 0` : `${SP.md}px 0 ${SP.sm}px`,
        padding: narrow ? `0 ${SP.xxs}px` : `0 ${SP.sm}px`,
        background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xxl,
      }}>
        {title}
        <span data-video-chip="scene" style={{ display: 'inline-flex', alignItems: 'center', minWidth: 0, flex: '0 1 auto', gap: SP.xxs }}>
          <Button size="xs" variant="secondary" title="Открыть сцену в панели «Видео»" onClick={open}
            style={{ minWidth: 0, flex: '0 1 auto', height: h, overflow: 'hidden', border: `1px solid ${scene ? C.accent : C.border}`, background: C.bgWhite }}>
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
              {icFixed(Clapperboard)}
              {sceneLabel}
              {chip.warn && scene && <span title="Снять пока нельзя: нет кадра или текста" style={{ color: C.warningText, display: 'inline-flex', flexShrink: 0 }}>{ic(TriangleAlert)}</span>}
              {icFixed(narrow ? ChevronUp : ChevronRight)}
            </span>
          </Button>
          {state.scenes.length > 0 && (
            <IconButton size={isMobile ? 'lg' : 'xs'} title="Сцены чата" ariaLabel="Сцены чата" onClick={e => setMenuAt((e.currentTarget as HTMLElement).getBoundingClientRect())}
              style={isMobile ? { width: TOUCH, height: TOUCH } : undefined}>{ic(ChevronDown)}</IconButton>
          )}
        </span>
        {fchip && (
          <span data-video-chip="film" style={{ display: 'inline-flex', minWidth: 0, flex: '0 1 auto', position: 'relative' }}>
            <Button size="xs" variant="secondary" title={filmStale ? 'Фильм изменён после сборки — пересоберите' : 'Открыть фильм в панели «Видео»'}
              onClick={() => openFilmPanel(sessionId)}
              style={{ minWidth: 0, flex: '0 1 auto', height: h, overflow: 'hidden', border: `1px solid ${C.border}`, background: C.bgWhite }}>
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                {icFixed(Film)}
                {filmLabel}
                {filmStale && <span data-video-film-dot="" aria-label="Фильм изменён после сборки" style={{ display: 'inline-flex', flexShrink: 0 }}><Dot color={C.info} /></span>}
                {icFixed(narrow ? ChevronUp : ChevronRight)}
              </span>
            </Button>
          </span>
        )}
        {work && !narrow && <QueueBadge>{work}</QueueBadge>}
        {!narrow && <span style={{ flex: 1 }} />}
        {!catalogReady && !narrow && <span style={{ fontSize: FS.xs, color: C.textMuted }}>Загружаем…</span>}
        {!narrow && (
          <IconButton size="sm" title="Свернуть полосу в строку" ariaLabel="Свернуть полосу в строку" onClick={() => setCollapsed(true)}>
            {ic(ChevronUp, ICON_SIZE.sm)}
          </IconButton>
        )}
      </div>
      {menu}
    </>,
  );
}
