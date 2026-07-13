import { useState } from 'react';
import { Settings, X } from 'lucide-react';
import { C, FONT, R, SHADOW } from '../../../lib/design';
import { ICON_SIZE } from '../../../components/ui/icons';
import type { GraphSettings } from './graphSettings';
import { GraphSettingsBody } from './GraphSettingsBody';

// Плавающая панель настроек графа: кнопка-шестерёнка в углу разворачивается в
// поповер с телом настроек (GraphSettingsBody). Используется там, где нет
// внешнего сайдбара — локальный граф в карточке заметки и мобильный режим.
export function GraphSettingsPanel({ settings, onChange, sources, tags, localMode, defaultOpen }: {
  settings: GraphSettings;
  onChange: (updater: (s: GraphSettings) => GraphSettings) => void;
  sources: { key: string; label: string }[];
  tags: string[];
  localMode: boolean;
  defaultOpen?: boolean;
}) {
  const [open, setOpen] = useState(!!defaultOpen);

  if (!open) {
    return (
      <button onClick={() => setOpen(true)} title="Настройки графа" style={{ ...gearBtn, position: 'absolute', top: 10, left: 10 }}>
        <IconGear />
      </button>
    );
  }

  return (
    <div style={{
      position: 'absolute', top: 10, left: 10, width: 252, maxHeight: 'calc(100% - 20px)',
      display: 'flex', flexDirection: 'column',
      background: C.bgPanel, border: `1px solid ${C.border}`, borderRadius: R.xl,
      boxShadow: SHADOW.dropdown, fontFamily: FONT.sans, overflow: 'hidden', zIndex: 5,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 10px', borderBottom: `1px solid ${C.border}`, flex: 'none' }}>
        <span style={{ flex: 1, fontSize: 12, fontWeight: 600, color: C.textSecondary }}>Настройки графа</span>
        <button onClick={() => setOpen(false)} title="Свернуть" style={{ ...gearBtn, border: 'none', width: 22, height: 22 }}>
          <X size={ICON_SIZE.xs} strokeWidth={2} />
        </button>
      </div>
      <div style={{ overflowY: 'auto', padding: '8px 10px 10px' }}>
        <GraphSettingsBody settings={settings} onChange={onChange} sources={sources} tags={tags} localMode={localMode} />
      </div>
    </div>
  );
}

function IconGear() {
  return <Settings size={ICON_SIZE.sm} strokeWidth={2} />;
}

const gearBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'center',
  width: 30, height: 30, background: C.bgPanel, border: `1px solid ${C.border}`,
  borderRadius: R.md, color: C.textMuted, cursor: 'pointer', padding: 0, zIndex: 5,
};
