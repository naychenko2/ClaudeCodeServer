// Выбор срока: быстрые чипы (Сегодня/Завтра/+7 дней/Без срока) как в макете,
// плюс произвольная дата и время через мини-календарь в поповере.
// Поповер — portal с fixed-позиционированием: не обрезается модалами со скроллом.

import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { C, FONT, R, SHADOW, Z } from '../../lib/design';
import { addDaysIso, todayIso, toIsoDate } from '../../lib/tasks';
import { CalendarIcon } from './bits';

interface Props {
  dueDate: string | null;   // YYYY-MM-DD
  dueTime: string | null;   // HH:MM
  onChange: (dueDate: string | null, dueTime: string | null) => void;
}

const MONTHS = ['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'];
const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

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
  const [timeDraft, setTimeDraft] = useState(dueTime ?? '');

  useEffect(() => { setTimeDraft(dueTime ?? ''); }, [dueTime]);

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

  const [viewYear, viewMonth] = viewYm;
  const cells = monthGrid(viewYear, viewMonth);

  // Позиция поповера: под якорем; если снизу не помещается — над ним
  const POPOVER_W = 268;
  const POPOVER_H = 430;
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
        {/* Произвольная дата/время */}
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
          {dueTime && (isCustom || isQuick) ? ` · ${dueTime}` : ''}
        </button>
      </div>

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
                  <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
                    {dir < 0 ? <path d="M15 18l-6-6 6-6" /> : <path d="M9 6l6 6-6 6" />}
                  </svg>
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
                  onClick={() => onChange(iso, dueTime)}
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

          {/* Время: пресеты одним тапом + произвольный ввод */}
          <div style={{ marginTop: 12, paddingTop: 12, borderTop: `1px solid ${C.borderLight}` }}>
            <div style={{
              fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
              textTransform: 'uppercase', letterSpacing: '0.05em', marginBottom: 7,
            }}>
              Время
            </div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5, marginBottom: 9 }}>
              {[null, '09:00', '12:00', '14:00', '16:00', '18:00', '20:00'].map(t => {
                const active = (dueTime ?? null) === t;
                return (
                  <button
                    key={t ?? 'none'}
                    onClick={() => onChange(t ? (dueDate ?? today) : dueDate, t)}
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
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <input
                value={timeDraft}
                onChange={e => setTimeDraft(e.target.value)}
                onBlur={() => {
                  const t = applyTime(timeDraft);
                  if (t) onChange(dueDate ?? today, t);
                  else if (timeDraft.trim() === '') onChange(dueDate, null);
                  else setTimeDraft(dueTime ?? '');
                }}
                placeholder="Своё…"
                style={{
                  width: 76, boxSizing: 'border-box', padding: '6px 9px',
                  border: `1px solid ${C.border}`, borderRadius: R.md,
                  background: C.bgWhite, fontFamily: FONT.mono, fontSize: 12.5,
                  color: C.textPrimary, outline: 'none', textAlign: 'center',
                }}
              />
              <div style={{ flex: 1 }} />
              <button
                onClick={() => setAnchor(null)}
                style={{
                  padding: '6px 13px', border: 'none', borderRadius: R.md, cursor: 'pointer',
                  background: C.accent, color: C.onAccent,
                  fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600,
                }}
              >
                Готово
              </button>
            </div>
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}
