// «Текст сцены»: растёт по тексту, «Развернуть» — редактор на всю высоту тела (макет v7). Правка живёт
// в поле сразу и уходит в сцену с задержкой; пока человек в поле, чужие обновления его не затирают.

import { useEffect, useRef, useState } from 'react';
import { ArrowLeft, Maximize2 } from 'lucide-react';
import { Button, C, FS, SP, TextArea } from 'aihome_shell/kit';
import { TEXT_MAX } from '../scene/model';
import { Hint, ic, Label } from './primitives';

const SNIPPETS = ['Камера:', 'Свет:', 'Звук:', 'Реплика:'];

export function SceneText({ value, onChange, onExpand, draft }: {
  value: string; onChange: (v: string) => void; onExpand: () => void; draft: boolean;
}) {
  const [text, setText] = useState(value);
  const dirty = useRef(false);
  useEffect(() => { if (!dirty.current) setText(value); }, [value]);
  useEffect(() => { if (dirty.current && text === value) dirty.current = false; }, [text, value]);
  const edit = (v: string) => {
    const t = v.slice(0, TEXT_MAX);
    dirty.current = true;
    setText(t);
    onChange(t);
  };
  return (
    <div data-video-text="">
      <Label aside={`${text.length} / ${TEXT_MAX}`}>Текст сцены</Label>
      <TextArea value={text} onChange={edit} autoGrow minHeight={72} maxHeight={220}
        placeholder="Что происходит от кадра A к кадру B: движение камеры, свет, звук, реплики" />
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xxs }}>
        <Hint>{draft ? 'Черновик этой сцены: уйдёт модели при запуске и дождётся вас, если выбрать другую карточку' : 'Текст уходит модели вместе с кадрами. Просьба из поля ввода добавится к нему.'}</Hint>
        <span style={{ flex: 1 }} />
        <Button size="xs" variant="ghost" leftIcon={ic(Maximize2)} onClick={onExpand}>Развернуть</Button>
      </div>
    </div>
  );
}

// Развёрнутый редактор: на всю высоту тела; закреплённый низ панели остаётся
export function SceneTextExpanded({ value, onChange, onBack, draft }: {
  value: string; onChange: (v: string) => void; onBack: () => void; draft: boolean;
}) {
  const [text, setText] = useState(value);
  const area = useRef<HTMLDivElement>(null);
  const edit = (v: string) => { const t = v.slice(0, TEXT_MAX); setText(t); onChange(t); };
  const add = (snip: string) => {
    const sep = text && !text.endsWith('\n') ? '\n' : '';
    edit(`${text}${sep}${snip} `);
  };
  return (
    <div data-video-text-expanded="" ref={area} style={{ display: 'flex', flexDirection: 'column', minHeight: '100%', paddingTop: SP.sm }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Button size="xs" variant="ghost" leftIcon={ic(ArrowLeft)} onClick={onBack}>Все настройки</Button>
        <span style={{ flex: 1 }} />
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>{text.length} / {TEXT_MAX}</span>
      </div>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, margin: `${SP.sm}px 0` }}>
        {SNIPPETS.map(s => <Button key={s} size="xs" variant="secondary" onClick={() => add(s)}>{s}</Button>)}
      </div>
      <TextArea value={text} onChange={edit} autoGrow minHeight={260} autoFocus placeholder="Опишите сцену" />
      <Hint>{draft ? 'Правка хранится черновиком у этой сцены — следующий запуск возьмёт её, переход к другой карточке не потеряет' : 'Текст уходит модели вместе с кадрами'}</Hint>
    </div>
  );
}
