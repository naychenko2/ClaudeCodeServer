import { C, R, SHADOW } from '../../../lib/design';

interface Props {
  enabled: boolean;
  onChange: (v: boolean) => void;
}

export function SyncToggleRow({ enabled, onChange }: Props) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12,
      padding: '12px 14px', background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
    }}>
      <div>
        <div style={{ fontSize: 14, fontWeight: 600, color: C.textHeading }}>Синхронизировать весь проект</div>
        <div style={{ fontSize: 12, color: C.textMuted, marginTop: 2 }}>
          {enabled ? 'Файлы будут скачаны для офлайн-доступа' : 'Скачать все файлы проекта для офлайна'}
        </div>
      </div>
      <button
        onClick={() => onChange(!enabled)}
        style={{
          position: 'relative', width: 44, height: 26, borderRadius: 999, border: 'none',
          cursor: 'pointer', flexShrink: 0,
          background: enabled ? C.accent : C.track, transition: 'background 0.15s',
        }}
      >
        <span style={{ position: 'absolute', top: 3, left: enabled ? 21 : 3, width: 20, height: 20, borderRadius: '50%', background: C.bgWhite, transition: 'left 0.15s', boxShadow: SHADOW.thumb }} />
      </button>
    </div>
  );
}
