// Карточки «Видео» в ленте (макет v7, «Связь с лентой»; дополнение плана 2026-10-02). В истории лежат
// только якоря module_record, содержимое — живьём из стора сцен (REST + события). video_scene — карточка
// сцены (плеер, версии ‹ ›, «Сохранить сцену» / «Скачать», «В фильм →», строка «В фильме …»);
// video_launch_versions — та же карточка запуска: ход, цена, варианты; video_saved, video_film_built,
// video_note — тихие строки. Действие агента рисуется ТОЙ ЖЕ разметкой, что действие человека: карточку
// строит только запись ленты и стор, а не текст ответа инструмента; отличие — одна метка «✦ Claude».
// Клик по карточке, а не по её кнопке, — выбор человеком: панель следует за ним, только если открыта.

import { useState, type KeyboardEvent, type ReactNode } from 'react';
import { ChevronLeft, ChevronRight, Clapperboard, Download, Film, Save, Target, Zap } from 'lucide-react';
import {
  Badge, Button, ByClaude, C, FLAGS, FS, IconButton, ProgressBar, R, SHADOW, SP, ICON_SIZE, TextField, isCardPick, useChatContext, useFeature,
} from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { videoApi, type VideoCatalog, type VideoClipVersion, type VideoLaunch, type VideoScene } from '../api';
import { filmFolder, filmPathOf, newFilmPath, saveTargetFor, snapshotOf, type SaveTarget } from '../film/model';
import { progressLabel } from '../editor/useScene';
import { downloadClip, openFilmPanel, openScenePanel, saveScene, selectFilmByHuman, selectSceneByHuman, takeVersion } from '../scene/actions';
import { currentVersion, modelLabel, plural, staleNotes } from '../scene/model';
import { isPersonalScope, videoScope } from '../scope';
import {
  filmName, getCatalog, getFilm, getFocusedFilmPath, getJobsOf, loadFilm, loadFilmList, patchFilm, useFilmList, useJobTick, useVideoStoreVersion, useVideoThreads, type JobProgress,
} from '../store/videoStore';
import { SCENE_KIND } from '../context/state';
import { ic } from '../editor/primitives';
import { recordOf, str } from './records';


// Потолки карточки в ленте: ширина читаемой колонки и высота плеера
const CARD_MAX_W = 560;
const PLAYER_MAX_H = 280;

// ── Модель карточки: чистая функция от сцены, записи ленты и хода задач ──

// Цена запуска из записи ленты (VideoQuoteResponse.Price): «≈ $3.20», «32 кредита», «бесплатно»
export function priceOfRecord(price: unknown): string | null {
  const p = price as { amount?: number | null; unit?: string; approx?: boolean } | null;
  if (!p || typeof p.unit !== 'string') return null;
  if (p.unit === 'free') return 'бесплатно';
  if (typeof p.amount !== 'number') return null;
  if (p.unit === 'credits') return `${Math.round(p.amount)} ${plural(Math.round(p.amount), 'кредит', 'кредита', 'кредитов')}`;
  return `${p.approx ? '≈ ' : ''}$${p.amount.toFixed(2)}`;
}

// Цена, когда в записи ленты её нет: сумма стоимостей готовых вариантов запуска
export function priceOfVersions(versions: VideoClipVersion[]): string | null {
  const costs = versions.map(v => v.cost).filter((c): c is NonNullable<VideoClipVersion['cost']> => !!c);
  if (!costs.length) return null;
  if (costs.every(c => c.currency === 'local')) return 'бесплатно';
  const usd = costs.filter(c => c.currency === 'usd').reduce((n, c) => n + c.amount, 0);
  if (usd > 0) return `$${usd.toFixed(2)}`;
  const cr = Math.round(costs.filter(c => c.currency === 'credits').reduce((n, c) => n + c.amount, 0));
  return cr > 0 ? `${cr} ${plural(cr, 'кредит', 'кредита', 'кредитов')}` : null;
}

