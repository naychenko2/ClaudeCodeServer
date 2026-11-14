// Строка этапа в ленте шаблона (F20/F23): ручка ⠿, колонка, имя + роль, «кто», «после», тег присутствия.
// Клик по строке выбирает этап — настройка открывается в WorkflowStageSettings (шторка/экран),
// а не раскрывается здесь. Состояний не держит: выбор и черновик — у родителя.

import { GripVertical, Lock } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Badge } from '../../components/ui';
import { presenceEditable } from './workflowEditorLogic';
import { presenceTag } from './workflowStripModel';
import type { WorkflowStage } from './types';

// Подписи ролей этапа (ключи — TaskRoles, разрез §0.3).
export const STAGE_ROLE_LABEL: Record<string, string> = {
  cut: 'Разрез', design: 'Дизайн', plan: 'План', step: 'Шаг', review: 'Ревью',
  qa: 'Проверка', final: 'Финал', merge: 'Слияние',
};

const LANE_OPTIONS = [
  { value: 'back', label: 'Бэк' },
  { value: 'front', label: 'Фронт' },
  { value: 'other', label: 'Другое' },
];

export const NON_SKIPPABLE_HINT = 'Несъёмный этап: без него маршрут не закрыть — его нельзя выключить или сделать необязательным.';

interface Props {
  stage: WorkflowStage;
  index: number;
  // Ошибки валидатора сервера и подсказки модели по этому этапу (показываются под заголовком).
  errors: string[];
  readOnly: boolean;
  // Лента (F20): номер колонки (null — выключен), «после» именами, строка «кто», ручка ⠿.
  column?: number | null;
  afterText?: string[];
  who?: string | null;
  grip?: { onPointerDown: (e: React.PointerEvent) => void; onKeyDown: (e: React.KeyboardEvent) => void };
  dragging?: boolean;
  dropHere?: boolean;
  // F23: строка выбрана (рамка accent + focus-тень) и клик по ней.
  selected?: boolean;
  onSelect?: () => void;
}

export function WorkflowStageRow({
  stage, index, errors, readOnly, column = index + 1, afterText = [], who = null, grip,
  dragging = false, dropHere = false, selected = false, onSelect,
}: Props): React.ReactElement {
  const fixed = !presenceEditable(stage.role);
  const label = STAGE_ROLE_LABEL[stage.role] ?? stage.role;
  // Имя этапа для показа (тот же порядок, что nodeTitle в FeatureProcessGraph): имя из шаблона,
  // фолбэк — подпись роли. Роль и id остаются подписью строки: с именем этапы одной роли
  // различимы, без него — как раньше (id, когда он отличается от роли).
  const nameShown = (stage.title ?? '').trim() || label;
  const stageId = stage.id?.trim() || stage.role;
  const tag = presenceTag(stage.presence);
  const isOff = stage.presence === 'off';
  const laneText = stage.role === 'step' && stage.lane ? (LANE_OPTIONS.find(l => l.value === stage.lane)?.label ?? stage.lane) : null;

  return (
    <div
      data-stage-index={index}
      style={{
        border: `1px ${isOff ? 'dashed' : 'solid'} ${errors.length ? C.dangerBorder : selected || dropHere ? C.accent : isOff ? C.dashed : C.border}`,
        borderRadius: R.xl, background: isOff ? 'transparent' : C.bgWhite, overflow: 'hidden',
        boxShadow: selected ? SHADOW.focus : 'none',
        opacity: dragging ? 0.5 : 1,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: `0 ${SP.md}px 0 ${SP.xs}px` }}>
        {!readOnly && (
          <button
            type="button"
            data-stage-grip=""
            aria-label={`Переставить этап «${nameShown}»: стрелки вверх и вниз или перетащите`}
            title="Перетащите (на сенсорном — долгое нажатие) или стрелками вверх/вниз"
            onPointerDown={grip?.onPointerDown}
            onKeyDown={grip?.onKeyDown}
            style={{
              flexShrink: 0, width: 44, minHeight: 44, display: 'flex', alignItems: 'center', justifyContent: 'center',
              background: 'none', border: 'none', cursor: 'grab', color: C.textMuted, touchAction: 'pan-y',
            }}
          >
            <GripVertical size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </button>
        )}
        <span
          aria-label={column === null ? 'Нет в маршруте' : `Колонка ${column}`}
          style={{
            flexShrink: 0, minWidth: 22, textAlign: 'center', fontFamily: FONT.mono, fontSize: FS.sm,
            fontWeight: 600, color: column === null ? C.textMuted : C.textSecondary,
          }}
        >{column === null ? '—' : column}</span>
        <button
          type="button"
          onClick={onSelect}
          aria-pressed={selected}
          aria-label={`Этап «${nameShown}»`}
          style={{
            flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: SP.sm, textAlign: 'left',
            padding: `${SP.sm}px 0`, minHeight: 44, background: 'none', border: 'none', cursor: 'pointer',
            fontFamily: FONT.sans, color: C.textHeading,
          }}
        >
          <span style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
            <span style={{ fontSize: FS.base, fontWeight: 600 }}>
              {nameShown}
              {laneText && (
                <span style={{ marginLeft: SP.sm, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary }}>{laneText}</span>
              )}
              <span style={{ marginLeft: SP.sm, fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 400, color: C.textMuted }}>
                {stage.role}{stageId !== stage.role ? ` · ${stageId}` : ''}
              </span>
            </span>
            <span style={{ fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {isOff ? 'нет в этом шаблоне' : who}
            </span>
            {!isOff && afterText.length > 0 && (
              <span style={{ fontSize: FS.xs, color: C.textMuted }}>после: {afterText.join(', ')}</span>
            )}
          </span>
        </button>
        {fixed && (
          <span title={NON_SKIPPABLE_HINT} aria-label="Несъёмный этап" style={{ display: 'flex', color: C.textMuted }}>
            <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </span>
        )}
        <Badge tone={tag.tone} size="xs">{tag.text}</Badge>
        {errors.length > 0 && <Badge tone="danger" size="xs">{errors.length}</Badge>}
      </div>

      {errors.length > 0 && (
        <div role="alert" style={{
          padding: `0 ${SP.md}px ${SP.sm}px`, fontSize: FS.xs, color: C.dangerText, lineHeight: 1.5,
        }}>
          {errors.map((e, i) => <div key={i}>{e}</div>)}
        </div>
      )}
    </div>
  );
}
