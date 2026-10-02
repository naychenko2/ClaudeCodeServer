// Вкладка «Фильм» (макет v7): строка контекста с меню фильмов и тратами, монтажный список со склейками и
// подрезкой, добавление сцен, музыка с «Сочинить под фильм…», «Сценарий» и закреплённый низ сборки.
// Фильмы — только в чате проекта: в личном чате вкладка остаётся с объяснением.

import { useEffect, useRef, useState, useSyncExternalStore, type CSSProperties, type ReactNode } from 'react';
import { Check, ChevronDown, Clapperboard, Film, FilePlus2, FolderTree, Hammer, ListVideo, Music, Plus, RefreshCw, Sparkles, X } from 'lucide-react';
import {
  Button, C, FS, IconButton, Menu, MenuItem, MenuSep, R, SegmentedControl, SP, TextField, ByClaude, showToast,
  type GenerationFoot,
} from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { ERR, type FilmState, type VideoScene } from '../api';
import { ProjectPicker } from '../panel/ProjectPicker';
import { Hint, ic, Label } from '../panel/primitives';
import { createScene, openScenePanel, selectSceneByHuman } from '../scene/actions';
import { isPersonalScope, videoScope } from '../scope';
import {
  buildFilm, cancelBuild, filmName, focusFilm, loadFilmList, patchFilm, useFilm, useFilmList, useVideoStoreVersion,
  useVideoThreads,
} from '../store/videoStore';
import { filmDuration } from '../strip/summary';
import { TOUCH, useBoxWidth } from '../useBoxWidth';
import { composeForFilm, isComposing, isFromSound } from './compose';
import { FilmList, type RowActions } from './FilmList';
import {
  buildView, filmClock, filmFolder, fileName, newFilmPath, sceneOfItem, scenesWord, snapshotOf, spentText,
} from './model';
import { openProjectFile } from './nav';
import { ScriptView } from './ScriptView';

const MP4_RE = /\.(mp4|webm|mov)$/i;
const AUDIO_RE = /\.(mp3|wav|flac|ogg|m4a)$/i;

// Сборка, упёршаяся в 503 dsp_unavailable: «Собрать» серое с причиной, пока человек не проверит снова
// (кнопка «Проверить снова» в теле вкладки) или не откроет вкладку заново — сервер за это время мог поправиться
export const DSP_TEXT = 'Сборка фильмов на этом сервере выключена — обратитесь к администратору';
const _blocked = new Map<string, string>();
const _blockedSubs = new Set<() => void>();
let _blockedVer = 0;
const setBlocked = (path: string, text: string | null) => {
  if (text === null ? !_blocked.delete(path) : (_blocked.set(path, text), false)) return;
  _blockedVer++;
  _blockedSubs.forEach(f => f());
};
const useBlocked = (path: string | null): string | undefined => {
  useSyncExternalStore(f => { _blockedSubs.add(f); return () => { _blockedSubs.delete(f); }; }, () => _blockedVer, () => _blockedVer);
  return path ? _blocked.get(path) : undefined;
};

// Высота тач-цели на телефоне для кнопок xs (24 px) и строк меню
const touchH = (isMobile: boolean): CSSProperties | undefined => (isMobile ? { height: TOUCH, minHeight: TOUCH } : undefined);

export interface FilmPanelModel {
  foot?: GenerationFoot;
  count: number;
  subtitle?: string;
  context?: ReactNode;
  contextAction?: ReactNode;
  peekSummary?: string;
  nameOf: (path: string) => string;
}