export interface SceneCardView {
  kind: 'scene' | 'launch';
  sceneId: string;
  name: string;
  focused: boolean;
  // «✦ Claude»: запуск или версия от агента — единственное отличие от ручного действия
  byClaude: boolean;
  // Ход съёмки; null — не идёт
  progress: { label: string; p?: number } | null;
  // «Veo 3.1 · 2 вар. · ≈ $3.20» — что запустили и почём
  launchLine: string | null;
  failure: string | null;
  // Итог запуска для компактной строки: «Отменено · деньги не списаны», «Готово: 2 варианта»
  outcome: string | null;
  versions: VideoClipVersion[];
  index: number;
  version: VideoClipVersion | null;
  // Подпись модели версии — из каталога, не сырой id
  versionModel: string | null;
  isCurrent: boolean;
  savedPath: string | null;
  canSave: boolean;
  canDownload: boolean;
  canAddToFilm: boolean;
  stale: string[];
  emptyText: string | null;
  film: { path: string; name: string; position: number } | null;
}

export function sceneCardView(p: {
  scene: VideoScene; jobId?: string | null; record?: Record<string, unknown>; personal: boolean; focused: boolean;
  jobs: JobProgress[]; pos: number | null; catalog?: VideoCatalog | null;
}): SceneCardView {
  const { scene, jobId, record, personal } = p;
  const launch: VideoLaunch | undefined = jobId ? scene.launches.find(l => l.jobId === jobId) : scene.launches[scene.launches.length - 1];
  const versions = jobId ? scene.versions.filter(v => v.jobId === jobId) : scene.versions;
  const cur = currentVersion(scene);
  const index = Math.min(p.pos ?? Math.max(0, versions.findIndex(v => v.versionId === cur?.versionId)), Math.max(0, versions.length - 1));
  const version = versions[index] ?? null;
  const jobs = p.jobs.filter(j => !jobId || j.jobId === jobId);
  const running = launch?.status === 'running' || jobs.length > 0;
  const initiator = str(record?.initiator) ?? launch?.initiator ?? (jobId ? null : version?.initiator) ?? null;
  const model = modelLabel(p.catalog ?? null, str(record?.provider) ?? launch?.provider, str(record?.model) ?? launch?.model);
  const count = typeof record?.count === 'number' ? record.count : launch?.count;
  const price = priceOfRecord(record?.price) ?? priceOfVersions(versions);
  const launchLine = jobId
    ? [model, count ? `${count} вар.` : null, price].filter(Boolean).join(' · ') || null
    : null;
  const saved = version ? scene.savedFiles.find(x => x.versionId === version.versionId) ?? null : null;
  let failure: string | null = null;
  if (!running && launch && (launch.status === 'failed' || launch.status === 'interrupted')) {
    failure = launch.interrupted ? 'Запуск оборвал перезапуск сервера — готовые варианты сохранены' : `Не получилось: ${launch.error ?? 'причина не пришла'}`;
  }
  let outcome: string | null = null;
  if (!running && launch?.status === 'cancelled') {
    outcome = versions.length ? 'Отменено · готовые варианты сохранены' : 'Отменено · деньги не списаны';
  } else if (!running && launch?.status === 'done' && versions.length) {
    outcome = `Готово: ${versions.length} ${plural(versions.length, 'вариант', 'варианта', 'вариантов')} — в карточке сцены`;
  }
  return {
    kind: jobId ? 'launch' : 'scene',
    sceneId: scene.sceneId,
    name: scene.name,
    focused: p.focused,
    byClaude: initiator === 'agent',
    // Ход съёмки показывает только полная карточка сцены: строка запуска не дублирует полосу
    progress: running && !jobId ? progressLabel(scene, jobs, count ?? 1) : null,
    launchLine,
    failure,
    outcome,
    versions,
    index,
    version,
    versionModel: version ? modelLabel(p.catalog ?? null, version.provider, version.model) : null,
    isCurrent: !!version && version.versionId === cur?.versionId,
    savedPath: saved?.path ?? null,
    canSave: !!version && !personal && !saved,
    canDownload: !!version && personal,
    canAddToFilm: !!version && !personal && !scene.filmRef,
    stale: staleNotes(scene),
    emptyText: !version && !running ? (scene.settings.text ? `«${scene.settings.text.slice(0, 140)}»` : 'Сцена без клипа — кадры и текст в редакторе сцены') : null,
    film: scene.filmRef && !personal ? { path: scene.filmRef.path, name: filmName(scene.filmRef.path), position: scene.filmRef.position } : null,
  };
}

