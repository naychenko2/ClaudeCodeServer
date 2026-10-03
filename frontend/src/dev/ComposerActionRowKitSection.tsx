import { useState } from 'react';
import { Island, IslandHeader } from '../components/ui';
import { C, FS, ISLAND, SP } from '../lib/design';
import { ComposerActionRow } from '../components/chat/ComposerActionRow';
import type { ContextAction } from '../lib/chatContext/types';

// Витрина строки чипов действий поля ввода (ADR-023 §Д2, макет composer-actions-v1): состояния из таблицы
// «Строки чипов по состояниям». Чипы живые — «Чат» и режимы переключаются, вопрос под чипами меняется;
// запуска здесь нет, подпись кнопки считает useActionRun.

const run = (id: string, label: string, over: Partial<ContextAction> = {}): ContextAction =>
  ({ id, kind: 'run', label, hint: `Запуск: ${label}`, op: id, ...over });
const editor = (id: string, label: string, hint: string): ContextAction => ({ id, kind: 'editor', label, hint, open: () => {} });
const menu = (id: string, label: string): ContextAction => ({
  id, kind: 'menu', label, hint: `${label}: откуда взять`,
  items: () => [{ id: 'proj', label: 'Из проекта', run: () => {} }, { id: 'off', label: 'Убрать кадр', disabledReason: 'кадр не задан', run: () => {} }],
});
const SET = {
  param: 'stemSet', title: 'Набор',
  options: [{ value: 'v', label: 'Вокал + минус' }, { value: '4', label: '4 стема' }, { value: '6', label: '6 стемов' }, { value: 'k', label: 'Караоке' }],
};

const STATES: { caption: string; actions: ContextAction[]; start: string | null }[] = [
  {
    caption: 'Картинка с файлом — потолок: «Чат» плюс пять действий', start: 'edit',
    actions: [run('edit', 'Изменить'), run('removeBg', 'Убрать фон'), run('upscale', 'Увеличить'),
      run('outpaint', 'Дорисовать', { question: { param: 'aspect', title: 'Пропорции', options: ['16:9', '9:16', '1:1', '4:3'].map(v => ({ value: v, label: v })) } }),
      editor('mark', 'Отметить', 'Открыть редактор на кисти — отмеченное пойдёт в «Изменить»')],
  },
  {
    caption: 'Песня — «Перегенерировать кусок» серый без выделения, у «Стемов» вопрос', start: 'stems',
    actions: [run('piece', 'Перегенерировать кусок', { disabledReason: 'Выделите кусок на волне в редакторе' }),
      run('stems', 'Стемы', { question: SET }), run('denoise', 'Убрать шум'), run('concat', 'Склеить')],
  },
  { caption: 'Сцена — кадры меню пунктиром', start: null, actions: [run('shoot', 'Снять'), menu('a', 'Кадр A'), menu('b', 'Кадр B')] },
];

function Demo({ caption, actions, start, isMobile }: { caption: string; actions: ContextAction[]; start: string | null; isMobile: boolean }) {
  const [sel, setSel] = useState<string | null>(start);
  const [ans, setAns] = useState('v');
  const q = actions.find(a => a.id === sel)?.question;
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, width: isMobile ? 342 : 640, maxWidth: '100%' }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>{caption}{isMobile ? ' · 360' : ''}</div>
      <div style={{ border: `1px solid ${C.border}`, borderRadius: 14, padding: SP.sm, background: C.bgCard }}>
        <ComposerActionRow actions={actions} selectedId={sel} onSelect={setSel} isMobile={isMobile}
          question={q ? { value: ans === 'v' ? q.options[0].value : ans, onChange: setAns } : null} />
      </div>
    </div>
  );
}

export function ComposerActionRowKitSection() {
  return (
    <Island>
      <IslandHeader title="Чипы действий поля ввода" />
      <div style={{ display: 'flex', flexDirection: 'column', gap: ISLAND.gap, padding: SP.md }}>
        {STATES.map(s => <Demo key={s.caption} {...s} isMobile={false} />)}
        <Demo {...STATES[0]} isMobile />
      </div>
    </Island>
  );
}
