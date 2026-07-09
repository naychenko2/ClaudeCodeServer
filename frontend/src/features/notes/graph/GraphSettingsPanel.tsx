import { useState } from 'react';
import { C, FONT, R, SHADOW } from '../../../lib/design';
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
          <svg width={13} height={13} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12" /></svg>
        </button>
      </div>
      <div style={{ overflowY: 'auto', padding: '8px 10px 10px' }}>
        <GraphSettingsBody settings={settings} onChange={onChange} sources={sources} tags={tags} localMode={localMode} />
      </div>
    </div>
  );
}

function IconGear() {
  return (
    <svg width={15} height={15} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
    </svg>
  );
}

const gearBtn: React.CSSProperties = {
  display: 'flex', alignItems: 'center', justifyContent: 'center',
  width: 30, height: 30, background: C.bgPanel, border: `1px solid ${C.border}`,
  borderRadius: R.md, color: C.textMuted, cursor: 'pointer', padding: 0, zIndex: 5,
};
