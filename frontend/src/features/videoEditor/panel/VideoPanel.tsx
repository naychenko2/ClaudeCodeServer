// Панель «Видео» рабочей области (ADR-022, макет v7) на общем каркасе GenerationPanel. Две вкладки с
// явным tab при любом показе: «Сцена» (✦ Снять) и «Фильм» (✦ Собрать). Живёт и в проекте, и в правой
// колонке личного чата (projectId = null → область personal, фильмов нет).

import { useEffect, useRef, useState } from 'react';
import { Clapperboard, Film, X } from 'lucide-react';
import {
  GenerationPanel, IconButton, C, REVEAL_PANEL_EVENT, ICON_SIZE, followPeeked, returnLabel, returnToOrigin,
  useAgentPick, usePanelReturnTo, consumePreset, usePendingPreset,
  type RevealPanelDetail,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { FilmTab, useFilmPanel } from '../film/FilmTab';
import { backToVideoStrip, flushSettings, releaseFocus, wireFrameBinding } from '../scene/actions';
import { isPersonalScope, videoScope } from '../scope';
import { TOUCH } from '../useBoxWidth';
import { ensureVideoThreads, focusFilm, focusScene, getThreadsState, VIDEO_PANEL } from '../store/videoStore';
import { ic } from './primitives';
import { SceneTab } from './SceneTab';
import { useScene } from './useScene';

export type Tab = 'scene' | 'film';
const isTab = (t: unknown): t is Tab => t === 'scene' || t === 'film';

// Вкладку из revealWorkspacePanel(videoEditor, tab) ловим на уровне модуля: закрытая панель
// монтируется уже ПОСЛЕ события, и её собственный слушатель его бы пропустил
interface Wanted { tab: Tab; target?: string; sessionId?: string }
let wanted: Wanted | null = null;
const subs = new Set<() => void>();
const takeWanted = () => { const w = wanted; wanted = null; return w; };
if (typeof window !== 'undefined') {
  window.addEventListener(REVEAL_PANEL_EVENT, e => {
    const d = (e as CustomEvent<Partial<RevealPanelDetail>>).detail;
    if (d?.key !== VIDEO_PANEL || !isTab(d.tab)) return;
    wanted = { tab: d.tab, ...(d.target ? { target: d.target } : {}), ...(d.sessionId ? { sessionId: d.sessionId } : {}) };
    subs.forEach(fn => fn());
  });
}

export function VideoPanel({ ctx }: { ctx: WorkspacePanelDefCtx }) {
  const { sessionId } = ctx;
  const scope = videoScope(ctx.projectId);
  const personal = isPersonalScope(scope);
  wireFrameBinding();
  const m = useScene(ctx.projectId, sessionId);
  const filmPanel = useFilmPanel(ctx.projectId, sessionId, ctx.isMobile);
  // Первый запрос показа (панель смонтирована им же): вкладка — сразу, цель — эффектом ниже
  const [first] = useState<Wanted | null>(() => takeWanted());
  const [tab, setTab] = useState<Tab>(first?.tab ?? 'scene');
  // Опущенная шторка держит низ с ценой и запуском на любой вкладке
  const [peeked, setPeeked] = useState(() => followPeeked(VIDEO_PANEL));
  const agentPick = useAgentPick(sessionId, VIDEO_PANEL, { current: tab, set: t => { if (isTab(t)) setTab(t); } });
  const returnTo = usePanelReturnTo(VIDEO_PANEL);
  // Заготовки у «Видео» нет — снимаем всё, что пришло, чтобы не копилось
  const preset = usePendingPreset(VIDEO_PANEL);
  useEffect(() => { if (preset) consumePreset(VIDEO_PANEL); }, [preset]);

  // Запрос показа: вкладка и цель («к сцене», «к фильму»)
  const firstUsed = useRef(false);
  useEffect(() => {
    const on = () => {
      const w = firstUsed.current ? takeWanted() : (firstUsed.current = true, first ?? takeWanted());
      if (!w) return;
      setTab(w.tab);
      if (!sessionId || !w.target) return;
      const target = w.target;
      void ensureVideoThreads(scope, sessionId).then(() => {
        const st = getThreadsState(sessionId);
        if (w.tab === 'scene' && st.scenes.some(s => s.sceneId === target) && st.focus.sceneId !== target) void focusScene(scope, sessionId, target);
        if (w.tab === 'film' && target.endsWith('.film') && st.focus.filmPath !== target) void focusFilm(scope, sessionId, target);
      });
    };
    subs.add(on);
    on();
    return () => { subs.delete(on); };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- first читается один раз
  }, [scope, sessionId]);

  // Недосохранённая правка уходит в свою сцену при закрытии панели и смене чата
  useEffect(() => () => { if (sessionId) void flushSettings(scope, sessionId); }, [scope, sessionId]);

  const { scene, r } = m;
  const sceneIdx = scene && sessionId ? getThreadsState(sessionId).scenes.findIndex(s => s.sceneId === scene.sceneId) : -1;
  const prevScene = sessionId ? getThreadsState(sessionId).scenes[(sceneIdx < 0 ? getThreadsState(sessionId).scenes.length : sceneIdx) - 1] ?? null : null;

  const title = 'Видео';
  const subtitle = tab === 'film'
    ? filmPanel.subtitle
    : [r.provider?.label, r.model?.label].filter(Boolean).join(' · ') || undefined;

  let context;
  if (tab === 'film') context = filmPanel.context;
  else if (scene) {
    context = (
      <span data-video-context="" style={{ flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
        Работаем с: <b style={{ color: C.textHeading }}>{scene.name}</b>
        {scene.filmRef ? ` · в фильме ${filmPanel.nameOf(scene.filmRef.path)}` : ' · не в фильме'}
      </span>
    );
  } else context = <span>Новая сцена · результат ляжет в ленту новой карточкой</span>;

  const release = () => { if (sessionId) void releaseFocus(scope, sessionId, scene); };
  const body = tab === 'scene'
    ? <SceneTab m={m} isMobile={ctx.isMobile} prevScene={prevScene} />
    : <FilmTab ctx={ctx} />;
  const foot = tab === 'scene' ? m.foot : filmPanel.foot;

  return (
    <GenerationPanel<Tab>
      title={title}
      subtitle={subtitle}
      icon={ic(Clapperboard, ICON_SIZE.sm)}
      tabs={[
        { value: 'scene', label: 'Сцена', icon: ic(Clapperboard) },
        { value: 'film', label: 'Фильм', icon: ic(Film), ...(filmPanel.count ? { count: filmPanel.count } : {}) },
      ]}
      tab={tab}
      onTabChange={setTab}
      context={context}
      contextAction={tab === 'scene' && scene
        ? <IconButton size={ctx.isMobile ? 'lg' : 'xs'} title="Снять выбор — новая сцена" ariaLabel="Снять выбор — новая сцена" onClick={release}
          style={ctx.isMobile ? { width: TOUCH, height: TOUCH } : undefined}>{ic(X)}</IconButton>
        : tab === 'film' ? filmPanel.contextAction : undefined}
      panelKey={VIDEO_PANEL}
      returnLink={returnTo ? { label: returnLabel(returnTo), onClick: () => { returnToOrigin(VIDEO_PANEL, returnTo, sessionId ?? undefined); backToVideoStrip(sessionId); } } : undefined}
      agentPick={agentPick}
      draftKey={tab === 'scene' ? m.draftKey : null}
      foot={foot}
      peeked={peeked}
      onPeekedChange={setPeeked}
      peekSummary={tab === 'scene'
        ? [scene?.name ?? 'Новая сцена', r.model?.label ?? 'Авто', `${r.durationSec} с`].join(' · ')
        : filmPanel.peekSummary}
      onClose={ctx.onClose}
      layout={ctx.isMobile ? 'sheet' : 'column'}
    >
      {personal && tab === 'film' ? <FilmTab ctx={ctx} /> : body}
    </GenerationPanel>
  );
}
