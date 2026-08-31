import type { CSSProperties } from 'react';
import { X } from 'lucide-react';
import { C, FONT, FS, R, WAVE_BAR_DELAYS } from '../../lib/design';
import { TOUCH_CALLOUT_GUARD } from '../../lib/pointer';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

// Ряд индикации записи голоса — [пульсирующая точка, mm:ss, «волна», ✕] — ровно тот,
// что композер рисует на месте своей textarea (Composer.inputArea). Карточки чата
// (уточняющий вопрос, доработка плана, ответ на эскалацию) прячут своё поле и ставят
// сюда этот ряд, поэтому он живёт одним компонентом: три копии разъехались бы.

// mm:ss с ведущим нулём — секундомер записи
export function fmtRecTime(s: number): string {
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

// Дорожка-«волна» (псевдо: SpeechRecognition не даёт амплитуду — анимируем полоски)
export function Waveform() {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 3, flex: 1, height: 22, overflow: 'hidden' }}>
      {WAVE_BAR_DELAYS.map((d, i) => (
        <span key={i} className="cc-wave-bar" style={{ height: 22, animationDelay: `${d}s` }} />
      ))}
    </div>
  );
}

// В композере рядом с ✕ (отменить) стоит ✓ (готово — вставить текст): там распознанное
// копится в буфере и до подтверждения в поле не попадает. Здесь пары нет ОСОЗНАННО:
// VoiceMicButton дописывает каждый распознанный кусок в поле сразу, «отменить» уже
// нечего — остаётся одно действие, «остановить». Вид кнопки при этом эталонный
// (cancelRecBtn композера), чтобы одно и то же действие выглядело одинаково.
export function VoiceRecordingRow({ seconds, onStop, isMobile, style }: {
  seconds: number;
  onStop: () => void;
  isMobile?: boolean;
  // Место, куда ряд встаёт, бывает разным (блок формы, ячейка flex-строки) —
  // раскладку задаёт хозяин места, вид ряда остаётся его собственным
  style?: CSSProperties;
}) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 10,
      minHeight: 44, padding: '8px 10px',
      borderRadius: R.md, border: `1px solid ${C.border}`,
      background: C.bgWhite,
      ...style,
    }}>
      <span style={{
        width: 9, height: 9, borderRadius: R.full,
        background: C.danger,
        animation: 'pulsedot 1s ease-in-out infinite',
        flexShrink: 0,
      }} />
      <span style={{
        fontSize: FS.base, color: C.dangerText, fontWeight: 600,
        fontFamily: FONT.mono, flexShrink: 0, minWidth: 34,
      }}>
        {fmtRecTime(seconds)}
      </span>
      <Waveform />
      <button
        type="button"
        onClick={onStop}
        onContextMenu={(e) => e.preventDefault()}
        title="Остановить запись"
        style={{
          ...TOUCH_CALLOUT_GUARD,
          width: isMobile ? 36 : 32, height: isMobile ? 36 : 32,
          borderRadius: R.pill, border: 'none',
          background: C.dangerBg, color: C.danger, cursor: 'pointer',
          display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
        }}
      >
        <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
      </button>
    </div>
  );
}