// ── Разметка: одна для человека и агента ──

function Line({ children, onClick, by }: { children: ReactNode; onClick?: () => void; by?: boolean }) {
  const key = (e: KeyboardEvent) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onClick?.(); } };
  return (
    <div data-video-line="" onClick={onClick} {...(onClick ? { role: 'button', tabIndex: 0, onKeyDown: key } : {})} style={{
      display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: SP.xs, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45,
      cursor: onClick ? 'pointer' : undefined, overflowWrap: 'anywhere',
    }}>{children}{by && <ByClaude />}</div>
  );
}

export interface SceneCardActions {
  onPick: () => void;
  onPrev: () => void;
  onNext: () => void;
  onTake: () => void;
  onSave: () => void;
  onDownload: () => void;
  onAddToFilm: () => void;
  onReshoot: () => void;
  onWork: () => void;
  onOpenFilm: () => void;
}

export function SceneCardView({ v, src, busy, a, chooser }: { v: SceneCardView; src: string | null; busy: boolean; a: SceneCardActions; chooser?: ReactNode }) {
  return (
    <div data-video-card={v.kind} data-scene={v.sceneId} data-current={v.focused ? 'true' : 'false'}
      onClick={e => { if (isCardPick(e.target, e.currentTarget)) a.onPick(); }}
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md, width: '100%', maxWidth: CARD_MAX_W, boxSizing: 'border-box',
        border: `1px solid ${v.focused ? C.accent : C.border}`, borderRadius: R.xl, background: C.bgCard, minWidth: 0, cursor: 'pointer',
        boxShadow: v.focused ? `${SHADOW.card}, ${SHADOW.selected}` : SHADOW.card,
      }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0, flexWrap: 'wrap' }}>
        <span style={{ display: 'inline-flex', color: C.accent }}>{ic(Clapperboard, ICON_SIZE.sm)}</span>
        <b style={{ fontSize: FS.base, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{v.name}</b>
        {v.version && <span style={{ fontSize: FS.xs, color: C.textMuted }}>· версия {v.version.number} · {v.versionModel ?? v.version.model} · {v.version.durationSec} с</span>}
        <span style={{ flex: 1 }} />
        {v.byClaude && <ByClaude title={v.progress ? 'Claude снимает эту сцену' : 'Сделал Claude'} />}
        {v.focused && <Badge size="xs" tone="accent" icon={ic(Target)}>{v.byClaude ? 'В работе ✦' : 'В работе'}</Badge>}
      </div>
      {v.launchLine && <div data-video-launch-line="" style={{ fontSize: FS.sm, color: C.textSecondary }}>{v.launchLine}</div>}
      {v.progress && (
        <div data-video-card-progress="" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <span style={{ fontSize: FS.sm, color: C.textSecondary }}>{v.progress.label}</span>
          {v.progress.p !== undefined && <ProgressBar value={v.progress.p} />}
        </div>
      )}
      {v.failure && <Line>{v.failure}</Line>}
      {v.version && src && (
        <video data-video-player="" controls preload="metadata" playsInline src={src}
          style={{ width: '100%', maxHeight: PLAYER_MAX_H, borderRadius: R.md, background: C.bgInset, display: 'block' }} />
      )}
      {v.versions.length > 1 && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
          <IconButton size="xs" title="Прошлый вариант" ariaLabel="Прошлый вариант" disabled={v.index <= 0} onClick={a.onPrev}>{ic(ChevronLeft)}</IconButton>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>{v.index + 1} из {v.versions.length}</span>
          <IconButton size="xs" title="Следующий вариант" ariaLabel="Следующий вариант" disabled={v.index >= v.versions.length - 1} onClick={a.onNext}>{ic(ChevronRight)}</IconButton>
          {!v.isCurrent && <Button size="xs" variant="ghost" onClick={a.onTake}>Оставить этот</Button>}
        </div>
      )}
      {v.emptyText && <Line>{v.emptyText}</Line>}
      {v.stale.map(t => <div key={t} style={{ fontSize: FS.sm, color: C.warningText }}>{t}</div>)}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, alignItems: 'center' }}>
        {v.canDownload && (
          <Button size="sm" variant="secondary" leftIcon={ic(Download)} onClick={a.onDownload}
            title="В личном чате клип живёт только в этом чате — скачайте файл">Скачать</Button>
        )}
        {v.savedPath && <Badge tone="success">В проекте: {v.savedPath.split('/').pop()}</Badge>}
        {v.canSave && <Button size="sm" variant="secondary" leftIcon={ic(Save)} loading={busy} onClick={a.onSave}>Сохранить сцену</Button>}
        {v.canAddToFilm && <Button size="sm" variant="ghost" loading={busy} onClick={a.onAddToFilm}>В фильм →</Button>}
        {!v.focused && (
          <Button size="sm" variant="secondary" leftIcon={ic(Target)} onClick={a.onWork}
            title="Сцена станет основной в контексте хода: чипы действий и панель «Контекст»">Работать с этой</Button>
        )}
        {v.version && <Button size="sm" variant="ghost" onClick={a.onReshoot}>Переснять</Button>}
      </div>
      {chooser}
      {v.film && (
        <Line onClick={a.onOpenFilm}>
          {ic(Film)}<span>В фильме «{v.film.name}» · <span style={{ whiteSpace: 'nowrap' }}>место {v.film.position + 1}</span></span>
          <span style={{ color: C.accent, whiteSpace: 'nowrap' }}>Открыть «Фильм» →</span>
        </Line>
      )}
    </div>
  );
}

