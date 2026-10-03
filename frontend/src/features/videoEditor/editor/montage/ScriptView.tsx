// «Сценарий» — на всю высоту тела (макет v7): тексты сцен по порядку фильма полями, у каждой
// «Переснять →». Текст сцены этого чата правится прямо здесь (снятая сцена получит «изменён —
// переснять»); сцена из другого чата — снимок из .film, только чтение.

import { useState } from 'react';
import { ArrowLeft, ArrowRight } from 'lucide-react';
import { Button, C, FS, R, SP, TextArea } from 'aihome_shell/kit';
import type { FilmDocument, VideoScene } from '../../api';
import { ic } from '../primitives';
import { changeSettings } from '../../scene/actions';
import { focusScene } from '../../store/videoStore';
import { fileName, sceneOfItem } from '../../film/model';

export function ScriptView({ scope, sessionId, doc, scenes, onBack, onReshoot }: {
  scope: string; sessionId: string; doc: FilmDocument; scenes: VideoScene[]; onBack: () => void; onReshoot: (i: number) => void;
}) {
  const edit = async (s: VideoScene, text: string) => {
    await focusScene(scope, sessionId, s.sceneId);
    changeSettings(scope, sessionId, { text }, true);
  };
  return (
    <div data-video-script="" style={{ paddingTop: SP.sm }}>
      <Button size="xs" variant="ghost" leftIcon={ic(ArrowLeft)} onClick={onBack}>Весь фильм</Button>
      {doc.items.map((it, i) => {
        const s = sceneOfItem(scenes, it);
        const text = s?.settings.text ?? it.scene?.text ?? '';
        const stale = !!s?.stale?.text && s.versions.length > 0;
        return (
          <div key={`${it.file}#${i}`} style={{ marginTop: SP.md, padding: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.md }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginBottom: SP.xs }}>
              <b style={{ fontSize: FS.sm, color: C.textHeading }}>{i + 1}. {s?.name ?? fileName(it.file)}</b>
              {stale && <span style={{ fontSize: FS.xs, color: C.warningText }}>изменён — переснять</span>}
              <span style={{ flex: 1 }} />
              <Button size="xs" variant="ghost" onClick={() => onReshoot(i)}><span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs }}>Переснять {ic(ArrowRight)}</span></Button>
            </div>
            {s
              ? <ScriptField value={text} onChange={v => { void edit(s, v); }} />
              : <div style={{ fontSize: FS.sm, color: C.textSecondary, whiteSpace: 'pre-wrap' }}>{text || '—'}<div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>Снимок из фильма: сцена снималась в другом чате</div></div>}
          </div>
        );
      })}
    </div>
  );
}

// Поле держит правку у себя: значение с сервера приходит позже набора
function ScriptField({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const [text, setText] = useState(value);
  return <TextArea value={text} onChange={v => { setText(v); onChange(v); }} autoGrow minHeight={56} />;
}
