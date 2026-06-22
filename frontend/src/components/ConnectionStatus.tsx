import { useSyncExternalStore } from 'react';
import { useOnline } from '../hooks/useOnline';
import { getSyncProgress, subscribeSyncProgress, syncLabel, syncCount } from '../lib/sync';

// Единый, ненавязчивый индикатор состояния связи и прогресса синхронизации.
// Сам подписывается на онлайн-статус и стор прогресса снапшота.
//
// Состояния (по приоритету):
//   1. Синхронизация (progress.active) — спиннер accent + «Синхронизация done/total»
//   2. Офлайн (!online)                — приглушённый серый статус + «Офлайн»
//   3. Онлайн                          — зелёная точка + переданный subtitle (путь/URL)
//
// Варианты отображения:
//   'footer' — для футера сайдбара воркспейса (иконка-кружок + заголовок + строка состояния)
//   'badge'  — для шапки списка проектов (компактный одностройный бейдж)

type ConnState = 'syncing' | 'offline' | 'online';

interface SyncSnapshot {
  active: boolean;
  done: number;
  total: number;
}

// Подписка на стор прогресса синхронизации
function useSyncProgress(): SyncSnapshot {
  return useSyncExternalStore(subscribeSyncProgress, getSyncProgress, getSyncProgress);
}

// Маленький спиннер в accent (CSS-анимация spin определена в index.css)
function Spinner({ size = 13 }: { size?: number }) {
  return (
    <div
      style={{
        width: size,
        height: size,
        borderRadius: '50%',
        border: '2px solid #E0D7C8',
        borderTopColor: '#D97757',
        animation: 'spin 0.8s linear infinite',
        flexShrink: 0,
      }}
    />
  );
}

// --- Вариант footer: иконка-кружок (как было), заголовок (title) и строка состояния ---

interface FooterProps {
  variant: 'footer';
  title: string;       // обычно имя проекта
  subtitle: string;    // путь проекта (показывается онлайн без активной синхронизации)
}

// --- Вариант badge: компактный одностройный индикатор для шапки ---

interface BadgeProps {
  variant: 'badge';
  label: string;       // обычно serverUrl (показывается онлайн без активной синхронизации)
}

type Props = FooterProps | BadgeProps;

export function ConnectionStatus(props: Props) {
  const online = useOnline();
  const progress = useSyncProgress();

  const state: ConnState = progress.active ? 'syncing' : online ? 'online' : 'offline';

  // Цвет точки-статуса: онлайн = зелёный, офлайн = приглушённый серый
  const dotColor = state === 'offline' ? '#A89F8E' : '#5E8B4E';

  if (props.variant === 'footer') {
    // Строка состояния: при синхронизации/офлайне заменяет путь проекта
    let statusText = props.subtitle;
    let statusColor = '#9A8F7E';
    let statusMono = true; // путь — моноширинным
    if (state === 'syncing') {
      statusText = syncLabel(progress);
      statusColor = '#BE5536';
      statusMono = false;
    } else if (state === 'offline') {
      statusText = 'Офлайн — сохранённые данные';
      statusColor = '#8A8070';
      statusMono = false;
    }

    return (
      <>
        {/* Иконка-кружок слева: спиннер при синхронизации, иначе точка-статус */}
        <div style={{ width: 32, height: 32, borderRadius: 9, background: '#FFFFFF', border: '1px solid #E0D7C8', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
          {state === 'syncing'
            ? <Spinner size={14} />
            : <div style={{ width: 9, height: 9, borderRadius: '50%', background: dotColor }} />}
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: 13, fontWeight: 600, color: '#2A251F', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{props.title}</div>
          <div style={{ fontSize: 11, color: statusColor, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontFamily: statusMono ? "'JetBrains Mono', monospace" : "'Hanken Grotesk', sans-serif", fontWeight: statusMono ? 400 : 600 }}>{statusText}</div>
        </div>
      </>
    );
  }

  // variant === 'badge'

  // Синхронизация: спиннер + обрезаемое слово «Синхронизация» + НЕобрезаемый счётчик «done/total».
  // Счётчик (главная информация) защищён flexShrink: 0 — он влезает всегда. На самом узком
  // экране слово схлопывается под многоточие/исчезает, но цифры остаются видны целиком.
  if (state === 'syncing') {
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 7, minWidth: 0, overflow: 'hidden' }}>
        <Spinner size={12} />
        {/* «Синхронизация» — необязательная подпись, жертвуется первой при нехватке места */}
        <span style={{
          fontFamily: "'Hanken Grotesk', sans-serif",
          fontSize: 11,
          fontWeight: 600,
          color: '#8A8070',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
          minWidth: 0,
        }}>
          Синхронизация
        </span>
        {/* Счётчик — моноширинный, accent, не сжимается и не обрезается */}
        <span style={{
          fontFamily: "'JetBrains Mono', monospace",
          fontSize: 11,
          fontWeight: 600,
          color: '#BE5536',
          whiteSpace: 'nowrap',
          flexShrink: 0,
        }}>
          {syncCount(progress)}
        </span>
      </span>
    );
  }

  // Онлайн (URL) / офлайн — одностройный обрезаемый текст
  let text = props.label;
  let mono = true;
  let color = '#756B5E';
  if (state === 'offline') {
    text = 'Офлайн';
    mono = false;
    color = '#8A8070';
  }

  // Для онлайн-URL убираем схему (https:// / http://) — на узком экране важнее видеть хост.
  if (state === 'online' && mono) {
    text = text.replace(/^https?:\/\//, '');
  }

  return (
    // minWidth: 0 + overflow позволяют бейджу сжиматься, а тексту — обрезаться многоточием
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 7, minWidth: 0, overflow: 'hidden' }}>
      <span style={{ width: 8, height: 8, borderRadius: '50%', background: dotColor, flexShrink: 0 }} />
      <span style={{
        fontFamily: mono ? "'JetBrains Mono', monospace" : "'Hanken Grotesk', sans-serif",
        fontSize: 11,
        fontWeight: mono ? 400 : 600,
        color,
        overflow: 'hidden',
        textOverflow: 'ellipsis',
        whiteSpace: 'nowrap',
        minWidth: 0,
      }}>
        {text}
      </span>
    </span>
  );
}
