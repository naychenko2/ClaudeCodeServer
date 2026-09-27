// Диалог первого создания архитектуры — общая точка для панели и документа.
// Раньше это были два разрозненных места: кнопка «Собрать архитектуру» + отдельный тоггл
// «с агентом» жили в полноэкранном документе, а компактная панель сайдбара вообще не знала
// ни про пустой холст, ни про агента. Здесь оба пути — карточки одного выбора.
import type { ReactNode } from 'react';
import { Sparkles, Layers } from 'lucide-react';
import {
  C, FONT, FS, R, SP, Modal, useIsMobile, ICON_SIZE, ICON_STROKE,
} from 'aihome_shell/kit';
import { AgentToggle } from './ArchitectureBuild';

interface Props {
  withAgent: boolean;
  onWithAgentChange: (v: boolean) => void;
  agentBusy: boolean;
  agentHint: string | null;
  onClose: () => void;
  onGenerate: () => void;
  onBlank: () => void;
}

export function ArchitectureCreateDialog({
  withAgent, onWithAgentChange, agentBusy, agentHint, onClose, onGenerate, onBlank,
}: Props) {
  const isMobile = useIsMobile();
  return (
    <Modal width={440} title="Создать архитектуру" onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <OptionCard
          icon={<Sparkles size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
          title="Из кода проекта"
          subtitle="Просканирую системы, контейнеры и связи между ними."
          onClick={onGenerate}
        />
        <div style={{ paddingLeft: SP.md }}>
          <AgentToggle checked={withAgent && !agentBusy} onChange={onWithAgentChange} disabled={agentBusy} hint={agentHint} />
        </div>
        {/* Пальцем на 360px холст не правят (мобильный режим только просмотра — как у документа) */}
        {!isMobile && (
          <OptionCard
            icon={<Layers size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />}
            title="С пустого холста"
            subtitle="Ничего искать не буду — модель будет чистой, элементы добавите сами."
            onClick={onBlank}
          />
        )}
      </div>
    </Modal>
  );
}

function OptionCard({ icon, title, subtitle, onClick }: {
  icon: ReactNode; title: string; subtitle: string; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} style={{
      display: 'flex', flexDirection: 'column', gap: SP.xs, width: '100%', textAlign: 'left',
      padding: SP.md, borderRadius: R.lg, border: `1px solid ${C.border}`, background: C.bgWhite,
      cursor: 'pointer', fontFamily: FONT.sans,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <span style={{ color: C.textSecondary, flexShrink: 0, display: 'flex' }}>{icon}</span>
        <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>{title}</span>
      </div>
      <span style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.4 }}>{subtitle}</span>
    </button>
  );
}
