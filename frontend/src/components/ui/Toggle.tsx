import { C, SHADOW } from '../../lib/design';

interface ToggleProps {
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
  width?: number;
  height?: number;
}

// === Переключатель-тумблер (on/off) ===
export function Toggle({ checked, onChange, disabled, width = 42, height = 25 }: ToggleProps) {
  const pad = 3;
  const thumb = height - pad * 2;
  return (
    <div
      onClick={() => !disabled && onChange(!checked)}
      style={{
        width, height, borderRadius: height, padding: pad,
        background: checked ? C.accent : C.track,
        display: 'flex', alignItems: 'center',
        transition: 'background .2s', flexShrink: 0,
        cursor: disabled ? 'default' : 'pointer', opacity: disabled ? 0.7 : 1,
      }}
    >
      <div style={{
        width: thumb, height: thumb, borderRadius: '50%',
        background: C.bgWhite, boxShadow: SHADOW.thumb,
        marginLeft: checked ? width - thumb - pad * 2 : 0,
        transition: 'margin .2s',
      }} />
    </div>
  );
}
