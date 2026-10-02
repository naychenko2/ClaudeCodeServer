// Панель «Видео» рабочей области (ADR-022, макет v7) на общем каркасе GenerationPanel. Две вкладки с
// явным tab при любом показе: «Сцена» (✦ Снять) и «Фильм» (✦ Собрать). Живёт и в проекте, и в правой
// колонке личного чата (projectId = null → область personal, фильмов нет).

import { useEffect, useState } from 'react';
import { Clapperboard, Film, X } from 'lucide-react';
import {
  GenerationPanel, IconButton, C, REVEAL_PANEL_EVENT, ICON_SIZE, followPeeked, returnLabel, returnToOrigin,
  useAgentPick, usePanelReturnTo, consumePreset, usePendingPreset,
  type RevealPanelDetail,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { FilmTab, useFilmPanel } from '../film/FilmTab';
import { flushSettings, releaseFocus, wireFrameBinding } from '../scene/actions';
import { isPersonalScope, videoScope } from '../scope';
import { focusFilm, focusScene, getThreadsState, VIDEO_PANEL } from '../store/videoStore';
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
  const filmPanel = useFilmPanel(ctx.projectId, sessionId);
  const [tab, setTab] = useState<Tab>(() => takeWanted()?.tab ?? 'scene');
  // Опущенная шторка держит низ с ценой и запуском на любой вкладке
  const [peeked, setPeeked] = useState(() => followPeeked(VIDEO_PANEL));
  const agentPick = useAgentPick(sessionId, VIDEO_PANEL);
  const returnTo = usePanelReturnTo(VIDEO_PANEL);
  // Заготовки у «Видео» нет — снимаем всё, что пришло, чтобы не копилось
  const preset = usePendingPreset(VIDEO_PANEL);
  useEffect(() => { if (preset) consumePreset(VIDEO_PANEL); }, [preset]);

  // Запрос показа: вкладка и цель («к сцене», «к фильму»)
  useEffect(() => {
    const on = () => {
      const w = takeWanted();
      if (!w) return;
      setTab(w.tab);
      if (!sessionId) return;
      if (w.tab === 'scene' && w.target && !w.target.includes(':') && getThreadsState(sessionId).scenes.some(s => s.sceneId === w.target)
        && getThreadsState(sessionId).focus.sceneId !== w.target) void focusScene(scope, sessionId, w.target);
      if (w.tab === 'film' && w.target?.endsWith('.film') && getThreadsState(sessionId).focus.filmPath !== w.target) void focusFilm(scope, sessionId, w.target);
    };
    subs.add(on);
    on();
    return () => { subs.delete(on); };
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
        Работаем со: <b style={{ color: C.textHeading }}>{scene.name}</b>
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
        ? <IconButton size="xs" title="Снять выбор — новая сцена" ariaLabel="Снять выбор — новая сцена" onClick={release}>{ic(X)}</IconButton>
        : tab === 'film' ? filmPanel.contextAction : undefined}
      panelKey={VIDEO_PANEL}
      returnLink={returnTo ? { label: returnLabel(returnTo), onClick: () => returnToOrigin(VIDEO_PANEL, returnTo, sessionId ?? undefined) } : undefined}
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
