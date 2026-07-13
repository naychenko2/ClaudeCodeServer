// Выбор срока: быстрые чипы (Сегодня/Завтра/+7 дней/Без срока) как в макете,
// плюс произвольная дата и время через мини-календарь в поповере.
// Поповер — portal с fixed-позиционированием: не обрезается модалами со скроллом.

import { useEffect, useState } from 'react';
import { ChevronLeft, ChevronRight, Clock } from 'lucide-react';
import { createPortal } from 'react-dom';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { addDaysIso, todayIso, toIsoDate } from '../../lib/tasks';
import { CalendarIcon } from './bits';

interface Props {
  dueDate: string | null;   // YYYY-MM-DD
  dueTime: string | null;   // HH:MM
  onChange: (dueDate: string | null, dueTime: string | null) => void;
}

const MONTHS = ['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'];
const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
const TIME_PRESETS = ['09:00', '12:00', '14:00', '16:00', '18:00', '20:00'];

function chipStyle(active: boolean): React.CSSProperties {
  return {
    display: 'inline-flex', alignItems: 'center', gap: 6,
    padding: '7px 13px', cursor: 'pointer',
    border: `1px solid ${active ? C.accent : C.border}`,
    borderRadius: R.lg,
    background: active ? C.accentLight : C.bgWhite,
    fontFamily: FONT.sans, fontSize: 13, fontWeight: active ? 600 : 500,
    color: active ? C.accent : C.textPrimary,
    transition: 'border-color 0.12s, background 0.12s',
    whiteSpace: 'nowrap',
  };
}

// Сетка дней месяца: недели по 7, начиная с понедельника
function monthGrid(year: number, month: number): (string | null)[] {
  const first = new Date(year, month, 1);
  const offset = (first.getDay() + 6) % 7;   // Пн = 0
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const cells: (string | null)[] = Array(offset).fill(null);
  for (let d = 1; d <= daysInMonth; d++)
    cells.push(toIsoDate(new Date(year, month, d)));
  while (cells.length % 7 !== 0) cells.push(null);
  return cells;
}

// Дата в подписи кастомного чипа: «18 июн» / «18 июн 2027»
function customLabel(iso: string): string {
  const [y, m, d] = iso.split('-').map(Number);
  const date = new Date(y, m - 1, d);
  const opts: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'short' };
  if (y !== new Date().getFullYear()) opts.year = 'numeric';
  return date.toLocaleDateString('ru-RU', opts).replace('.', '').replace(' г', '');
}

