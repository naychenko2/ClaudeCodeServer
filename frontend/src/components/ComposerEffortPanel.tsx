import { useRef } from 'react';
import { Gauge, CircleHelp } from 'lucide-react';
import { EFFORTS, effortLabel } from '../lib/effort';
import { C, R, FONT } from '../lib/design';

// Усилие рассуждения (--effort): ползунок «Быстрее ↔ Умнее» со ступенями. Отдельной
// плашки у усилия нет — панель живёт подвалом меню модели (ComposerModelPicker), потому
// что без модели усилие смысла не имеет. Показывается только у провайдеров с
// supportsEffort — гейт на стороне родителя.
//
// Ступени ползунка — только реальные уровни (низкий…максимум). «По умолчанию» на эту
// шкалу не ложится (это «флаг не передаём, уровень выбирает CLI»), поэтому оно вынесено
// отдельной кнопкой сброса под ползунком, а не притворяется нулевой ступенью.
interface Props {
  value?: string | null;
  onChange: (effort: string) => void;
}

const LEVELS = EFFORTS.filter(e => e.value !== '');

// Уровень усилия картинкой для плашки модели: столбики по нарастанию, закрашено столько,
// какова ступень. Слово «Очень высокий» съедало до 90 px полосы, столбики — 19 (с зазором 25).
// Незакрашенные — тем же цветом, но бледно: токен рамки на фоне открытой плашки
// пропадал, и шкала читалась как «один-два столбика», а не «два из пяти».
// На «По умолчанию» не рисуем ничего: это отсутствие настройки, а не уровень
export function EffortBars({ value }: { value?: string | null }) {
  const idx = LEVELS.findIndex(l => l.value === value);
  if (idx < 0) return null;
  return (
    <span aria-hidden style={{ display: 'inline-flex', alignItems: 'flex-end', gap: 1, height: 12, flexShrink: 0 }}>
      {LEVELS.map((l, i) => (
        <span key={l.value} style={{
          width: 3, height: 4 + i * 2, borderRadius: 1,
          background: 'currentColor', opacity: i <= idx ? 1 : 0.25,
        }} />
      ))}
    </span>
  );
}

const HELP = 'Сколько модель рассуждает перед ответом. Выше уровень — глубже разбор, '
  + 'но ход дольше и дороже. «По умолчанию» — флаг не передаётся, уровень выбирает сам CLI.';

// Ползунок ступеней. Нативный input[type=range] — ради клавиатуры, драга и доступности;
// трек и точки ступеней рисуем слоем под ним (у самого инпута трек прозрачный).
function EffortSlider({ value, onPick }: { value: string; onPick: (v: string) => void }) {
  const idx = LEVELS.findIndex(l => l.value === value);
  const hasLevel = idx >= 0;
  const last = LEVELS.length - 1;
  // Был ли onChange в текущем нажатии (см. onClick ниже)
  const changedRef = useRef(false);
  // Центр бегунка ходит от 11px до (100% - 11px) — точки ставим по той же формуле,
  // иначе они разъедутся с реальными позициями ступеней
  const dotLeft = (i: number) => `calc(11px + (100% - 22px) * ${i / last})`;

  return (
    <div style={{ position: 'relative', height: 28, margin: '2px 0 4px' }}>
      {/* Трек-пилюля */}
      <div style={{
        position: 'absolute', inset: 0, borderRadius: R.pill, background: C.bgSelected,
      }} />
      {/* Точки ступеней; последняя — accent, как ориентир «предел шкалы» */}
      {LEVELS.map((l, i) => (
        <span
          key={l.value}
          style={{
            position: 'absolute', top: '50%', left: dotLeft(i), transform: 'translate(-50%, -50%)',
            width: 4, height: 4, borderRadius: '50%',
            background: i === last ? C.accent : C.textMuted,
            opacity: i === last ? 0.9 : 0.45,
          }}
        />
      ))}
      <input
        type="range"
        className={`cc-effort-range${hasLevel ? '' : ' cc-effort-range--empty'}`}
        min={0}
        max={last}
        step={1}
        value={hasLevel ? idx : 0}
        aria-label="Усилие рассуждения"
        aria-valuetext={hasLevel ? LEVELS[idx].label : 'По умолчанию'}
        onChange={e => { changedRef.current = true; onPick(LEVELS[Number(e.target.value)].value); }}
        // На «По умолчанию» невидимый бегунок уже стоит на первой ступени: клик по ней
        // или «←» значение не меняют, и onChange молчит — «Низкий» было не выбрать.
        // Ловим эти два входа отдельно. По DOM-значению в onClick судить нельзя: у
        // контролируемого инпута оно откатывается к 0 до ответа родителя, и клик по
        // «Максимуму» превращался бы в «Низкий» — поэтому смотрим, был ли onChange
        onPointerDown={() => { changedRef.current = false; }}
        onClick={() => { if (!hasLevel && !changedRef.current) onPick(LEVELS[0].value); }}
        onKeyDown={e => {
          if (!hasLevel && ['ArrowLeft', 'ArrowDown', 'Home', 'PageDown'].includes(e.key)) {
            e.preventDefault();
            onPick(LEVELS[0].value);
          }
        }}
        style={{ position: 'relative' }}
      />
    </div>
  );
}

export function ComposerEffortPanel({ value, onChange }: Props) {
  // Как и у модели: в сессии «по умолчанию» — null, в каталоге — ''
  const current = value ?? '';
  const isDefault = current === '';

  return (
    <div style={{
      padding: '9px 11px 10px', fontFamily: FONT.sans,
      // Отчерк от списка моделей над панелью
      marginTop: 4, borderTop: `1px solid ${C.borderLight}`,
    }}>
      {/* Шапка: подпись, текущее значение и подсказка */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginBottom: 10 }}>
        <span style={{ fontSize: 13, color: C.textMuted }}>Усилие</span>
        <span style={{ fontSize: 13, fontWeight: 700, color: C.textHeading, flex: 1, minWidth: 0 }}>
          {effortLabel(current)}
        </span>
        <span title={HELP} style={{ display: 'flex', color: C.textMuted, cursor: 'help', flexShrink: 0 }}>
          <CircleHelp size={14} strokeWidth={2} />
        </span>
      </div>

      {/* Полюса шкалы */}
      <div style={{
        display: 'flex', justifyContent: 'space-between',
        fontSize: 11.5, color: C.textMuted, marginBottom: 3,
      }}>
        <span>Быстрее</span>
        <span>Умнее</span>
      </div>

      <EffortSlider value={current} onPick={onChange} />

      {/* «По умолчанию» — не ступень шкалы, а отдельное состояние (флаг не передаём) */}
      <button
        type="button"
        onClick={() => { if (!isDefault) onChange(''); }}
        style={{
          width: '100%', marginTop: 6, padding: '6px 9px', borderRadius: R.md,
          border: `1px solid ${isDefault ? C.accent : C.border}`,
          background: isDefault ? C.accentLight : 'transparent',
          color: isDefault ? C.accent : C.textSecondary,
          fontSize: 12, fontWeight: 600, cursor: isDefault ? 'default' : 'pointer',
          fontFamily: FONT.sans, display: 'flex', alignItems: 'center', gap: 6,
        }}
      >
        <Gauge size={13} strokeWidth={2} style={{ flexShrink: 0 }} />
        По умолчанию
      </button>
    </div>
  );
}
