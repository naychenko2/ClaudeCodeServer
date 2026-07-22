import { useState, useRef, useEffect, type ReactNode } from 'react';
import { ChevronDown, Check, ArrowRightLeft } from 'lucide-react';
import { C, R, FONT, SHADOW, Z } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

// Общая механика выпадающих меню полосы контролов композера (модель, усилие) —
// один в один как меню режимов прав: плашка «иконка + подпись + шеврон», а в списке
// строки «иконка + название + описание» с галочкой у активной.
//
// Вынесено сюда, чтобы пикеры не расходились по отступам и поведению: позиционирование
// (мобила — фиксировано во всю ширину, десктоп — над кнопкой), закрытие по клику вне
// и разметка строк живут в одном месте.

export interface ComposerMenuItem {
  value: string;
  label: string;
  description?: string;
  icon?: ReactNode;
  badge?: ReactNode;   // правый бейдж строки (напр. окно контекста модели)
}

export interface ComposerMenuGroup {
  key: string;
  label?: string;      // заголовок группы; не задан — группа без шапки
  note?: string;       // пояснение под шапкой (напр. предупреждение о переносе чата)
  items: ComposerMenuItem[];
}

interface Props {
  value: string;
  // Список строк. Игнорируется, если задан children — тогда во всплывашке своё содержимое
  // (напр. ползунок усилия), а от меню берутся только плашка, позиционирование и клик-вне.
  groups?: ComposerMenuGroup[];
  children?: (close: () => void) => ReactNode;
  onChange?: (value: string) => void;
  triggerIcon?: ReactNode;
  triggerLabel: string;
  title: string;               // тултип плашки
  isMobile?: boolean;
  minWidth?: number;           // ширина списка на десктопе
  maxTriggerWidth?: number;
  // Схлопнуть плашку до квадратной иконки (узкий экран): подпись и шеврон убираются,
  // значение остаётся в тултипе
  compact?: boolean;
}

export function ComposerMenu({
  value, groups = [], children, onChange, triggerIcon, triggerLabel, title,
  isMobile, minWidth = 300, maxTriggerWidth, compact,
}: Props) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  // Закрытие по клику вне (как у меню режима прав)
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0 }}>
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        title={title}
        // Фон только на наведении/открытии: полоса лежит на тени карточки композера,
        // и залитые плашки разрезали бы её пятнами
        onMouseEnter={e => { if (!open) e.currentTarget.style.background = C.accentLight; }}
        onMouseLeave={e => { if (!open) e.currentTarget.style.background = 'transparent'; }}
        style={compact ? {
          // Схлопнутый вид — иконка + шеврон без подписи: шеврон отличает список выбора
          // от обычной кнопки-действия, поэтому остаётся и в узкой полосе
          height: isMobile ? 36 : 32, padding: '0 6px',
          borderRadius: R.md, border: 'none',
          background: open ? C.bgSelected : 'transparent', color: C.textSecondary,
          cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
          gap: 3, flexShrink: 0, transition: 'background 0.15s',
        } : {
          height: isMobile ? 32 : 28, padding: isMobile ? '0 8px' : '0 10px', borderRadius: R.md, border: 'none',
          background: open ? C.bgSelected : 'transparent', color: C.textSecondary,
          fontSize: 12.5, fontWeight: 600, cursor: 'pointer', fontFamily: FONT.sans,
          display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0,
          maxWidth: maxTriggerWidth ?? (isMobile ? 130 : 190), overflow: 'hidden',
          transition: 'background 0.15s',
        }}
      >
        {triggerIcon}
        {/* В сжатом виде прячем только подпись — шеврон остаётся признаком «это выбор» */}
        {!compact && (
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
            {triggerLabel}
          </span>
        )}
        <ChevronDown size={compact ? 10 : ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ flexShrink: 0, opacity: 0.55, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
      </button>

      {open && (
        <div style={{
          // Мобила: во всю ширину над кнопкой; десктоп: absolute от кнопки.
          // Прижимаем к правому краю кнопки — пикеры стоят справа полосы, иначе список
          // уезжает за границу окна.
          ...(isMobile
            ? (() => { const r = rootRef.current?.getBoundingClientRect(); return { position: 'fixed' as const, left: 16, right: 16, bottom: r ? window.innerHeight - r.top + 6 : 80 }; })()
            : { position: 'absolute' as const, bottom: 'calc(100% + 6px)', right: 0, minWidth }),
          maxWidth: 'calc(100vw - 32px)', maxHeight: 420, overflowY: 'auto',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, padding: 5, zIndex: Z.dropdown,
        }}>
          {children ? children(() => setOpen(false)) : groups.map(g => (
            <div key={g.key}>
              {g.label && (
                <div style={{
                  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
                  textTransform: 'uppercase', letterSpacing: 0.4, padding: '7px 9px 3px',
                }}>
                  {g.label}
                </div>
              )}
              {g.note && (
                <div style={{
                  display: 'flex', alignItems: 'flex-start', gap: 6, margin: '0 4px 4px',
                  padding: '5px 8px', borderRadius: R.sm, background: C.bgPanel,
                  fontSize: 11, color: C.textMuted, lineHeight: 1.35, fontFamily: FONT.sans,
                }}>
                  <ArrowRightLeft size={11} strokeWidth={2.2} style={{ flexShrink: 0, marginTop: 2 }} />
                  <span>{g.note}</span>
                </div>
              )}
              {g.items.map(it => {
                const active = it.value === value;
                return (
                  <button
                    key={it.value}
                    type="button"
                    onClick={() => { setOpen(false); if (!active) onChange?.(it.value); }}
                    onMouseEnter={e => { if (!active) e.currentTarget.style.background = C.accentLight; }}
                    onMouseLeave={e => { if (!active) e.currentTarget.style.background = 'transparent'; }}
                    style={{
                      width: '100%', display: 'flex', alignItems: 'flex-start', gap: 9,
                      padding: isMobile ? '11px 11px' : '8px 9px', borderRadius: R.md, border: 'none',
                      background: active ? C.accentLight : 'transparent', cursor: 'pointer', textAlign: 'left',
                    }}
                  >
                    {it.icon && (
                      <span style={{ color: active ? C.accent : C.textMuted, display: 'flex', marginTop: 1, flexShrink: 0 }}>
                        {it.icon}
                      </span>
                    )}
                    <span style={{ flex: 1, minWidth: 0 }}>
                      <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                        <span style={{
                          flex: 1, minWidth: 0, fontSize: 13, fontWeight: 600, color: C.textHeading,
                          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                        }}>
                          {it.label}
                        </span>
                        {it.badge}
                      </span>
                      {it.description && (
                        <span style={{ display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 1, lineHeight: 1.35 }}>
                          {it.description}
                        </span>
                      )}
                    </span>
                    {active && (
                      <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.accent} style={{ flexShrink: 0, marginTop: 2 }} />
                    )}
                  </button>
                );
              })}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
