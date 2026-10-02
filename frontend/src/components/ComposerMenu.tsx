import { useState, useRef, useEffect, useLayoutEffect, type ReactNode } from 'react';
import { ChevronDown, Check, ArrowRightLeft } from 'lucide-react';
import { C, R, FONT, FS, SHADOW, SP, Z } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

// Общая механика выпадающих меню полосы контролов композера (модель, усилие) —
// один в один как меню режимов прав: плашка «иконка + подпись + шеврон», а в списке
// строки «иконка + название + описание» с галочкой у активной.
//
// Вынесено сюда, чтобы пикеры не расходились по отступам и поведению: позиционирование
// (мобила — фиксировано во всю ширину, десктоп — над кнопкой), закрытие по клику вне
// и разметка строк живут в одном месте.

// Потолок высоты всплывашки, когда места над кнопкой хватает
const MENU_MAX_H = 420;

export interface ComposerMenuItem {
  value: string;
  label: string;
  description?: string;
  hint?: string;       // вторичный текст рядом с подписью (напр. текущая версия семейства модели)
  icon?: ReactNode;
  badge?: ReactNode;   // правый бейдж строки (напр. окно контекста модели)
}

export interface ComposerMenuGroup {
  key: string;
  label?: string;      // заголовок группы; не задан — группа без шапки
  note?: string;       // пояснение под шапкой (напр. предупреждение о переносе чата)
  // Отчеркнуть группу снизу: нужно группе-одиночке вне остальных (пункт «По умолчанию»
  // над провайдерами) — при скрытых заголовках она иначе сливается со следующим списком
  divider?: boolean;
  items: ComposerMenuItem[];
}

interface Props {
  value: string;
  // Список строк. Игнорируется, если задан children — тогда во всплывашке своё содержимое
  // (напр. ползунок усилия), а от меню берутся только плашка, позиционирование и клик-вне.
  groups?: ComposerMenuGroup[];
  children?: (close: () => void) => ReactNode;
  // Блоки над и под списком групп (напр. пометка о заморозке модели сверху и ползунок
  // усилия снизу у объединённой плашки «модель · усилие»). С children не сочетаются.
  header?: ReactNode;
  footer?: ReactNode;
  onChange?: (value: string) => void;
  triggerIcon?: ReactNode;
  triggerLabel: string;
  // Значок после подписи (напр. столбики уровня усилия); в compact-форме не показывается
  triggerSuffix?: ReactNode;
  title: string;               // тултип плашки
  isMobile?: boolean;
  minWidth?: number;           // ширина списка на десктопе
  maxTriggerWidth?: number;
  // Схлопнуть плашку до квадратной иконки (узкий экран): подпись и шеврон убираются,
  // значение остаётся в тултипе
  compact?: boolean;
}

export function ComposerMenu({
  value, groups = [], children, header, footer, onChange, triggerIcon, triggerLabel, triggerSuffix, title,
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

  // Низ мобильной шторки — по позиции кнопки. Меряем в layout-эффекте (ref в рендере
  // читать нельзя); setState с тем же числом ререндера не даёт.
  const [menuBottom, setMenuBottom] = useState(80);
  // Потолок высоты — место над кнопкой: меню раскрывается вверх, и на низком экране
  // (телефон боком) при 420 px его верх с шапкой уезжал за край окна недостижимо
  const [menuMaxH, setMenuMaxH] = useState(MENU_MAX_H);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- меряем каждый рендер, пока меню открыто: позиция кнопки плавает от высоты композера
  useLayoutEffect(() => {
    if (!open) return;
    const r = rootRef.current?.getBoundingClientRect();
    if (isMobile) setMenuBottom(r ? window.innerHeight - r.top + 6 : 80);
    setMenuMaxH(r ? Math.max(160, Math.min(MENU_MAX_H, r.top - 6 - SP.sm)) : MENU_MAX_H);
  });

  return (
    <div ref={rootRef} style={{ position: 'relative', flexShrink: 0 }}>
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        title={title}
        // Имя кнопки для скринридера — полный тултип: значки после подписи (столбики
        // усилия) aria-hidden, и без этого уровень был бы только в description
        aria-label={title}
        aria-expanded={open}
        // Фон только на наведении/открытии: полоса лежит на тени карточки композера,
        // и залитые плашки разрезали бы её пятнами
        onMouseEnter={e => { if (!open) e.currentTarget.style.background = C.accentLight; }}
        onMouseLeave={e => { if (!open) e.currentTarget.style.background = 'transparent'; }}
        style={compact ? {
          // Схлопнутый вид — иконка + шеврон без подписи: шеврон отличает список выбора
          // от обычной кнопки-действия, поэтому остаётся и в узкой полосе.
          // На мобиле тач-цель не меньше 40×40 (guidelines): через эту кнопку идут и
          // модель, и усилие
          height: isMobile ? 40 : 32, minWidth: isMobile ? 40 : undefined, padding: '0 6px',
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
        {!compact && triggerSuffix}
        <ChevronDown size={compact ? 10 : ICON_SIZE.xs} strokeWidth={ICON_STROKE}
          style={{ flexShrink: 0, opacity: 0.55, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
      </button>

      {open && (
        <div style={{
          // Мобила: во всю ширину над кнопкой; десктоп: absolute от кнопки.
          // Прижимаем к правому краю кнопки — пикеры стоят справа полосы, иначе список
          // уезжает за границу окна.
          ...(isMobile
            ? { position: 'fixed' as const, left: 16, right: 16, bottom: menuBottom }
            : { position: 'absolute' as const, bottom: 'calc(100% + 6px)', right: 0, minWidth }),
          maxWidth: 'calc(100vw - 32px)', maxHeight: menuMaxH, overflowY: 'auto',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, padding: 5, zIndex: Z.dropdown,
        }}>
          {!children && header}
          {children ? children(() => setOpen(false)) : groups.map(g => (
            <div key={g.key} style={g.divider ? {
              paddingBottom: SP.xs, marginBottom: SP.xs,
              borderBottom: `1px solid ${C.borderLight}`,
            } : undefined}>
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
                          flex: it.hint ? '0 1 auto' : 1, minWidth: 0, fontSize: 13, fontWeight: 600, color: C.textHeading,
                          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                        }}>
                          {it.label}
                        </span>
                        {it.hint && (
                          <span style={{
                            flex: 1, minWidth: 0, fontSize: FS.xs, color: C.textMuted,
                            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                          }}>
                            {it.hint}
                          </span>
                        )}
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
          {/* Подвал прилипает к низу: при длинном списке моделей ползунок усилия иначе
              уезжал бы под прокрутку, и его никто бы не нашёл. bottom -5 — паддинг меню */}
          {!children && footer && (
            <div style={{ position: 'sticky', bottom: -5, background: C.bgWhite, margin: '0 -5px -5px', padding: '0 5px 5px' }}>
              {footer}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