// Запуск в ленте — компактная строка под карточкой сцены: модель, число вариантов, цена, ход и итог.
// Плеер и варианты живут в ОДНОЙ полной карточке сцены (якорь video_scene), поэтому съёмка не рисует двойника
export function LaunchRowView({ v, a }: { v: SceneCardView; a: Pick<SceneCardActions, 'onPick'> }) {
  // Тихая строка без рамки: карточка в ленте одна — сцена, запуск лишь подписывает, что и почём снимали
  return (
    <div data-video-card="launch" data-scene={v.sceneId} data-current={v.focused ? 'true' : 'false'}
      onClick={e => { if (isCardPick(e.target, e.currentTarget)) a.onPick(); }}
      style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, padding: `0 ${SP.md}px`, width: '100%', maxWidth: CARD_MAX_W, boxSizing: 'border-box', minWidth: 0, cursor: 'pointer' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', minWidth: 0, fontSize: FS.sm, color: C.textMuted }}>
        <span style={{ display: 'inline-flex', color: C.accent }}>{ic(Zap, ICON_SIZE.sm)}</span>
        <span style={{ color: C.textSecondary }}>{v.name}</span>
        {v.launchLine && <span data-video-launch-line="">{v.launchLine}</span>}
        <span style={{ flex: 1 }} />
        {v.byClaude && <ByClaude title="Сделал Claude" />}
      </div>
      {v.failure && <Line>{v.failure}</Line>}
      {v.outcome && <Line><span data-video-launch-outcome="">{v.outcome}</span></Line>}
    </div>
  );
}

// «В фильм →»: фильм известен заранее (открытый фильм или выбор человека). Несохранённая сцена
// сохраняется в него — сервер сам ставит клип в фильм; уже сохранённая добавляется в фильм патчем
export async function addToFilm(scope: string, sessionId: string, scene: VideoScene, versionId: string | undefined, to: SaveTarget): Promise<void> {
  const target = 'filmPath' in to ? to.filmPath : filmPathOf(to.folder);
  let file = scene.savedFiles.find(x => x.versionId === versionId)?.path ?? null;
  if (!file) {
    const res = await saveScene(scope, sessionId, scene, versionId, to);
    if (!res) return;
    if (res.addedToFilm) { void loadFilmList(scope, sessionId, true); openFilmPanel(sessionId, target); return; }
    file = res.path;
  }
  if (!getFilm(sessionId, target).state) await loadFilm(scope, sessionId, target);
  if (await patchFilm(scope, sessionId, target, [{ op: 'add', file, scene: snapshotOf(scene) }])) openFilmPanel(sessionId, target);
}