// Шапка, строка контекста и низ вкладки — для каркаса панели
export function useFilmPanel(projectId: string | null, sessionId: string | null, isMobile = false): FilmPanelModel {
  const scope = videoScope(projectId);
  const personal = isPersonalScope(scope);
  const threads = useVideoThreads(scope, sessionId);
  useVideoStoreVersion();
  const path = personal ? null : threads.focus.filmPath ?? null;
  const film = useFilm(scope, path ? sessionId : null, path);
  const f = film.state;
  const name = path ? filmName(path) : '';
  const blocked = useBlocked(path);
  const nameOf = (p: string) => filmName(p);
  if (personal) {
    return { count: 0, context: <span>Фильмы — только в проекте</span>, nameOf };
  }
  if (!path || !sessionId) {
    return { count: 0, context: <span>Фильм не выбран</span>, nameOf, peekSummary: 'Фильм не выбран' };
  }
  const count = f?.document.items.length ?? 0;
  const dur = f ? filmClock(f.document) : '0:00';
  const v = f ? buildView(f) : { kind: 'empty' as const };
  const runBuild = async () => {
    setBlocked(path, null);
    const ok = await buildFilm(scope, sessionId, path);
    if (!ok.ok && ok.code === ERR.dspUnavailable) setBlocked(path, DSP_TEXT);
  };
  const base: GenerationFoot = {
    reason: count === 0 ? 'Добавьте в фильм хотя бы одну сцену' : blocked,
    price: [`${dur} · бесплатно`, `${count} ${scenesWord(count)} · сборка без ИИ`],
    runLabel: 'Собрать',
    runIcon: ic(Hammer),
    onRun: () => { void runBuild(); },
  };
  let foot: GenerationFoot = base;
  const resultActions = (file: string) => [
    { label: 'Открыть', onClick: () => { void openProjectFile(file).then(ok => { if (!ok) showToast('Файл открывается в проекте', '', 'info'); }); } },
    { label: 'Показать в дереве', onClick: () => { void openProjectFile(file, true); } },
  ];
  if (v.kind === 'waiting') {
    foot = { ...base, progress: { label: 'Ждём очередь сборки…', onCancel: () => { void cancelBuild(scope, sessionId, path); } } };
  } else if (v.kind === 'running') {
    const pct = Math.round(v.progress * 100);
    const target = fileName(f?.build?.file ?? 'film.mp4');
    foot = { ...base, progress: { label: `Собираем ${target}… ${pct} %`, p: pct, onCancel: () => { void cancelBuild(scope, sessionId, path); } } };
  } else if (v.kind === 'done') {
    foot = v.stale.length
      ? { ...base, stale: v.stale, result: { file: fileName(v.file), actions: resultActions(v.file) }, runLabel: 'Пересобрать' }
      : { ...base, result: { file: fileName(v.file), actions: resultActions(v.file) }, runLabel: 'Собрано', runIcon: ic(Check), runDisabled: true };
  } else if (v.kind === 'failed') {
    foot = { ...base, runLabel: 'Собрать ещё раз' };
  }
  return {
    foot, count, nameOf,
    subtitle: `${name} · ${dur}`,
    peekSummary: `${name} · ${count} ${scenesWord(count)} · ${dur}`,
    context: <FilmContext scope={scope} sessionId={sessionId} path={path} f={f} />,
    contextAction: (
      <IconButton size={isMobile ? 'lg' : 'xs'} title="Закрыть фильм — он сохранён в проекте" ariaLabel="Закрыть фильм"
        style={isMobile ? { width: TOUCH, height: TOUCH } : undefined}
        onClick={() => { void focusFilm(scope, sessionId, null); }}>{ic(X)}</IconButton>
    ),
  };
}

// Строка контекста: «Фильм: утро-в-горах ▾ · video/утро-в-горах/ · $13.00». Имя фильма не режем никогда:
// на узкой колонке прячем сначала путь (он есть в меню ▾), потом слово «Фильм:»
const CTX_WIDE = 420;
const CTX_PATH = 520;

