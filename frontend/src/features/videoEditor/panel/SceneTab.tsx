// Вкладка «Сцена» (макет v7): кадры A → B с меню, текст сцены, исполнитель, длительность,
// пропорции, звук клипа. Низ — в панели (foot из useScene).

import { useEffect, useRef, useState } from 'react';
import { ArrowRight, AlertTriangle, RotateCw } from 'lucide-react';
import { Button, C, ExecutorList, ExecutorSummaryRow, FS, R, SegmentedControl, SP, Toggle, noteGenDraft, useGenDraft } from 'aihome_shell/kit';
import type { FrameRef, VideoScene } from '../api';
import { drawInImages, editFrame, frameBOf, runScene, uploadFrame } from '../scene/actions';
import { executorRows, pickRow, rowId, staleNotes } from '../scene/model';
import { setFailure, useFilm } from '../store/videoStore';
import { FrameMenu, FrameThumb } from './FrameSlot';
import { Hint, ic, Label } from './primitives';
import { SceneText, SceneTextExpanded } from './SceneText';
import type { SceneModel } from './useScene';

function Executor({ m, isMobile }: { m: SceneModel; isMobile: boolean }) {
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  const catalog = m.catalog;
  useEffect(() => {
    if (!open) return;
    const raf = requestAnimationFrame(() => box.current?.scrollIntoView({ block: 'nearest' }));
    return () => cancelAnimationFrame(raf);
  }, [open]);
  if (!catalog) return null;
  const rows = executorRows(catalog, m.personal, m.r.aspect, m.r.auto ? 'выбирает сервер' : '');
  const sel = rows.find(x => x.id === rowId(m.r));
  const name = m.r.auto ? 'Авто' : `${m.r.provider?.label} · ${m.r.model?.label}`;
  return (
    <div ref={box} data-video-executor={open ? 'open' : 'closed'} style={{ marginTop: SP.md }}>
      <ExecutorSummaryRow name={name} parts={m.r.auto ? ['порядок: ' + catalog.autoProviders.join(' → ')] : []}
        price={sel ? { label: sel.price, tone: m.r.provider?.key === 'local' ? 'success' : 'neutral' } : undefined}
        open={open} onToggle={() => setOpen(o => !o)} isMobile={isMobile} />
      {open && (
        <div style={{ marginTop: SP.xs }}>
          <ExecutorList rows={rows} value={rowId(m.r)} isMobile={isMobile}
            onChange={id => { const p = pickRow(id); m.change({ provider: p.provider, model: p.model }); setOpen(false); }} />
        </div>
      )}
    </div>
  );
}