// «Сохранить сцену»: клип ложится в папку фильма и встаёт в него
export async function saveToFolder(scope: string, sessionId: string, scene: VideoScene, versionId: string | undefined, to: SaveTarget): Promise<void> {
  const res = await saveScene(scope, sessionId, scene, versionId, to);
  if (res?.addedToFilm) void loadFilmList(scope, sessionId, true);
}

// Старт «Сохранить сцену» / «В фильм →»: открытый фильм старше папки сцены; нигде не лежит — false, спросить человека
export function startSceneSave(scope: string, sessionId: string, scene: VideoScene, versionId: string | undefined, kind: 'save' | 'film'): boolean {
  const to = saveTargetFor(getFocusedFilmPath(sessionId), scene);
  if (!to) return false;
  void (kind === 'save' ? saveToFolder(scope, sessionId, scene, versionId, to) : addToFilm(scope, sessionId, scene, versionId, to));
  return true;
}

// Фильма нет — спрашиваем, в какой: фильмы проекта списком и «Новый фильм» по имени
function FilmChooser({ scope, sessionId, onPick, onCancel }: { scope: string; sessionId: string; onPick: (folder: string) => void; onCancel: () => void }) {
  const films = useFilmList(scope, sessionId);
  const [name, setName] = useState('');
  const p = newFilmPath(name);
  return (
    <div data-video-film-chooser="" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, padding: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md }}>
      <b style={{ fontSize: FS.sm, color: C.textHeading }}>В какой фильм?</b>
      {films.map(x => (
        <Button key={x.path} size="sm" variant="ghost" leftIcon={ic(Film)} onClick={() => onPick(filmFolder(x.path))}
          style={{ justifyContent: 'flex-start' }}>{x.name}</Button>
      ))}
      <div style={{ display: 'flex', gap: SP.xs, alignItems: 'center' }}>
        <div style={{ flex: 1, minWidth: 0 }}><TextField value={name} onChange={setName} placeholder="Новый фильм, например утро-в-горах" /></div>
        <Button size="sm" disabled={!p} onClick={() => { if (p) onPick(filmFolder(p)); }}>Создать</Button>
      </div>
      <div><Button size="xs" variant="ghost" onClick={onCancel}>Отмена</Button></div>
    </div>
  );
}

export function SceneCard({ ctx, sceneId, jobId, record }: { ctx: ChatItemToolCtx; sceneId: string; jobId?: string | null; record?: Record<string, unknown> }) {
  const scope = videoScope(ctx.projectId);
  const personal = isPersonalScope(scope);
  const sessionId = ctx.sessionId;
  const state = useVideoThreads(scope, sessionId);
  const { primary } = useChatContext(sessionId);
  useVideoStoreVersion();
  // Полосу рисует карточка сцены; строки запусков (jobId) её не имеют и тикать не должны
  useJobTick(!jobId && !!sessionId && getJobsOf(sessionId, sceneId).length > 0);
  const [pos, setPos] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [ask, setAsk] = useState<'save' | 'film' | null>(null);
  const scene = state.scenes.find(s => s.sceneId === sceneId) ?? null;
  if (!scene || !sessionId) return null;
  const v = sceneCardView({
    scene, jobId, record, personal, focused: primary?.kind === SCENE_KIND && primary.ref.sceneId === scene.sceneId, jobs: getJobsOf(sessionId, scene.sceneId), pos, catalog: getCatalog(scope),
  });
  const run = async (fn: () => Promise<unknown>) => { setBusy(true); try { await fn(); } finally { setBusy(false); } };
  const ver = v.version;
  if (jobId) {
    return <LaunchRowView v={v} a={{ onPick: () => { void selectSceneByHuman(scope, sessionId, scene.sceneId); } }} />;
  }
  // Папка — из открытого фильма; нет фильма и у сцены нет папки — человека спрашивают
  const act = (kind: 'save' | 'film', folder: string) => run(() => (kind === 'save'
    ? saveToFolder(scope, sessionId, scene, ver?.versionId, { folder })
    : addToFilm(scope, sessionId, scene, ver?.versionId, { folder })));
  const start = (kind: 'save' | 'film') => { if (!startSceneSave(scope, sessionId, scene, ver?.versionId, kind)) setAsk(kind); };
  return (
    <SceneCardView v={v} busy={busy}
      chooser={ask && <FilmChooser scope={scope} sessionId={sessionId} onCancel={() => setAsk(null)}
        onPick={folder => { const k = ask; setAsk(null); void act(k, folder); }} />}
      src={ver ? videoApi.versionFileUrl(scope, sessionId, scene.sceneId, ver.versionId) : null}
      a={{
        onPick: () => { void selectSceneByHuman(scope, sessionId, scene.sceneId); },
        onPrev: () => setPos(Math.max(0, v.index - 1)),
        onNext: () => setPos(Math.min(v.versions.length - 1, v.index + 1)),
        onTake: () => { if (ver) void takeVersion(scope, sessionId, scene.sceneId, ver.versionId); },
        onSave: () => start('save'),
        onDownload: () => { if (ver) downloadClip(scope, sessionId, scene, ver.versionId); },
        onAddToFilm: () => start('film'),
        onWork: () => { void selectSceneByHuman(scope, sessionId, scene.sceneId); },
        onReshoot: () => { void selectSceneByHuman(scope, sessionId, scene.sceneId).then(() => openScenePanel(sessionId)); },
        onOpenFilm: () => { if (v.film) { const p = v.film.path; void selectFilmByHuman(scope, sessionId, p).then(() => openFilmPanel(sessionId, p)); } },
      }} />
  );
}