function FilmContext({ scope, sessionId, path, f }: { scope: string; sessionId: string; path: string; f: FilmState | null }) {
  const [menuAt, setMenuAt] = useState<DOMRect | null>(null);
  const [spentOpen, setSpentOpen] = useState(false);
  const films = useFilmList(scope, sessionId);
  const spent = spentText(f?.spent);
  const box = useRef<HTMLSpanElement>(null);
  const w = useBoxWidth(box);
  const showPrefix = w === 0 || w >= CTX_WIDE;
  const showPath = w >= CTX_PATH;
  return (
    <span ref={box} data-video-context="film" style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: SP.xs, overflow: 'hidden' }}>
      <span style={{ display: 'inline-flex', color: C.textMuted, flexShrink: 0 }}>{ic(Film)}</span>
      <Button size="xs" variant="ghost" title={`Фильмы проекта · ${path}`} onClick={e => setMenuAt((e.currentTarget as HTMLElement).getBoundingClientRect())}
        style={{ minWidth: 0, flexShrink: 0, maxWidth: '100%', height: 22, padding: `0 ${SP.xxs}px` }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, minWidth: 0 }}>
          <span style={{ whiteSpace: 'nowrap' }}>{showPrefix && 'Фильм: '}<b style={{ color: C.textHeading }}>{filmName(path)}</b></span>
          {ic(ChevronDown)}
        </span>
      </Button>
      {showPath && (
        <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}>{filmFolder(path)}/</span>
      )}
      <span style={{ flex: 1 }} />
      {spent && (
        <span style={{ position: 'relative', flexShrink: 0 }}>
          <Button size="xs" variant="ghost" title="Потрачено на сцены фильма" onClick={() => setSpentOpen(o => !o)} style={{ height: 22, padding: `0 ${SP.xxs}px` }}>
            <span data-video-spent="">{spent}</span>
          </Button>
          {spentOpen && (
            <Menu onClose={() => setSpentOpen(false)} minWidth={260} top={26}>
              <div style={{ padding: SP.sm, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
                <b style={{ color: C.textHeading }}>Потрачено на фильм: {spent}</b>
                <div style={{ marginTop: SP.xxs, fontSize: FS.xs, color: C.textMuted }}>
                  Деньги списываются, когда клип готов; остановленное не списывается. Сборка фильма бесплатна. Потолка трат нет — это только счётчик.
                </div>
              </div>
            </Menu>
          )}
        </span>
      )}
      {menuAt && (
        <Menu onClose={() => setMenuAt(null)} anchor={menuAt} anchorAlign="start" minWidth={280}>
          {films.map(x => (
            <MenuItem key={x.path} icon={ic(Film)} label={x.name} hint={`${x.path}${x.stale ? ' · устарел' : ' · собран'}`}
              onClick={() => { setMenuAt(null); void focusFilm(scope, sessionId, x.path); }} />
          ))}
          {films.length > 0 && <MenuSep />}
          <MenuItem icon={ic(FilePlus2)} label="Новый фильм" onClick={() => { setMenuAt(null); setNewFilm(sessionId); }} />
          <MenuItem icon={ic(FolderTree)} label="Показать в дереве" hint={path} onClick={() => { setMenuAt(null); void openProjectFile(path, true); }} />
        </Menu>
      )}
    </span>
  );
}

// «Новый фильм»: поле имени показывает тело вкладки; флажок — на чат
const _newFilm = new Set<string>();
const _newListeners = new Set<() => void>();
function setNewFilm(sessionId: string) { _newFilm.add(sessionId); _newListeners.forEach(f => f()); }

function useNewFilm(sessionId: string | null): [boolean, () => void, () => void] {
  const [, set] = useState(0);
  useEffect(() => { const f = () => set(n => n + 1); _newListeners.add(f); return () => { _newListeners.delete(f); }; }, []);
  const on = !!sessionId && _newFilm.has(sessionId);
  return [on, () => { if (sessionId) { _newFilm.add(sessionId); set(n => n + 1); } }, () => { if (sessionId) { _newFilm.delete(sessionId); set(n => n + 1); } }];
}

function NewFilmForm({ onCreate, onCancel }: { onCreate: (name: string) => void; onCancel: () => void }) {
  const [name, setName] = useState('');
  const p = newFilmPath(name);
  return (
    <div data-video-new-film="" style={{ marginTop: SP.sm, padding: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md }}>
      <Label>Новый фильм</Label>
      <TextField value={name} onChange={setName} placeholder="Например, утро-в-горах" autoFocus />
      <Hint>{p ? `Файл: ${p}` : 'Имя станет папкой и файлом фильма в video/'}</Hint>
      <div style={{ display: 'flex', gap: SP.xs, marginTop: SP.sm }}>
        <Button size="sm" disabled={!p} onClick={() => onCreate(name)}>Завести</Button>
        <Button size="sm" variant="ghost" onClick={onCancel}>Отмена</Button>
      </div>
    </div>
  );
}

function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div data-video-empty="film" style={{ margin: `${SP.md}px 0`, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
      <b style={{ color: C.textHeading }}>{title}</b> {children}
    </div>
  );
}

type Picking = null | 'scene' | 'mp4' | 'music';

