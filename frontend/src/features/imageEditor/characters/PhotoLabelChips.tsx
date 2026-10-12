// Метки фото лица (ADR-027 §1, спека character-panel-final-spec §8.1.2 / §10.5).
// Изолированный компонент: подпись под фото (режим подписи) и карточка-редактор под сеткой
// (режим редактора). Только фото лица; фото для обучения меток не имеют (ADR-026).
// Логика текста и состояния — чистая функция labelCaption (тесты + мутация в PhotoLabelChips.test.ts).

import { type ReactNode } from 'react';
import { Pencil, Sparkles, Trash2, X } from 'lucide-react';
import { Button, Field, IconButton, InlineSegmented, Select, C, FS, R, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { LABELS } from './characterTexts';

// Состояние меток одного фото: зеркалит поля меток ImageEditCharacterPhoto.
// null — поле не задано; 'unknown' — модель ответила, но метки нет.
export interface PhotoLabelState {
  angle?: string | null;
  pitch?: string | null;
  emotion?: string | null;
  labelSource?: string | null;
}

// Чистая функция: подпись под фото по состоянию меток.
//  - ни одной метки и labelSource=null → «Размечаем…» (мuted, курсив): разметка ещё не пробовалась;
//  - есть реальные метки → «{ракурс} · {эмоция}»; цвет по источнику (human — primary, иначе — secondary);
//  - меток нет (unknown / не определены) → «Без меток» (muted).
// Наклон в подписи не показываем — он виден в редакторе.
export function labelCaption(label: PhotoLabelState): { text: string; color: string; italic: boolean } {
  const attempted = label.labelSource != null || label.angle != null || label.pitch != null || label.emotion != null;
  if (!attempted) return { text: LABELS.pending, color: C.textMuted, italic: true };

  const parts: string[] = [];
  if (label.angle && label.angle !== 'unknown') parts.push(LABELS.captionAngle[label.angle] ?? label.angle);
  if (label.emotion && label.emotion !== 'unknown') parts.push(LABELS.captionEmotion[label.emotion] ?? label.emotion);
  if (parts.length > 0) {
    return { text: parts.join(' · '), color: label.labelSource === 'human' ? C.textPrimary : C.textSecondary, italic: false };
  }
  return { text: LABELS.none, color: C.textMuted, italic: false };
}

// Источник разметки: иконка + подпись. none — без иконки.
function sourceLine(label: PhotoLabelState): ReactNode {
  const attempted = label.labelSource != null || label.angle != null || label.pitch != null || label.emotion != null;
  if (!attempted) {
    return <span style={{ fontSize: FS.xs, color: C.textMuted }}>{LABELS.srcPending}</span>;
  }
  if (label.labelSource === 'human') {
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, fontSize: FS.xs, color: C.textMuted }}>
        <Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />{LABELS.srcHuman}
      </span>
    );
  }
  if (label.labelSource === 'auto') {
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xxs, fontSize: FS.xs, color: C.textMuted }}>
        <Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />{LABELS.srcAuto}
      </span>
    );
  }
  return <span style={{ fontSize: FS.xs, color: C.textMuted }}>{LABELS.srcNone}</span>;
}

// Подпись под фото (режим подписи): FS.xs, не больше 2 строк.
export function PhotoLabelCaption({ label }: { label: PhotoLabelState }) {
  const cap = labelCaption(label);
  return (
    <div style={{
      fontSize: FS.xs, lineHeight: 1.3, overflowWrap: 'anywhere', color: cap.color,
      fontStyle: cap.italic ? 'italic' : undefined,
      display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
    }}>{cap.text}</div>
  );
}

// Карточка-редактор меток (режим редактора): открывается по нажатию на фото, стоит под сеткой.
// Сохранение — сразу при каждом изменении (onSave); источник становится human.
export function PhotoLabelEditor({ index, label, busy, saveError, onSave, onClose, onRemove }: {
  // Порядковый номер фото в сетке (для шапки «Фото N»)
  index: number;
  label: PhotoLabelState;
  // Идёт сохранение — поля disabled
  busy?: boolean;
  // Ошибка последнего сохранения
  saveError?: string | null;
  onSave: (next: { angle?: string | null; pitch?: string | null; emotion?: string | null }) => void;
  onClose: () => void;
  onRemove?: () => void;
}) {
  const pending = label.labelSource == null && label.angle == null && label.pitch == null && label.emotion == null;
  const disabled = !!busy || pending;

  const set = (field: 'angle' | 'pitch' | 'emotion', value: string | null) => {
    onSave({ [field]: value });
  };

  return (
    <div style={{
      background: C.accentLight, border: `1px solid ${C.accentMuted}`, borderRadius: R.lg,
      padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm }}>
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Фото {index + 1}</span>
        <IconButton size="sm" variant="media" ariaLabel={LABELS.close} title={LABELS.close} onClick={onClose}>
          <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
      <Field label={LABELS.angle}>
        <InlineSegmented
          value={(label.angle && label.angle !== 'unknown' ? label.angle : null) as 'front' | 'three-quarter' | 'profile' | null}
          options={LABELS.angleOptions as unknown as { value: 'front' | 'three-quarter' | 'profile'; label: string }[]}
          onChange={v => set('angle', v)}
          disabled={disabled}
        />
      </Field>
      <Field label={LABELS.pitch}>
        <InlineSegmented
          value={(label.pitch && label.pitch !== 'unknown' ? label.pitch : null) as 'level' | 'up' | 'down' | null}
          options={LABELS.pitchOptions as unknown as { value: 'level' | 'up' | 'down'; label: string }[]}
          onChange={v => set('pitch', v)}
          disabled={disabled}
        />
      </Field>
      <Field label={LABELS.emotion}>
        <Select
          value={(label.emotion && label.emotion !== 'unknown' ? label.emotion : '') as string}
          options={LABELS.emotionOptions as unknown as { value: string; label: string }[]}
          placeholder="Не задано"
          onChange={v => set('emotion', v === '' ? null : v)}
          disabled={disabled}
        />
      </Field>
      {sourceLine(label)}
      {saveError && <span style={{ fontSize: FS.xs, color: C.dangerText, overflowWrap: 'anywhere' }}>{saveError}</span>}
      {onRemove && (
        <Button variant="ghost" size="sm" leftIcon={<Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={onRemove} style={{ alignSelf: 'flex-start' }}>
          Убрать фото
        </Button>
      )}
    </div>
  );
}
