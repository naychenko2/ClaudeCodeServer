import { useState, type ReactNode } from 'react';
import { Plus } from 'lucide-react';
import { C, FONT, SHADOW, TB } from '../../lib/design';

// Неявная кнопка «создать» после заголовка виджета: тихий плюсик,
// проявляется на hover (bgSelected + чуть темнее иконка)
function CreateButton({ title, onClick }: { title: string; onClick: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <button
      onClick={onClick}
      title={title}
      aria-label={title}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        width: 22, height: 22, borderRadius: 6, border: 'none', cursor: 'pointer',
        background: hover ? C.bgSelected : 'transparent',
        color: hover ? C.textPrimary : C.textMuted, transition: 'background 0.15s, color 0.15s',
        flexShrink: 0, padding: 0,
      }}
    >
      <Plus size={14} strokeWidth={2} />
    </button>
  );
}

// Карточка-обертка виджета дашборда «Домой»: заголовок serif + иконка + опциональный
// «+» создания после заголовка + действие справа («Все →»). Стиль — как ProjectCard.
export function WidgetCard({ icon, title, action, fill, onCreate, createTitle, children }: {
  icon?: ReactNode;
  title: string;
  action?: ReactNode;
  // Заполнить высоту родителя (виджет в ряду фиксированной высоты со скроллом внутри)
  fill?: boolean;
  // Обработчик «+» после заголовка (создание сущности виджета) + подпись тултипа
  onCreate?: () => void;
  createTitle?: string;
  children: ReactNode;
}) {
  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: 16,
      padding: 16, boxShadow: SHADOW.card, minWidth: 0,
      display: 'flex', flexDirection: 'column', gap: 10,
      ...(fill ? { height: '100%', boxSizing: 'border-box' as const } : {}),
    }}>
      {/* flexWrap + nowrap-заголовок: на узкой карточке (планшет, 2 колонки) action-блок
          (тумблер/ссылка) переносится целиком на вторую строку, не сминая заголовок.
          Заголовок не сжимается (без minWidth:0) — спейсер отдает место, action уезжает вниз */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        {icon && <span style={{ display: 'flex', color: C.textSecondary }}>{icon}</span>}
        <span style={{ fontFamily: FONT.serif, fontSize: 17, fontWeight: 500, color: C.textHeading, whiteSpace: 'nowrap' }}>
          {title}
        </span>
        {onCreate && <CreateButton title={createTitle ?? 'Создать'} onClick={onCreate} />}
        <span style={{ flex: 1 }} />
        {action}
      </div>
      {children}
    </div>
  );
}

// Мини-сегмент (тогл) для шапки виджета: серый трек + белая активная пилюля с тенью —
// стиль общего PillSwitch (default), но компактный. dot — цветная точка перед подписью.
export function MiniSegment<T extends string>({ value, options, onChange }: {
  value: T;
  options: { value: T; label: string; title?: string; dot?: string }[];
  onChange: (v: T) => void;
}) {
  return (
    <span style={{
      display: 'inline-flex', gap: 2, padding: 2,
      borderRadius: 999, background: TB.pillTrack,
    }}>
      {options.map(o => {
        const active = o.value === value;
        return (
          <button
            key={o.value}
            onClick={() => onChange(o.value)}
            title={o.title}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 5,
              border: 'none', borderRadius: 999, padding: '3px 10px', cursor: 'pointer',
              background: active ? TB.pillThumbBg : 'transparent',
              boxShadow: active ? TB.pillThumbShadow : 'none',
              color: active ? C.textHeading : C.textSecondary,
              fontFamily: FONT.sans, fontSize: 11.5, fontWeight: active ? 600 : 400,
              whiteSpace: 'nowrap', transition: 'background 0.15s, color 0.15s',
            }}
          >
            {o.dot && <span style={{ width: 6, height: 6, borderRadius: '50%', background: o.dot, flexShrink: 0 }} />}
            {o.label}
          </button>
        );
      })}
    </span>
  );
}

// Текстовая кнопка-действие в шапке виджета («Все задачи →»)
export function WidgetAction({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      style={{
        background: 'none', border: 'none', padding: 0, cursor: 'pointer',
        fontFamily: FONT.sans, fontSize: 12.5, color: C.accent, whiteSpace: 'nowrap',
      }}
    >
      {label}
    </button>
  );
}

// Компактное пустое состояние виджета
export function WidgetEmpty({ text }: { text: string }) {
  return (
    <div style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textMuted, padding: '10px 0' }}>
      {text}
    </div>
  );
}

// Относительное время для строк сессий («только что», «5 мин назад», «вчера»)
export function relTime(iso: string): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const min = Math.floor(Math.max(0, Date.now() - t) / 60_000);
  if (min < 1) return 'только что';
  if (min < 60) return `${min} мин назад`;
  const h = Math.floor(min / 60);
  if (h < 24) return `${h} ч назад`;
  const d = Math.floor(h / 24);
  if (d === 1) return 'вчера';
  if (d < 7) return `${d} дн назад`;
  return new Date(t).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
}