export function FilmTab({ ctx }: { ctx: WorkspacePanelDefCtx }) {
  const { sessionId, isMobile } = ctx;
  const scope = videoScope(ctx.projectId);
  const personal = isPersonalScope(scope);
  const threads = useVideoThreads(scope, sessionId);
  useVideoStoreVersion();
  const path = personal ? null : threads.focus.filmPath ?? null;
  const film = useFilm(scope, path ? sessionId : null, path);
  const films = useFilmList(scope, personal ? null : sessionId);
  const dspBlocked = useBlocked(path);
  const [newOpen, openNew, closeNew] = useNewFilm(sessionId);
  const [picking, setPicking] = useState<Picking>(null);
  const [script, setScript] = useState(false);
  const [highlight, setHighlight] = useState<number | null>(null);
  const [shownPath, setShownPath] = useState(path);
  if (shownPath !== path) { setShownPath(path); setPicking(null); setScript(false); setHighlight(null); }
  // Открыли вкладку заново — прошлый отказ «сборка выключена» мог устареть: проверим при следующем «Собрать»
  useEffect(() => { if (path) setBlocked(path, null); }, [path]);

  if (personal) {
    return (
      <Empty title="Фильмы живут в проекте.">
        Фильм — файл <code>.film</code> в папке проекта: порядок сцен, склейки, музыка. В личном чате сцены можно снимать и скачивать,
        а собрать их в фильм — в чате проекта.
      </Empty>
    );
  }
  if (!sessionId) return <Empty title="Сначала начните чат." />;

  const create = async (name: string) => {
    const p = newFilmPath(name);
    if (!p) return;
    closeNew();
    if (await focusFilm(scope, sessionId, p)) void loadFilmList(scope, sessionId, true);
  };

  if (!path) {
    return (
      <div>
        <Empty title="Фильм не выбран.">Откройте файл <code>.film</code> в дереве или заведите новый.</Empty>
        <Button size="sm" leftIcon={ic(Plus)} onClick={openNew}>Новый фильм</Button>
        {newOpen && <NewFilmForm onCreate={n => { void create(n); }} onCancel={closeNew} />}
        {films.length > 0 && (
          <>
            <Label>Фильмы проекта</Label>
            {films.map(x => (
              <Button key={x.path} size="sm" variant="ghost" onClick={() => { void focusFilm(scope, sessionId, x.path); }}
                style={{ width: '100%', justifyContent: 'flex-start', minHeight: isMobile ? TOUCH : 36 }}>
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
                  {ic(Film)}<span style={{ fontWeight: 400, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{x.name}</span>
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>{x.itemCount} {scenesWord(x.itemCount)}{x.stale ? ' · устарел' : ''}</span>
                </span>
              </Button>
            ))}
          </>
        )}
      </div>
    );
  }

  const f = film.state;
  if (!f) {
    if (film.loading) return <div style={{ paddingTop: SP.sm, fontSize: FS.sm, color: C.textMuted }}>Загружаем фильм…</div>;
    return (
      <div>
        <Empty title={film.code === ERR.filmSchema ? 'Фильм записан новой версией формата — только чтение.' : 'Фильм не прочитался.'}>
          {film.error}
        </Empty>
        <Button size="sm" variant="ghost" onClick={() => { void focusFilm(scope, sessionId, null); }}>Закрыть фильм</Button>
      </div>
    );
  }

  const doc = f.document;
  const scenes = threads.scenes;
  const patch = (ops: Parameters<typeof patchFilm>[3]) => patchFilm(scope, sessionId, path, ops);
  const reshoot = (i: number) => {
    const it = doc.items[i];
    const s = sceneOfItem(scenes, it);
    if (s) void selectSceneByHuman(scope, sessionId, s.sceneId).then(() => openScenePanel(sessionId));
    else {
      // Сцены в этом чате нет: заводим новую по снимку из фильма
      const fa = it.scene?.frameA;
      void createScene(scope, sessionId, fa ? { frameA: { kind: 'file', path: fa } } : {}).then(() => openScenePanel(sessionId));
    }
  };
  const actions: RowActions = {
    onMove: (from, to) => { void patch([{ op: 'move', from, to }]); setHighlight(to); },
    onRemove: i => { void patch([{ op: 'remove', index: i }]); },
    onTrim: (i, trim) => { void patch([{ op: 'trim', index: i, trim }]); },
    onCut: (i, type, sec) => { void patch([{ op: 'cut', index: i, cutType: type, ...(type === 'butt' ? {} : { sec }) }]); },
    onReshoot: reshoot,
    onReveal: i => { void openProjectFile(doc.items[i].file, true); },
  };
  const addFile = (file: string, scene?: VideoScene | null) => {
    setPicking(null);
    void patch([{ op: 'add', file, ...(scene ? { scene: snapshotOf(scene) } : {}) }]).then(ok => { if (ok) setHighlight(doc.items.length); });
  };
  const inFilm = new Set(doc.items.map(i => i.file));
  const savedOutside = scenes.flatMap(s => s.savedFiles.filter(x => !inFilm.has(x.path)).map(x => ({ s, path: x.path })));
  const lastFrameB = doc.items[doc.items.length - 1]?.scene?.frameB;
  const newScene = () => {
    void createScene(scope, sessionId, lastFrameB ? { frameA: { kind: 'file', path: lastFrameB } } : {}).then(() => openScenePanel(sessionId));
  };

  if (script) {
    return <ScriptView scope={scope} sessionId={sessionId} doc={doc} scenes={scenes} onBack={() => setScript(false)} onReshoot={reshoot} />;
  }

  const music = doc.music;
  const composing = isComposing(path, music?.file);
  return (
    <div data-video-film-tab="">
      {dspBlocked && (
        <div data-video-dsp-blocked="" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.sm, fontSize: FS.sm, color: C.warningText }}>
          <span style={{ flex: '1 1 auto', minWidth: 0 }}>{dspBlocked}</span>
          <Button size="xs" variant="secondary" leftIcon={ic(RefreshCw)} onClick={() => setBlocked(path, null)} style={touchH(isMobile)}>Проверить снова</Button>
        </div>
      )}
      {f.build?.state === 'failed' && (
        <div data-video-build-failed="" style={{ marginTop: SP.sm, fontSize: FS.sm, color: C.warningText }}>Прошлая сборка не получилась: {f.build.error ?? 'причина не пришла'}</div>
      )}
      <Label aside={<Button size="xs" variant="ghost" leftIcon={ic(ListVideo)} onClick={() => setScript(true)} disabled={!doc.items.length} style={touchH(isMobile)}>Сценарий</Button>}>
        Сцены · {doc.items.length} · {filmClock(doc)}
      </Label>
      {doc.items.length === 0
        ? (
          <Empty title="В фильме пока нет сцен.">
            Поставьте сохранённую сцену, готовый mp4 или снимите новую. Из ленты сцену добавляет кнопка «В фильм →» в её карточке.
          </Empty>
        )
        : <FilmList scope={scope} items={doc.items} cuts={doc.cuts} marks={f.marks} highlight={highlight} isMobile={isMobile} a={actions} />}

      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.sm }}>
        <Button size="xs" variant="secondary" leftIcon={ic(Plus)} onClick={() => setPicking(picking === 'scene' ? null : 'scene')} style={touchH(isMobile)}>Сцена из проекта</Button>
        <Button size="xs" variant="secondary" leftIcon={ic(Plus)} onClick={() => setPicking(picking === 'mp4' ? null : 'mp4')} style={touchH(isMobile)}>Готовый mp4</Button>
        <Button size="xs" variant="secondary" leftIcon={ic(Clapperboard)} onClick={newScene} style={touchH(isMobile)}>Снять новую</Button>
      </div>
      {picking === 'scene' && (
        <div data-video-add-scene="" style={{ marginTop: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md, padding: SP.xxs }}>
          {savedOutside.length === 0 && <div style={{ padding: SP.sm, fontSize: FS.sm, color: C.textMuted }}>Сохранённых сцен вне фильма в этом чате нет — выберите файл ниже.</div>}
          {savedOutside.map(x => (
            <Button key={x.path} size="sm" variant="ghost" onClick={() => addFile(x.path, x.s)} style={{ width: '100%', justifyContent: 'flex-start', minHeight: isMobile ? TOUCH : 36 }}>
              <span style={{ fontWeight: 400 }}>{x.s.name} · {fileName(x.path)}</span>
            </Button>
          ))}
          <ProjectPicker scope={scope} start={filmFolder(path)} accept={MP4_RE} emptyText="В этой папке нет клипов"
            onBack={() => setPicking(null)} onPick={p => addFile(p, scenes.find(s => s.savedFiles.some(x => x.path === p)))} />
        </div>
      )}
      {picking === 'mp4' && (
        <div style={{ marginTop: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md, padding: SP.xxs }}>
          <ProjectPicker scope={scope} start="" accept={MP4_RE} emptyText="В этой папке нет клипов" onBack={() => setPicking(null)} onPick={p => addFile(p)} />
        </div>
      )}

      <Label by={music && isFromSound(path, music.file) ? <span style={{ fontSize: FS.xs, color: C.textMuted }}>из «Звука»</span> : undefined}>Музыка</Label>
      {music ? (
        <div data-video-music="" style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.md, padding: SP.sm }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
            <span style={{ color: C.textMuted, display: 'inline-flex' }}>{ic(Music)}</span>
            <span title={music.file} style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{fileName(music.file)}</span>
            <Button size="xs" variant="ghost" onClick={() => setPicking(picking === 'music' ? null : 'music')} style={touchH(isMobile)}>Сменить</Button>
            <IconButton size={isMobile ? 'lg' : 'xs'} title="Убрать музыку" ariaLabel="Убрать музыку" onClick={() => { void patch([{ op: 'music', music: null }]); }}
              style={isMobile ? { width: TOUCH, height: TOUCH } : undefined}>{ic(X)}</IconButton>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.sm }}>
            <span style={{ fontSize: FS.xs, color: C.textMuted, width: 64 }}>Громкость</span>
            <input key={music.volume} type="range" min={0} max={100} step={5} defaultValue={music.volume} aria-label="Громкость музыки"
              onMouseUp={e => { void patch([{ op: 'music', music: { ...music, volume: Number((e.target as HTMLInputElement).value) } }]); }}
              onTouchEnd={e => { void patch([{ op: 'music', music: { ...music, volume: Number((e.target as HTMLInputElement).value) } }]); }}
              onKeyUp={e => { void patch([{ op: 'music', music: { ...music, volume: Number((e.target as HTMLInputElement).value) } }]); }}
              style={{ flex: 1, accentColor: C.accent }} />
            <span style={{ fontSize: FS.xs, color: C.textSecondary, width: 36, textAlign: 'right' }}>{music.volume} %</span>
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.sm }}>
            <span style={{ fontSize: FS.xs, color: C.textMuted, width: 64 }}>Затухание</span>
            <span style={{ flex: 1 }}>
              <SegmentedControl<string> value={String(music.fadeOut)} onChange={v => { void patch([{ op: 'music', music: { ...music, fadeOut: Number(v) } }]); }}
                options={[{ value: '0', label: 'нет' }, { value: '2', label: '2 с' }, { value: '4', label: '4 с' }]} />
            </span>
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm, color: C.textSecondary }}>
          <span style={{ flex: 1 }}>Без музыки · в фильме только звук сцен</span>
          <Button size="xs" variant="ghost" onClick={() => setPicking(picking === 'music' ? null : 'music')} style={touchH(isMobile)}>Файл из проекта</Button>
        </div>
      )}
      {picking === 'music' && (
        <div style={{ marginTop: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md, padding: SP.xxs }}>
          <ProjectPicker scope={scope} start="music" accept={AUDIO_RE} emptyText="В этой папке нет звука" onBack={() => setPicking(null)}
            onPick={p => { setPicking(null); void patch([{ op: 'music', music: { file: p, volume: music?.volume ?? 60, fadeOut: music?.fadeOut ?? 2 } }]); }} />
        </div>
      )}
      {doc.items.length > 0 && (
        <div style={{ marginTop: SP.sm }}>
          <Button size="sm" variant="secondary" leftIcon={ic(Sparkles)} disabled={composing} style={isMobile ? { minHeight: TOUCH } : undefined}
            onClick={() => { void composeForFilm(scope, sessionId, filmName(path), f); }}>
            {composing ? 'Сочиняем в «Звуке»…' : 'Сочинить под фильм…'}
          </Button>
          <Hint>
            Откроет «Звук» с заготовкой: длина {filmClock(doc)}, настроение по текстам сцен. Готовый трек встанет сюда сам.
          </Hint>
        </div>
      )}
      {f.document.builds.length > 0 && f.marks.some(m => m.claude) && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginTop: SP.md, fontSize: FS.xs, color: C.textMuted }}>
          <ByClaude /> — правки Claude в этом фильме; ваша правка снимет метку
        </div>
      )}
      <Hint>Фильм сохраняется сам на каждое изменение: <code>{path}</code>{doc.items.length > 0 && ` · ${Math.round(filmDuration(doc))} с`}</Hint>
    </div>
  );
}