export function SceneTab({ m, isMobile, prevScene, claudeFrames }: {
  m: SceneModel; isMobile: boolean; prevScene: VideoScene | null; claudeFrames?: boolean;
}) {
  const { scope, sessionId, scene, r, personal } = m;
  const [slot, setSlot] = useState<'A' | 'B' | null>(null);
  const [expanded, setExpanded] = useState(false);
  const draft = useGenDraft(m.draftKey);
  useEffect(() => { setSlot(null); setExpanded(false); }, [scene?.sceneId]);
  const film = useFilm(scope, scene?.filmRef ? sessionId : null, scene?.filmRef?.path ?? null);
  const lockAspect = !!scene?.filmRef && scene.filmRef.position > 0 && film.state ? film.state.document.aspect : null;
  const stale = staleNotes(scene);
  const pickFrame = (s: 'A' | 'B', f: FrameRef | null) => {
    m.change(s === 'A' ? { frameA: f ?? undefined } : { frameB: f ?? undefined });
    noteGenDraft(m.draftKey);
    setSlot(null);
  };
  const prevB = prevScene && frameBOf(prevScene) ? { frame: frameBOf(prevScene)!, sceneName: prevScene.name } : null;
  const edit = (v: string) => { m.change({ text: v }, true); noteGenDraft(m.draftKey); };

  if (expanded) return <SceneTextExpanded value={r.text} onChange={edit} onBack={() => setExpanded(false)} draft={draft} />;

  const emptyScene = !scene && !r.frameA && !r.frameB && !r.text;
  const frameProps = (s: 'A' | 'B') => ({
    scope, sessionId, label: s, frame: s === 'A' ? r.frameA : r.frameB, empty: 'из проекта, нарисовать',
    active: slot === s, onClick: () => setSlot(slot === s ? null : s), small: isMobile,
    stale: s === 'A' ? !!scene?.stale?.frameA : !!scene?.stale?.frameB, claude: claudeFrames,
  });
  const menuFor = (s: 'A' | 'B') => (
    <FrameMenu scope={scope} personal={personal} isMobile={isMobile} slot={s} frame={s === 'A' ? r.frameA : r.frameB} prevB={prevB}
      folder={scene?.folder ? `${scene.folder}/кадры` : 'video'}
      onPick={f => pickFrame(s, f)}
      onEdit={() => { if (scene && sessionId) void editFrame(scope, sessionId, scene, s); setSlot(null); }}
      onDraw={() => { if (sessionId) void drawInImages(scope, sessionId, s); setSlot(null); }}
      onUpload={file => { void uploadFrame(scope, scene, file).then(f => { if (f) pickFrame(s, f); }); }}
      onClose={() => setSlot(null)} />
  );

  const model = r.model;
  return (
    <div data-video-scene-tab="">
      {emptyScene && (
        <div data-video-empty="scene" style={{ margin: `${SP.sm}px 0`, fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.45 }}>
          <b style={{ color: C.textHeading }}>Сцена — клип от кадра A к кадру B.</b> Выберите кадры и напишите, что происходит между ними.
          Цена видна внизу до запуска; варианты лягут в ленту карточкой, лучший сохраните в проект и поставьте в фильм.
        </div>
      )}
      <Label>Кадры</Label>
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.xs }}>
        <FrameThumb {...frameProps('A')} />
        <span style={{ alignSelf: 'center', color: C.textMuted, display: 'inline-flex' }}>{ic(ArrowRight)}</span>
        <FrameThumb {...frameProps('B')} />
      </div>
      {slot && menuFor(slot)}
      {stale.map(t => (
        <div key={t} data-video-stale="" style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', marginTop: SP.xs, fontSize: FS.sm, color: C.warningText }}>
          <span style={{ display: 'inline-flex', marginTop: 1 }}>{ic(AlertTriangle)}</span><span>{t}</span>
        </div>
      ))}

      <SceneText value={r.text} onChange={edit} onExpand={() => setExpanded(true)} draft={draft} />

      <Executor m={m} isMobile={isMobile} />
      {m.failure && (
        <div data-video-failure="" style={{ marginTop: SP.sm, padding: SP.sm, borderRadius: R.md, background: C.bgInset, fontSize: FS.sm, color: C.warningText }}>
          <div>{m.failure.text}</div>
          {m.failure.retry && sessionId && scene && (
            <div style={{ marginTop: SP.xs, display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
              <Button size="xs" variant="secondary" leftIcon={ic(RotateCw)}
                onClick={() => { void runScene({ scope, sessionId, scene, r, quote: m.failure!.retry!.quote }); }}>
                Повторить: {m.failure.retry.model} · {m.failure.retry.reason}
              </Button>
              <Button size="xs" variant="ghost" onClick={() => setFailure(sessionId, null)}>Скрыть</Button>
            </div>
          )}
        </div>
      )}

      <Label>Длительность</Label>
      {model && model.durations.length > 0
        ? <SegmentedControl<string> value={String(r.durationSec)} onChange={v => m.change({ durationSec: Number(v) })}
          options={model.durations.map(d => ({ value: String(d), label: `${d} с` }))} />
        : <DurationFree value={r.durationSec} onChange={d => m.change({ durationSec: d })} />}
      {model && <Hint>{model.label}: {model.durations.join(' / ')} с</Hint>}

      <Label>Пропорции</Label>
      {lockAspect
        ? <Hint>Как у фильма — {lockAspect}. Пропорции задаёт первая сцена, иначе сборка дала бы поля.</Hint>
        : (
          <SegmentedControl<string> value={r.aspect} onChange={v => m.change({ aspect: v })}
            options={(model?.aspects.length ? model.aspects : ['16:9', '9:16', '1:1']).map(a => ({ value: a, label: a }))} />
        )}

      <Label>Звук клипа</Label>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Toggle checked={r.sound} disabled={!!model && !model.sound} onChange={v => m.change({ sound: v })} ariaLabel="Звук клипа" />
        <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
          {model && !model.sound
            ? `${model.label} снимает без звука — музыку поставите во вкладке «Фильм»`
            : r.sound ? 'Со звуком: шум, голоса и реплики из текста' : 'Без звука'}
        </span>
      </div>
    </div>
  );
}

function DurationFree({ value, onChange }: { value: number; onChange: (d: number) => void }) {
  return (
    <SegmentedControl<string> value={String(value)} onChange={v => onChange(Number(v))}
      options={[5, 8, 10].map(d => ({ value: String(d), label: `${d} с` }))} />
  );
}
