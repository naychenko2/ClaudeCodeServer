// Редактор «Сцена» (ADR-023, шаг 3ф-2): текст сцены, пропорции и звук клипа — то, что не живёт чипами.
// Открывается из панели «Контекст» («Редактор сцены»); правит ОСНОВНУЮ сцену контекста, а не сцену серверного
// фокуса. Правка уходит в сцену с задержкой, закрытие окна дописывает недосохранённое; ничего не отменяет.

import { useEffect, useState } from 'react';
import { Check } from 'lucide-react';
import {
  Button, C, FS, getChatContextState, ICON_SIZE, ICON_STROKE, Modal, MODAL_W, noteGenDraft, SegmentedControl, SP, Toggle,
  useGenDraft,
} from 'aihome_shell/kit';
import type { VideoScene } from '../api';
import { sceneOfPrimary } from '../context/state';
import { changeSettings, currentResolved, flushSettings } from '../scene/actions';
import { isPersonalScope, videoScope } from '../scope';
import { closeVideoEditor, sceneDraftKey, useFilm, useVideoStoreVersion } from '../store/videoStore';
import { Hint, Label } from './primitives';
import { SceneText, SceneTextExpanded } from './SceneText';

function Body({ scope, sessionId, scene }: { scope: string; sessionId: string; scene: VideoScene }) {
  const personal = isPersonalScope(scope);
  const [expanded, setExpanded] = useState(false);
  const draftKey = sceneDraftKey(scene.sceneId);
  const draft = useGenDraft(draftKey);
  const film = useFilm(scope, scene.filmRef && !personal ? sessionId : null, scene.filmRef?.path ?? null);
  const r = currentResolved(sessionId, scope, scene);
  const model = r.model;
  const lockAspect = scene.filmRef && scene.filmRef.position > 0 && film.state ? film.state.document.aspect : null;
  const change = (patch: Parameters<typeof changeSettings>[2], debounced = false) =>
    changeSettings(scope, sessionId, patch, debounced, scene.sceneId);
  const edit = (v: string) => { change({ text: v }, true); noteGenDraft(draftKey); };

  if (expanded) return <SceneTextExpanded value={r.text} onChange={edit} onBack={() => setExpanded(false)} draft={draft} />;
  return (
    <>
      <SceneText value={r.text} onChange={edit} onExpand={() => setExpanded(true)} draft={draft} />
      <Label>Пропорции</Label>
      {lockAspect
        ? <Hint>Как у фильма — {lockAspect}. Пропорции задаёт первая сцена, иначе сборка дала бы поля.</Hint>
        : (
          <SegmentedControl<string> value={r.aspect} onChange={v => change({ aspect: v })}
            options={(model?.aspects.length ? model.aspects : ['16:9', '9:16', '1:1']).map(a => ({ value: a, label: a }))} />
        )}
      <Label>Звук клипа</Label>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Toggle checked={r.sound} disabled={!!model && !model.sound} onChange={v => change({ sound: v })} ariaLabel="Звук клипа" />
        <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
          {model && !model.sound
            ? `${model.label} снимает без звука — музыку ставят в «Монтаже»`
            : r.sound ? 'Со звуком: шум, голоса и реплики из текста' : 'Без звука'}
        </span>
      </div>
    </>
  );
}

export function SceneEditor({ projectId, sessionId }: { projectId: string | null; sessionId: string }) {
  useVideoStoreVersion();
  const scope = videoScope(projectId);
  const scene = sceneOfPrimary(sessionId, getChatContextState(sessionId).primary);
  // Недосохранённая правка уходит в свою сцену при закрытии окна
  useEffect(() => () => { void flushSettings(scope, sessionId); }, [scope, sessionId]);

  return (
    <Modal width={MODAL_W.wide} title={scene ? `Сцена · ${scene.name}` : 'Сцена'}
      subtitle={scene?.filmRef ? 'в фильме' : scene ? 'не в фильме' : undefined}
      onClose={closeVideoEditor} closeOnBackdrop={false}
      footer={(
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', justifyContent: 'flex-end' }}>
          <span style={{ marginRight: 'auto', fontSize: FS.xs, color: C.textMuted }}>Правки сохраняются сами; снимает чип «Снять» в поле ввода</span>
          <Button size="sm" variant="primary" leftIcon={<Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={closeVideoEditor}>Готово</Button>
        </div>
      )}>
      <div data-video-scene-editor="">
        {scene
          ? <Body scope={scope} sessionId={sessionId} scene={scene} />
          : <div style={{ fontSize: FS.sm, color: C.textMuted }}>Сцена не выбрана — возьмите её в работу в чате.</div>}
      </div>
    </Modal>
  );
}
