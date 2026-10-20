import { useState, type ReactNode } from 'react';
import { C, R, SHADOW, FS } from '../../../lib/design';
import { ICON_SIZE } from '../../ui/icons';
import type { FeatureRun } from '../../../types/featureRun';
import { FeatureGlyph, type FeatureGlyph as FeatureGlyphKind } from './FeatureGlyphs';

// Выбор фичи, когда в чате их несколько (≥ 2): секция «Фичи чата · N» + горизонтальный
// ряд чипов. Каждый чип — глиф состояния (12 px, общий с пилюлей) + имя фичи; выбранная
// подсвечена. Точка `●` (C.accent) — у фичи, которую показывает пилюля (runs[0]).
// На мобиле (360 px) ряд — одна строка с горизонтальной прокруткой (скроллбар скрыт).
//
// Роль: role="radiogroup" (группа) + role="radio" (чип), aria-checked — выбранный.

// Состояние фичи для глифа: из полей бэкового агрегата. Приоритет:
// wait (ждёт) → stopped (упал) → closed (закрыта) → done (пройдено) → run (идёт).
function pickerGlyph(run: FeatureRun): FeatureGlyphKind {
  const att = run.attention;
  if (att && att.kind === 'wait') return 'wait';
  if (att && att.kind === 'stopped') return 'stopped';
  if (run.closed) return 'closed';
  if (run.ready) return 'done';
  return 'run';
}

// Подпись секции (как CellLabel/SectionLabel): 10 px, 700, uppercase, letter-spacing .06em.
function SectionLabel({ children }: { children: ReactNode }) {
  return (
    <div style={{
      fontSize: FS.xxs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
      color: C.textMuted, margin: '2px 0 4px',
    }}>
      {children}
    </div>
  );
}

// Один чип фичи: глиф состояния + имя (многоточие, title = полное название) + точка `●` (runs[0]).
function FeatureChip({ run, on, isPill, isMobile, inMenu, onPick }: {
  run: FeatureRun;
  on: boolean;
  isPill: boolean;
  isMobile: boolean;
  inMenu: boolean;
  onPick: () => void;
}) {
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const bg = on ? C.accentLight : (hovered ? C.bgSelected : C.bgCard);
  // r-1: в меню (десктоп, 380) чип — 160, чтобы два влезли в ряд (2×160 + gap 4 = 324 < 346);
  // на карточке и на мобиле — прежние 220 (карточка: три ряда, мобила: одна строка).
  const maxWidth = inMenu && !isMobile ? 160 : 220;
  return (
    <button
      role="radio"
      aria-checked={on}
      // r-1: tooltip — полное название (текст чипа обрезается многоточием).
      // п.32: epic nullable-терпимо — без названия tooltip/текст пустые.
      title={run.epic?.title}
      onClick={onPick}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 5, flexShrink: 0, maxWidth,
        border: `1px solid ${on ? C.accentMuted : C.borderLight}`,
        background: bg,
        color: on ? C.textHeading : C.textSecondary,
        borderRadius: R.md, padding: '2px 8px',
        // M-1: у <button> браузер не наследует font-family → без inherit чипы были Arial
        // среди Hanken Grotesk; ● унаследует сам.
        fontFamily: 'inherit',
        minHeight: isMobile ? 40 : 26, fontSize: FS.sm,
        cursor: 'pointer', fontWeight: on ? 600 : 400,
        boxShadow: focused ? SHADOW.focus : 'none',
      }}
    >
      <FeatureGlyph kind={pickerGlyph(run)} size={ICON_SIZE.xs} />
      <span style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}>
        {run.epic?.title}
      </span>
      {isPill && (
        <span
          title="Эту фичу показывает пилюля"
          style={{ fontSize: FS.xxs, fontWeight: 700, color: C.accent, flexShrink: 0 }}
        >●</span>
      )}
    </button>
  );
}

export function FeaturePicker({ runs, selected, onSelect, isMobile = false, inMenu = false }: {
  runs: FeatureRun[];
  selected: number;
  onSelect: (index: number) => void;
  isMobile?: boolean;
  inMenu?: boolean;
}) {
  // Пикер — только при ≥ 2 фичах; при одной фиче выбор бессмыслен.
  if (runs.length < 2) return null;
  // r-1: в меню (десктоп) потолок пикера — два ряда (2×26 чип + gap 4 + padding 2 = 58)
  // с прокруткой внутри; на карточке и на мобиле — без потолка (все чипы / одна строка).
  const menuDesktop = inMenu && !isMobile;
  return (
    <div>
      <SectionLabel>Фичи чата · {runs.length}</SectionLabel>
      <div
        role="radiogroup"
        aria-label="Фича"
        style={{
          display: 'flex', alignItems: 'center', gap: 4,
          // M-4: десктоп — перенос (все чипы видны); мобила — одна строка с прокруткой.
          flexWrap: isMobile ? 'nowrap' : 'wrap',
          overflowX: isMobile ? 'auto' : 'visible',
          // r-1: меню-десктоп — потолок два ряда + вертикальная прокрутка внутри.
          ...(menuDesktop ? { maxHeight: 58, overflowY: 'auto' } : {}),
          scrollbarWidth: 'none',
          paddingBottom: 2, marginBottom: 2,
        }}
      >
        {runs.map((run, i) => (
          <FeatureChip
            key={run.featureId}
            run={run}
            on={i === selected}
            isPill={i === 0}
            isMobile={isMobile}
            inMenu={inMenu}
            onPick={() => onSelect(i)}
          />
        ))}
      </div>
    </div>
  );
}
