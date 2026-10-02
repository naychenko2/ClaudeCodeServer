// Карточки «Видео» в ленте (макет v7, «Связь с лентой»): в истории лежат только якоря module_record,
// содержимое — живьём из стора сцен. video_scene — карточка сцены (плеер, версии ‹ ›, «Сохранить
// сцену» / «Скачать», «В фильм →», строка «В фильме …»); video_launch_versions — ход запуска и его
// варианты; video_saved, video_film_built, video_note — тихие строки. Клик по карточке, а не по её
// кнопке, — выбор человеком: панель следует за ним, только если открыта. Без флага — строка fallback.

import { useState, type ReactNode } from 'react';
import { ChevronLeft, ChevronRight, Clapperboard, Download, Film, Save, Sparkles } from 'lucide-react';
import {
  Badge, Button, C, FLAGS, FS, IconButton, ProgressBar, R, SHADOW, SP, ICON_SIZE, ICON_STROKE, isCardPick, showToast, useFeature,
} from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { videoApi, type VideoClipVersion, type VideoScene } from '../api';
import { openFilmPanel, openScenePanel, saveScene, selectFilmByHuman, selectSceneByHuman, takeVersion, downloadClip } from '../scene/actions';
import { currentVersion, staleNotes } from '../scene/model';
import { isPersonalScope, videoScope } from '../scope';
import { filmName, getFocusedFilmPath, getJobsOf, patchFilm, getFilm, loadFilm, useVideoThreads, useVideoStoreVersion } from '../store/videoStore';
import { snapshotOf } from '../film/model';
import { progressLabel } from '../panel/useScene';
import { recordOf, str } from './records';

