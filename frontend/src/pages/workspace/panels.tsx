// Контент панелек «Терминал» и «Preview» правой колонки нового интерфейса
// (workspace-cc-panels). Состояние терминалов/сервисов живёт в WorkspacePage —
// сюда приходит пропсами; эти компоненты только рисуют компактный вид под
// узкую колонку (280-560px): полоса чипов сверху + рабочая область.
import { useEffect, useState } from 'react';
import { Plus, X } from 'lucide-react';
import type { ProjectService } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { ICON_STROKE } from '../../components/ui/icons';
import { TerminalView } from '../../components/terminal/TerminalView';
import { PreviewServiceList, groupServices } from '../../components/tools/ToolsSidebar';
import type { TerminalInfo } from '../../lib/terminalSignalr';

// Чип в полосе вкладок панельки (терминалы, сервисы)
function ChipButton({ active, label, title, onClick, onClose, closeTitle, dot }: {
  active?: boolean;
  label: string;
  title?: string;
  onClick: () => void;
  onClose?: () => void;
  closeTitle?: string;
  dot?: string;
}) {
  const [hover, setHover] = useState(false);
  return (
    <span
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, maxWidth: 160,
        padding: '3px 7px', borderRadius: R.md, cursor: 'pointer', flexShrink: 0,
        border: `1px solid ${active ? C.accent : C.border}`,
        background: active ? C.accentMuted : hover ? C.bgSelected : 'transparent',
      }}
    >
      {dot && <span style={{ width: 6, height: 6, borderRadius: '50%', background: dot, flexShrink: 0 }} />}
      <span
        onClick={onClick}
        title={title ?? label}
        style={{
          fontFamily: FONT.sans, fontSize: 11.5, fontWeight: 600,
          color: active ? C.accent : C.textSecondary,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}
      >
        {label}
      </span>
      {onClose && (hover || active) && (
        <span onClick={e => { e.stopPropagation(); onClose(); }} title={closeTitle}
          style={{ display: 'flex', color: C.textMuted, cursor: 'pointer' }}>
          <X size={11} strokeWidth={ICON_STROKE} />
        </span>
      )}
    </span>
  );
}

// === Панелька «Терминал»: чипы терминалов + смонтированные xterm-ы ===
// Неактивные терминалы скрыты display:none (не размонтированы) — буфер и фон-вывод
// сохраняются, как в ToolsPaneView старого режима.
export function TerminalPanelContent({ terminals, activeTerminalId, onSelect, onCreate, onStop, onActivity }: {
  terminals: TerminalInfo[];
  activeTerminalId: string | null;
  onSelect: (id: string | null) => void;
  onCreate: () => void;
  onStop: (id: string) => void;
  onActivity: (busy: boolean) => void;
}) {
  // Активный не выбран (например после F5) — показываем первый существующий
  const effectiveId = terminals.some(t => t.id === activeTerminalId)
    ? activeTerminalId
    : terminals[0]?.id ?? null;
  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <div style={{
        flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4, flexWrap: 'wrap',
        padding: '6px 8px', borderBottom: `1px solid ${C.border}`,
      }}>
        {terminals.map(t => (
          <ChipButton key={t.id} active={t.id === effectiveId} label={t.name}
            onClick={() => onSelect(t.id)} onClose={() => onStop(t.id)} closeTitle="Остановить терминал" />
        ))}
        <button onClick={onCreate} title="Новый терминал" style={{
          display: 'flex', alignItems: 'center', justifyContent: 'center', width: 24, height: 24,
          border: 'none', borderRadius: R.sm, background: 'transparent', cursor: 'pointer', color: C.textMuted,
        }}>
          <Plus size={14} strokeWidth={ICON_STROKE} />
        </button>
      </div>
      <div style={{ flex: 1, minHeight: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {terminals.length > 0 ? (
          terminals.map(t => (
            <div key={t.id} style={{
              flex: 1, minHeight: 0, flexDirection: 'column',
              display: t.id === effectiveId ? 'flex' : 'none',
            }}>
              <TerminalView
                terminalId={t.id}
                visible={t.id === effectiveId}
                onActivity={t.id === effectiveId ? onActivity : undefined}
              />
            </div>
          ))
        ) : (
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 }}>
            Создайте терминал кнопкой «+»
          </div>
        )}
      </div>
    </div>
  );
}

// === Панелька «Preview»: ТОЛЬКО список dev-сервисов ===
// Тот же список, что во вкладке Preview старого режима (PreviewServiceList из
// ToolsSidebar): группировка по источникам, обновление, форма «Добавить свой…».
// Само окно превью живёт в центральной области воркспейса: клик по запущенному
// сервису открывает его там (повторный — закрывает), запуск кнопкой ▶ открывает сразу.
export function PreviewPanelContent({ projectId, services, activePreviewId, onSelect, onStart, onStop, onRefresh }: {
  projectId: string;
  services: ProjectService[];
  activePreviewId: string | null;
  onSelect: (id: string | null) => void;
  onStart: (svc: ProjectService) => void;
  onStop: (id: string) => void;
  onRefresh: () => void;
}) {
  // Список сервисов подгружается при открытии панельки (как вкладка preview в ToolsSidebar)
  useEffect(() => { onRefresh(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, []);
  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <PreviewServiceList
        projectId={projectId}
        groups={groupServices(services)}
        hasAny={services.length > 0}
        activePreviewId={activePreviewId}
        onRefreshServices={onRefresh}
        onStartService={svc => { onStart(svc); onSelect(svc.id); }}
        onStopService={onStop}
        onSelectPreview={id => onSelect(activePreviewId === id ? null : id)}
      />
    </div>
  );
}
