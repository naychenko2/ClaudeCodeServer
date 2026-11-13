// Настройка этапа (экран 3 редактора, F23): шторка 540px справа на десктопе и отдельный экран
// на мобиле. Оболочка — заголовок «Этап «Ревью» review» + «✕» (мобила: «‹» и «Готово»); без выбора —
// «Этап не выбран». Поля этапа — в WorkflowStageFields (F24).
// Состояний черновика не держит: правка идёт через onChange родителя.

import { useEffect } from 'react';
import { Workflow, X } from 'lucide-react';
import { C, FONT, FS, SHADOW, SP, Z } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, EmptyState, IconButton } from '../../components/ui';
import { useIsMobile } from '../../lib/breakpoints';
import type { Persona } from '../personas/types';
import type { SpecialtyCatalogEntry } from '../specialties/types';
import { STAGE_ROLE_LABEL } from './WorkflowStageRow';
import { StageFields } from './WorkflowStageFields';
import type { WorkflowStage } from './types';

export const SETTINGS_W = 540;

interface Props {
  // null — этап не выбран (пустая настройка).
  stage: WorkflowStage | null;
  // Все этапы шаблона и место выбранного: из них считаются id для «после» и дубли id.
  stages: WorkflowStage[];
  index: number;
  readOnly: boolean;
  specialties: SpecialtyCatalogEntry[];
  personas: Persona[];
  // Ключи MCP, уже выданные (проекту/персоне): шаблон может только сузить, не выдать новое.
  mcpChoices: string[];
  onChange: (next: WorkflowStage) => void;
  onRenameId: (newId: string) => void;
  onRemove: () => void;
  onClose: () => void;
}

export function WorkflowStageSettings({
  stage, stages, index, readOnly, specialties, personas, mcpChoices, onChange, onRenameId, onRemove, onClose,
}: Props): React.ReactElement {
  const isMobile = useIsMobile();
  const label = stage ? (STAGE_ROLE_LABEL[stage.role] ?? stage.role) : '';
  const nameShown = stage ? ((stage.title ?? '').trim() || label) : '';

  // Esc закрывает шторку (на мобиле — «Готово» и «‹»).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const shellStyle: React.CSSProperties = isMobile ? {
    position: 'fixed', inset: 0, zIndex: Z.modal, background: C.bgMain,
  } : {
    position: 'fixed', top: 0, right: 0, bottom: 0, width: SETTINGS_W, maxWidth: '100vw', zIndex: Z.overlay,
    background: C.bgWhite, borderLeft: `1px solid ${C.border}`, boxShadow: SHADOW.island,
  };

  return (
    <aside
      data-stage-settings=""
      aria-label={stage ? `Настройка этапа «${nameShown}»` : 'Настройка этапа'}
      style={{ ...shellStyle, display: 'flex', flexDirection: 'column', minWidth: 0 }}
    >
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px`, minHeight: 52,
        borderBottom: `1px solid ${C.borderLight}`, flexShrink: 0,
      }}>
        {isMobile && (
          <Button variant="ghost" size="sm" onClick={onClose} ariaLabel="Назад к этапам">‹</Button>
        )}
        <span style={{
          flex: 1, minWidth: 0, fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 600, color: C.textHeading,
          overflowWrap: 'anywhere',
        }}>
          {stage ? (
            <>
              Этап «{nameShown}»
              <span style={{ marginLeft: SP.sm, fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 400, color: C.textMuted }}>
                {stage.role}
              </span>
            </>
          ) : 'Настройка этапа'}
        </span>
        {isMobile ? (
          <Button variant="primary" size="sm" onClick={onClose}>Готово</Button>
        ) : (
          <IconButton title="Закрыть" ariaLabel="Закрыть настройку этапа" onClick={onClose}>
            <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}
      </div>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: SP.md }}>
        {stage ? (
          <StageFields
            stage={stage} stages={stages} index={index} readOnly={readOnly} specialties={specialties}
            personas={personas} mcpChoices={mcpChoices} onChange={onChange} onRenameId={onRenameId} onRemove={onRemove}
          />
        ) : (
          <EmptyState
            inline compact
            icon={<Workflow size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
            title="Этап не выбран"
            subtitle="Добавьте этап слева — здесь появятся исполнитель, промпт, модели, права и таймаут."
          />
        )}
      </div>
    </aside>
  );
}