export function DueDatePicker({ dueDate, dueTime, onChange }: Props) {
  const today = todayIso();
  const tomorrow = addDaysIso(today, 1);
  const week = addDaysIso(today, 7);

  // Якорь открытого поповера (rect кнопки «Дата…») или null
  const [anchor, setAnchor] = useState<DOMRect | null>(null);

  // Позиция календаря в поповере: месяц выбранной даты или текущий
  const [viewYm, setViewYm] = useState<[number, number]>(() => {
    const base = dueDate ?? today;
    const [y, m] = base.split('-').map(Number);
    return [y, m - 1];
  });
  // Поле «своё время» показывает только кастомное значение: пресет (09:00 и т.п.)
  // в него не дублируется — активный пресет и так подсвечен своим чипом
  const customTime = dueTime && !TIME_PRESETS.includes(dueTime) ? dueTime : '';
  const [timeDraft, setTimeDraft] = useState(customTime);

  useEffect(() => { setTimeDraft(customTime); }, [customTime]);

  // Клик вне поповера — закрыть (portal: проверяем по data-атрибуту)
  useEffect(() => {
    if (!anchor) return;
    const handler = (e: PointerEvent) => {
      if (!(e.target as HTMLElement).closest('[data-due-popover]')) setAnchor(null);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setAnchor(null); };
    document.addEventListener('pointerdown', handler);
    window.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('pointerdown', handler);
      window.removeEventListener('keydown', onKey);
    };
  }, [anchor]);

  const quick = [
    { key: 'today', label: 'Сегодня', date: today },
    { key: 'tomorrow', label: 'Завтра', date: tomorrow },
    { key: 'week', label: '+7 дней', date: week },
  ];
  const isQuick = quick.some(q => q.date === dueDate);
  const isCustom = !!dueDate && !isQuick;

  const applyTime = (raw: string): string | null => {
    const m = raw.trim().match(/^(\d{1,2})[:.](\d{2})$/);
    if (!m) return null;
    const h = Number(m[1]), min = Number(m[2]);
    if (h > 23 || min > 59) return null;
    return `${String(h).padStart(2, '0')}:${String(min).padStart(2, '0')}`;
  };

  // Маска «чч:мм»: только цифры, двоеточие подставляется само, значения зажимаются
  // в валидный диапазон по мере ввода (9→09, часы ≤23, десятки минут ≤5)
  const maskTime = (raw: string): string => {
    let d = raw.replace(/\D/g, '').slice(0, 4);
    if (d.length >= 1 && Number(d[0]) > 2) d = '0' + d;              // «9…» → «09…»
    if (d.length >= 2 && Number(d.slice(0, 2)) > 23) d = '23' + d.slice(2);
    if (d.length >= 3 && Number(d[2]) > 5) d = d.slice(0, 2) + '5' + d.slice(3);
    d = d.slice(0, 4);
    return d.length <= 2 ? d : `${d.slice(0, 2)}:${d.slice(2)}`;
  };

  const [viewYear, viewMonth] = viewYm;
  const cells = monthGrid(viewYear, viewMonth);

  // Позиция поповера: под якорем; если снизу не помещается — над ним
  const POPOVER_W = 268;
  const POPOVER_H = 340;
  const popoverPos = (): React.CSSProperties => {
    if (!anchor) return {};
    const left = Math.max(12, Math.min(anchor.left, window.innerWidth - POPOVER_W - 12));
    const openUp = window.innerHeight - anchor.bottom < POPOVER_H + 20 && anchor.top > POPOVER_H;
    return openUp
      ? { left, top: anchor.top - 6, transform: 'translateY(-100%)' }
      : { left, top: anchor.bottom + 6 };
  };

  return (
    <div>
      {/* Дата: быстрые чипы + произвольная через календарик */}
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
        {quick.map(q => (
          <button key={q.key} onClick={() => onChange(q.date, dueTime)} style={chipStyle(dueDate === q.date)}>
            <CalendarIcon size={12} />
            {q.label}
          </button>
        ))}
        <button onClick={() => onChange(null, null)} style={chipStyle(!dueDate)}>
          Без срока
        </button>
        <button
          data-due-popover
          onClick={e => {
            // rect читаем синхронно: в асинхронном updater e.currentTarget уже null
            const rect = e.currentTarget.getBoundingClientRect();
            const base = dueDate ?? today;
            const [y, m] = base.split('-').map(Number);
            setViewYm([y, m - 1]);
            setAnchor(prev => prev ? null : rect);
          }}
          style={chipStyle(isCustom || !!anchor)}
        >
          <CalendarIcon size={12} />
          {isCustom ? customLabel(dueDate) : 'Дата…'}
        </button>
      </div>

      {/* Время — отдельно от даты: видно всегда, когда дата задана, без лишних кликов */}
      {dueDate && (
        <div style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 6, marginTop: 10 }}>
          <span style={{
            fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
            textTransform: 'uppercase', letterSpacing: '0.05em', marginRight: 2,
          }}>
            Время
          </span>
          {[null, ...TIME_PRESETS].map(t => {
            const active = (dueTime ?? null) === t;
            return (
              <button
                key={t ?? 'none'}
                onClick={() => onChange(dueDate, t)}
                style={{
                  padding: '4px 9px', cursor: 'pointer',
                  border: `1px solid ${active ? C.accent : C.border}`,
                  borderRadius: R.md,
                  background: active ? C.accentLight : C.bgWhite,
                  color: active ? C.accent : C.textSecondary,
                  fontFamily: t ? FONT.mono : FONT.sans, fontSize: 11.5,
                  fontWeight: active ? 700 : 500,
                }}
              >
                {t ?? 'Нет'}
              </button>
            );
          })}
          {/* Своё время: чип-поле с часами — пунктир намекает «введи значение»;
              при нестандартном времени подсвечен как активный выбор */}
          {(() => {
            const customActive = customTime !== '';
            return (
              <label style={{
                display: 'inline-flex', alignItems: 'center', gap: 5,
                padding: '3px 8px', cursor: 'text', boxSizing: 'border-box',
                border: customActive ? `1px solid ${C.accent}` : `1.5px dashed ${C.dashed}`,
                borderRadius: R.md,
                background: customActive ? C.accentLight : 'transparent',
              }}>
                <Clock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={customActive ? C.accent : C.textMuted} style={{ flexShrink: 0 }} />
                <input
                  value={timeDraft}
                  onChange={e => setTimeDraft(maskTime(e.target.value))}
                  onBlur={() => {
                    const t = applyTime(timeDraft);
                    if (t) onChange(dueDate, t);
                    else if (timeDraft.trim() === '' && customTime !== '') onChange(dueDate, null);
                    else setTimeDraft(customTime);
                  }}
                  placeholder="своё время"
                  inputMode="numeric"
                  maxLength={5}
                  size={9}
                  style={{
                    width: 74, border: 'none', outline: 'none', background: 'transparent',
                    fontFamily: FONT.mono, fontSize: 11.5,
                    fontWeight: customActive ? 700 : 400,
                    color: customActive ? C.accent : C.textPrimary,
                  }}
                />
              </label>
            );
          })()}
        </div>
      )}

      {anchor && createPortal(
        <div data-due-popover style={{
          position: 'fixed', zIndex: Z.modal + 1, ...popoverPos(),
          width: POPOVER_W, boxSizing: 'border-box',
          maxHeight: 'calc(100vh - 24px)', overflowY: 'auto',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          boxShadow: SHADOW.dropdown, padding: 14,
        }}>
          {/* Шапка: месяц + навигация */}
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 10 }}>
            <span style={{ fontFamily: FONT.sans, fontSize: 13.5, fontWeight: 700, color: C.textHeading }}>
              {MONTHS[viewMonth]} {viewYear}
            </span>
            <div style={{ display: 'flex', gap: 4 }}>
              {[-1, 1].map(dir => (
                <button
                  key={dir}
                  onClick={() => setViewYm(([y, m]) => {
                    const d = new Date(y, m + dir, 1);
                    return [d.getFullYear(), d.getMonth()];
                  })}
                  style={{
                    width: 26, height: 26, padding: 0, cursor: 'pointer',
                    border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgWhite,
                    color: C.textSecondary, display: 'flex', alignItems: 'center', justifyContent: 'center',
                  }}
                >
                  {dir < 0
                    ? <ChevronLeft size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                    : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                </button>
              ))}
            </div>
          </div>

          {/* Дни недели */}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', marginBottom: 4 }}>
            {WEEKDAYS.map(w => (
              <div key={w} style={{ textAlign: 'center', fontFamily: FONT.sans, fontSize: 10, fontWeight: 600, color: C.textMuted, padding: '2px 0' }}>
                {w}
              </div>
            ))}
          </div>

          {/* Сетка дней */}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: 2 }}>
            {cells.map((iso, i) => {
              if (!iso) return <div key={i} />;
              const day = Number(iso.split('-')[2]);
              const isSelected = iso === dueDate;
              const isToday = iso === today;
              return (
                <button
                  key={i}
                  // Выбор дня сразу закрывает календарик — время задаётся отдельной строкой
                  onClick={() => { onChange(iso, dueTime); setAnchor(null); }}
                  style={{
                    height: 30, padding: 0, cursor: 'pointer', borderRadius: R.md,
                    border: 'none',
                    background: isSelected ? C.accent : 'transparent',
                    color: isSelected ? C.onAccent : isToday ? C.accent : C.textPrimary,
                    fontFamily: FONT.sans, fontSize: 12.5, fontWeight: isSelected || isToday ? 700 : 400,
                  }}
                >
                  {day}
                </button>
              );
            })}
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}
