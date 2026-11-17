// KM-F8: детский вариант композера (Composer variant="child"). Показывает только то,
// что нужно ребёнку: поле, отправку, «+» (сразу выбор файла), чип вложения, перетаскивание
// файла и тумблер «Читать ответы вслух». Выбора модели, effort, режимов, губы контекста,
// «/», персон и микрофона здесь нет по построению — отдельный компонент, а не россыпь
// флагов в 2800-строчном взрослом Composer (его разметка остаётся нетронутой).
// Текст и вложения живут в родителе (ChildLessonChat) и приходят теми же пропсами,
// что у взрослого композера: onSend, attachments, onRemoveAttachment, onAttachFiles, onAttach.

import { useRef, useState } from 'react';
import { ArrowUp, AudioLines, Bot, Plus, Square } from 'lucide-react';
import { C, COMPOSER_LIP, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Chip } from '../../components/ui/Chip';
import { IconButton } from '../../components/ui/IconButton';
import type { ComposerProps } from '../../components/Composer';

const basename = (p: string) => p.split('/').pop() ?? p;

type Props = Pick<ComposerProps,
  'sessionId' | 'onSend' | 'onStop' | 'onAttach' | 'isGenerating' | 'attachments' | 'leadingChips'
  | 'onRemoveAttachment' | 'onAttachFiles' | 'attachmentLabels' | 'isMobile' | 'voiceMode' | 'onToggleVoiceMode' | 'offline'>;

export function ChildComposer({
  onSend, onStop, onAttach, isGenerating, attachments, leadingChips, onRemoveAttachment,
  onAttachFiles, attachmentLabels, isMobile = false, voiceMode = false, onToggleVoiceMode, offline = false,
}: Props) {
  const [text, setText] = useState('');
  const [dragOver, setDragOver] = useState(false);
  const taRef = useRef<HTMLTextAreaElement>(null);
  // Тач-цель не меньше 40 (макет: «на сенсорных устройствах кнопки композера 40 вместо 34»)
  const btn = isMobile ? 40 : 34;
  const canSend = !offline && !isGenerating && (text.trim().length > 0 || attachments.length > 0);

  const submit = () => {
    if (!canSend) return;
    onSend(text.trim(), attachments);
    setText('');
    if (taRef.current) taRef.current.style.height = 'auto';
  };

  const onDrop = (e: React.DragEvent) => {
    if (!onAttachFiles) return;
    const files = Array.from(e.dataTransfer.files);
    setDragOver(false);
    if (files.length) { e.preventDefault(); onAttachFiles(files); }
  };
  const onDragOver = (e: React.DragEvent) => {
    if (!onAttachFiles) return;
    if (Array.from(e.dataTransfer.types).includes('Files')) { e.preventDefault(); setDragOver(true); }
  };

  return (
    <div
      data-child-composer=""
      onDrop={onDrop}
      onDragOver={onDragOver}
      onDragLeave={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDragOver(false); }}
    >
      <div style={{
        position: 'relative', display: 'flex', flexDirection: 'column', gap: SP.xs,
        background: C.bgCard, borderRadius: R.xl, boxShadow: SHADOW.card,
        border: `1px solid ${dragOver ? C.accent : C.border}`, padding: SP.sm,
      }}>
        {dragOver && (
          <div style={{
            position: 'absolute', inset: 0, borderRadius: R.xl, background: C.accentLight, color: C.accent,
            display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: FS.base, fontWeight: 600, zIndex: 1,
            pointerEvents: 'none',
          }}>
            Отпустите — прикрепим к сообщению
          </div>
        )}
        {(leadingChips || attachments.length > 0) && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
            {leadingChips}
            {attachments.map(p => (
              <Chip key={p} maxW={220} touch={isMobile} onRemove={() => onRemoveAttachment(p)}
                removeLabel={`Убрать вложение ${attachmentLabels?.[p] ?? basename(p)}`} title={attachmentLabels?.[p] ?? basename(p)}>
                {attachmentLabels?.[p] ?? basename(p)}
              </Chip>
            ))}
          </div>
        )}
        <div style={{ display: 'flex', alignItems: 'flex-end', gap: SP.xs }}>
          <textarea
            ref={taRef}
            autoComplete="off"
            rows={1}
            value={text}
            disabled={offline}
            aria-label="Сообщение помощнику"
            placeholder="Напиши задачу или вопрос"
            onChange={e => setText(e.target.value)}
            onInput={e => {
              const el = e.currentTarget;
              el.style.height = 'auto';
              el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
            }}
            onKeyDown={e => {
              if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); submit(); }
            }}
            style={{
              flex: 1, minWidth: 0, border: 'none', outline: 'none', resize: 'none', background: 'transparent',
              color: C.textPrimary, fontFamily: FONT.sans, fontSize: isMobile ? 16 : FS.lg, lineHeight: 1.5,
              minHeight: 34, maxHeight: 200, padding: isMobile ? '6px 8px' : '6px 4px', boxSizing: 'border-box',
            }}
          />
          {onToggleVoiceMode && (
            <IconButton
              size={isMobile ? 'lg' : 'md'}
              active={voiceMode}
              ariaLabel="Читать ответы вслух"
              title="Читать ответы вслух"
              onClick={() => { void onToggleVoiceMode(!voiceMode); }}
            >
              <AudioLines size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} aria-pressed={voiceMode} />
            </IconButton>
          )}
          {isGenerating ? (
            <button
              onClick={onStop} aria-label="Остановить" title="Остановить"
              style={{
                width: btn, height: btn, borderRadius: R.pill, border: 'none', cursor: 'pointer', flexShrink: 0,
                background: C.accent, color: C.onAccent, display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
            >
              <Square size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </button>
          ) : (
            <button
              onClick={submit} disabled={!canSend} aria-label="Отправить" title="Отправить"
              style={{
                width: btn, height: btn, borderRadius: R.pill, border: 'none', flexShrink: 0,
                cursor: canSend ? 'pointer' : 'default',
                background: canSend ? C.accent : C.bgPanel, color: canSend ? C.onAccent : C.textMuted,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}
            >
              <ArrowUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </button>
          )}
        </div>
      </div>
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: COMPOSER_LIP.row, padding: `${SP.xs}px ${SP.sm}px`,
        color: C.textMuted, fontSize: FS.sm,
      }}>
        <button
          onClick={onAttach} aria-label="Прикрепить файл" title="Прикрепить фото, скриншот или PDF"
          style={{
            width: isMobile ? 40 : 28, height: isMobile ? 40 : 28, borderRadius: R.pill, border: 'none',
            background: 'none', cursor: 'pointer', color: C.textMuted, flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}
        >
          <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </button>
        <Bot size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        <span>Помощник — программа. Подсказывает, а решаешь ты.</span>
      </div>
    </div>
  );
}