function Fallback({ text }: { text: string | null }) {
  return text ? <Line>{text}</Line> : null;
}

export function SceneAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  const rec = recordOf(ctx.item);
  const sceneId = str(rec?.data.sceneId);
  if (!on || !sceneId) return <Fallback text={rec?.fallback ?? null} />;
  return <SceneCard ctx={ctx} sceneId={sceneId} record={rec?.data} />;
}

export function LaunchAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  const rec = recordOf(ctx.item);
  const sceneId = str(rec?.data.sceneId);
  const jobId = str(rec?.data.jobId);
  if (!on || !sceneId || !jobId) return <Fallback text={rec?.fallback ?? null} />;
  return <SceneCard ctx={ctx} sceneId={sceneId} jobId={jobId} record={rec?.data} />;
}

// ── Тихие строки: сохранение, сборка фильма, заметки — одна разметка, у агента ещё «✦ Claude» ──

export interface QuietView { recordType: string; text: string; byClaude: boolean; filmPath: string | null; sceneId: string | null }

export function quietView(recordType: string, data: Record<string, unknown>, fallback: string | null): QuietView {
  const p = str(data.path);
  return {
    recordType,
    text: fallback ?? str(data.text) ?? '',
    byClaude: str(data.initiator) === 'agent',
    filmPath: str(data.filmPath) ?? (p?.endsWith('.film') ? p : null),
    sceneId: str(data.sceneId),
  };
}

export function QuietLineView({ v, onClick }: { v: QuietView; onClick?: () => void }) {
  return (
    <Line onClick={onClick} by={v.byClaude}>
      <span style={{ display: 'inline-flex', flexShrink: 0 }}>{ic(v.recordType === 'video_film_built' ? Film : Clapperboard)}</span>
      <span data-video-quiet={v.recordType} style={{ flex: '1 1 8em', minWidth: 0 }}>{v.text}</span>
    </Line>
  );
}

export function QuietLine({ ctx }: { ctx: ChatItemToolCtx }) {
  const on = useFeature(FLAGS.videoEditor);
  const rec = recordOf(ctx.item);
  if (!rec) return null;
  const v = quietView(rec.recordType, rec.data, rec.fallback);
  const scope = videoScope(ctx.projectId);
  const sessionId = ctx.sessionId;
  if (!on || !sessionId) return <Fallback text={v.text} />;
  const onClick = v.filmPath && !isPersonalScope(scope)
    ? () => { void selectFilmByHuman(scope, sessionId, v.filmPath!); }
    : v.sceneId ? () => { void selectSceneByHuman(scope, sessionId, v.sceneId!); } : undefined;
  return <QuietLineView v={v} onClick={onClick} />;
}