const ic = (I: typeof Film, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

function Line({ children, onClick }: { children: ReactNode; onClick?: () => void }) {
  return (
    <div data-video-line="" onClick={onClick} style={{
      display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45,
      cursor: onClick ? 'pointer' : undefined, overflowWrap: 'anywhere',
    }}>{children}</div>
  );
}

function Fallback({ text }: { text: string | null }) {
  return text ? <Line>{text}</Line> : null;
}

// «В фильм →»: несохранённая сцена сперва сохраняется (сервер ставит её в открытый фильм сам), иначе
// файл добавляется в открытый фильм патчем; фильма нет — вкладка «Фильм» с выбором
export async function addToFilm(scope: string, sessionId: string, scene: VideoScene, versionId: string | undefined): Promise<void> {
  const path = getFocusedFilmPath(sessionId);
  let file = scene.savedFiles.find(x => x.versionId === versionId)?.path ?? null;
  if (!file) {
    const res = await saveScene(scope, sessionId, scene, versionId);
    if (!res) return;
    if (res.addedToFilm) { openFilmPanel(sessionId); return; }
    file = res.path;
  }
  if (!path) {
    showToast('Выберите или заведите фильм — сцена встанет в него', '', 'info');
    openFilmPanel(sessionId);
    return;
  }
  if (!getFilm(sessionId, path).state) await loadFilm(scope, sessionId, path);
  if (await patchFilm(scope, sessionId, path, [{ op: 'add', file, scene: snapshotOf(scene) }])) openFilmPanel(sessionId, path);
}

function Player({ scope, sessionId, scene, v }: { scope: string; sessionId: string; scene: VideoScene; v: VideoClipVersion }) {
  return (
    <video data-video-player="" controls preload="metadata" playsInline
      src={videoApi.versionFileUrl(scope, sessionId, scene.sceneId, v.versionId)}
      style={{ width: '100%', maxHeight: 280, borderRadius: R.md, background: C.bgInset, display: 'block' }} />
  );
}

export function SceneCard({ ctx, sceneId, jobId }: { ctx: ChatItemToolCtx; sceneId: string; jobId?: string | null }) {
  const scope = videoScope(ctx.projectId);
  const personal = isPersonalScope(scope);
  const sessionId = ctx.sessionId;
  const state = useVideoThreads(scope, sessionId);
  useVideoStoreVersion();
  const scene = state.scenes.find(s => s.sceneId === sceneId) ?? null;
  const [pos, setPos] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  if (!scene || !sessionId) return null;
  const versions = jobId ? scene.versions.filter(v => v.jobId === jobId) : scene.versions;
  const cur = currentVersion(scene);
  const idx = pos ?? Math.max(0, versions.findIndex(v => v.versionId === cur?.versionId));
  const v = versions[Math.min(idx, versions.length - 1)] ?? null;
  const focused = state.focus.sceneId === scene.sceneId;
  const launch = jobId ? scene.launches.find(l => l.jobId === jobId) : scene.launches[scene.launches.length - 1];
  const jobs = getJobsOf(sessionId, scene.sceneId).filter(j => !jobId || j.jobId === jobId);
  const running = launch?.status === 'running' || jobs.length > 0;
  const agentWorking = running && launch?.initiator === 'agent';
  const saved = v ? scene.savedFiles.find(x => x.versionId === v.versionId) : null;
  const stale = staleNotes(scene);
  const pick = () => { void selectSceneByHuman(scope, sessionId, scene.sceneId); };
  const run = async (fn: () => Promise<unknown>) => { setBusy(true); try { await fn(); } finally { setBusy(false); } };

  return (
    <div data-video-card={jobId ? 'launch' : 'scene'} data-scene={scene.sceneId} data-current={focused ? 'true' : 'false'}
      onClick={e => { if (isCardPick(e.target, e.currentTarget)) pick(); }}
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md, width: '100%', maxWidth: 560, boxSizing: 'border-box',
        border: `1px solid ${focused ? C.accent : C.border}`, borderRadius: R.xl, background: C.bgCard, minWidth: 0, cursor: 'pointer',
        boxShadow: focused ? `${SHADOW.card}, 0 0 0 3px ${C.accentLight}` : SHADOW.card,
      }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
        <span style={{ display: 'inline-flex', color: C.accent }}>{ic(Clapperboard, ICON_SIZE.sm)}</span>
        <b style={{ fontSize: FS.base, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{scene.name}</b>
        {v && <span style={{ fontSize: FS.xs, color: C.textMuted }}>· версия {v.number} · {v.model} · {v.durationSec} с</span>}
        <span style={{ flex: 1 }} />
        {agentWorking && <Badge size="xs" tone="info" icon={ic(Sparkles)}>Claude работает</Badge>}
        {focused && <Badge size="xs" tone="neutral">в работе</Badge>}
      </div>
      {running && (
        <div data-video-card-progress="" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{progressLabel(scene, jobs, launch?.count ?? 1).label}</span>
          {progressLabel(scene, jobs, launch?.count ?? 1).p !== undefined && <ProgressBar value={progressLabel(scene, jobs, launch?.count ?? 1).p!} />}
        </div>
      )}
      {!running && launch && (launch.status === 'failed' || launch.status === 'interrupted') && (
        <Line>{launch.interrupted ? 'Запуск оборвал перезапуск сервера — готовые варианты сохранены' : `Не получилось: ${launch.error ?? 'причина не пришла'}`}</Line>
      )}
      {v ? (
        <>
          <Player scope={scope} sessionId={sessionId} scene={scene} v={v} />
          {versions.length > 1 && (
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
              <IconButton size="xs" title="Прошлый вариант" ariaLabel="Прошлый вариант" disabled={idx <= 0} onClick={() => setPos(Math.max(0, idx - 1))}>{ic(ChevronLeft)}</IconButton>
              <span style={{ fontSize: FS.xs, color: C.textMuted }}>{idx + 1} из {versions.length}</span>
              <IconButton size="xs" title="Следующий вариант" ariaLabel="Следующий вариант" disabled={idx >= versions.length - 1} onClick={() => setPos(Math.min(versions.length - 1, idx + 1))}>{ic(ChevronRight)}</IconButton>
              {cur?.versionId !== v.versionId && (
                <Button size="xs" variant="ghost" onClick={() => { void takeVersion(scope, sessionId, scene.sceneId, v.versionId); }}>Оставить этот</Button>
              )}
            </div>
          )}
        </>
      ) : !running && <Line>{scene.settings.text ? `«${scene.settings.text.slice(0, 140)}»` : 'Сцена без клипа — кадры и текст в панели «Видео»'}</Line>}
      {stale.map(t => <div key={t} style={{ fontSize: FS.sm, color: C.warningText }}>{t}</div>)}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
        {v && (personal
          ? <Button size="sm" variant="secondary" leftIcon={ic(Download)} onClick={() => downloadClip(scope, sessionId, scene, v.versionId)} title="В личном чате клип живёт только в этом чате — скачайте файл">Скачать</Button>
          : saved
            ? <Badge tone="success">В проекте: {saved.path.split('/').pop()}</Badge>
            : <Button size="sm" variant="secondary" leftIcon={ic(Save)} loading={busy} onClick={() => { void run(() => saveScene(scope, sessionId, scene, v.versionId)); }}>Сохранить сцену</Button>)}
        {v && !personal && !scene.filmRef && (
          <Button size="sm" variant="ghost" loading={busy} onClick={() => { void run(() => addToFilm(scope, sessionId, scene, v.versionId)); }}>В фильм →</Button>
        )}
        <Button size="sm" variant="ghost" onClick={() => { void selectSceneByHuman(scope, sessionId, scene.sceneId).then(() => openScenePanel(sessionId)); }}>
          {v ? 'Переснять' : 'Открыть в панели'}
        </Button>
      </div>
      {scene.filmRef && !personal && (
        <Line onClick={() => { void selectFilmByHuman(scope, sessionId, scene.filmRef!.path).then(() => openFilmPanel(sessionId, scene.filmRef!.path)); }}>
          {ic(Film)} В фильме «{filmName(scene.filmRef.path)}» · место {scene.filmRef.position + 1} · <span style={{ color: C.accent }}>Открыть «Фильм» →</span>
        </Line>
      )}
    </div>
  );
}

export function SceneAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  const rec = recordOf(ctx.item);
  const sceneId = str(rec?.data.sceneId);
  if (!on || !sceneId) return <Fallback text={rec?.fallback ?? null} />;
  return <SceneCard ctx={ctx} sceneId={sceneId} />;
}

export function LaunchAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  const rec = recordOf(ctx.item);
  const sceneId = str(rec?.data.sceneId);
  const jobId = str(rec?.data.jobId);
  if (!on || !sceneId || !jobId) return <Fallback text={rec?.fallback ?? null} />;
  return <SceneCard ctx={ctx} sceneId={sceneId} jobId={jobId} />;
}

// Тихие строки: «Сцена сохранена», «Claude собрал фильм: …film.mp4» (клик — вкладка «Фильм»), заметки
export function QuietLine({ ctx }: { ctx: ChatItemToolCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  const rec = recordOf(ctx.item);
  if (!rec) return null;
  const scope = videoScope(ctx.projectId);
  const sessionId = ctx.sessionId;
  const p = str(rec.data.path);
  const filmPath = str(rec.data.filmPath) ?? (p?.endsWith('.film') ? p : null);
  const sceneId = str(rec.data.sceneId);
  const text = rec.fallback ?? str(rec.data.text) ?? '';
  if (!on || !sessionId) return <Fallback text={text} />;
  const onClick = filmPath && !isPersonalScope(scope)
    ? () => { void selectFilmByHuman(scope, sessionId, filmPath); }
    : sceneId ? () => { void selectSceneByHuman(scope, sessionId, sceneId); } : undefined;
  return (
    <Line onClick={onClick}>
      {ic(rec.recordType === 'video_film_built' ? Film : Clapperboard)}
      <span data-video-quiet={rec.recordType}>{text}</span>
    </Line>
  );
}
