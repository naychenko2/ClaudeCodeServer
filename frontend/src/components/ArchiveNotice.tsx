import { useEffect, useState } from 'react';
import { Archive, Check, Timer } from 'lucide-react';
import { C, FONT, FS, SP } from '../lib/design';
import { Button, Menu, MenuItem } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { api } from '../lib/api';
import { showToast } from '../lib/toast';

interface Props {
  // Сколько чатов лежит в архиве этой области
  count: number;
  // Выйти из архивного вида обратно к списку
  onExit: () => void;
  // Удалить весь архив (насовсем). Не передан — кнопки очистки нет
  onClear?: () => void;
  isMobile?: boolean;
}

// Пресеты срока хранения архива. Дефолт (null) первым: архив вечен, пока человек
// не решит иначе — молчаливое удаление сделало бы «В архив» отложенным «Удалить»
const RETENTION: { days: number | null; label: string }[] = [
  { days: null, label: 'Хранить всегда' },
  { days: 1, label: '1 день' },
  { days: 15, label: '15 дней' },
  { days: 30, label: '30 дней' },
  { days: 90, label: '90 дней' },
  { days: 180, label: 'полгода' },
  { days: 365, label: 'год' },
];

// Строка действий над архивным списком чатов: срок хранения, полная очистка и выход.
// Пояснительного текста здесь нет намеренно — где человек находится, видно по вдавленной
// кнопке «Архив» в шапке и по счётчику на ней; подпись повторяла бы это словами и отъедала
// строку у самого списка. Пустой архив объясняет EmptyState.
//
// Общая для списка чатов вне проектов (ChatList) и списка чатов проекта (SessionList):
// набор действий в двух местах обязан совпадать.
export function ArchiveNotice({ count, onExit, onClear, isMobile = false }: Props) {
  // Срок хранения — настройка ПОЛЬЗОВАТЕЛЯ, одна на все области, поэтому компонент
  // тянет её сам, а не принимает пропом: двух архивных списков на экране не бывает.
  // undefined — ещё не знаем (кнопка молчит о сроке, пока ответ не приехал)
  const [days, setDays] = useState<number | null | undefined>(undefined);
  const [menu, setMenu] = useState<DOMRect | null>(null);

  useEffect(() => {
    let alive = true;
    api.auth.me()
      .then(me => { if (alive) setDays(me.archiveRetentionDays ?? null); })
      .catch(() => { /* нет сети — срок просто не показываем */ });
    return () => { alive = false; };
  }, []);

  const pick = async (next: number | null) => {
    setMenu(null);
    if (next === (days ?? null)) return;
    const prev = days;
    setDays(next); // optimistic: меню закрылось, подпись обязана смениться сразу
    try {
      await api.auth.setArchiveRetention(next);
    } catch {
      setDays(prev);
      showToast('Архив', 'Не удалось изменить срок хранения', 'info');
    }
  };

  const current = RETENTION.find(r => r.days === (days ?? null));
  // Подпись кнопки — только значение пресета («Всегда», «30 дней»): иконка таймера
  // перед ним уже говорит, что это про срок, а полный текст («Удалять через 30 дней»)
  // живёт в тултипе и в пунктах меню. Длинная подпись в узкой панели либо переносилась
  // на две строки, либо растягивала строку действий — некрасиво в обоих случаях
  const label = days === undefined
    ? 'Хранить…'
    : days === null ? 'Всегда' : current?.label ?? `${days} дн`;
  // Кнопки строки не переносят текст никогда: перенос посреди слова («Очис-тить»)
  // выглядит поломкой, а не вёрсткой
  const btnStyle = { whiteSpace: 'nowrap' as const };

  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm,
      padding: isMobile ? `6px ${SP.lg}px` : '5px 10px',
      borderBottom: `1px solid ${C.borderLight}`,
      background: C.bgPanel, flexShrink: 0,
    }}>
      <Archive size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
      <span style={{ flex: 1, minWidth: 0 }} />
      <Button
        variant="ghost" size="xs"
        title={days === undefined
          ? 'Через сколько дней удалять архивные чаты'
          : days === null
            ? 'Архив хранится всегда'
            : `Архивные чаты удаляются через ${current?.label ?? `${days} дн`} после архивации`}
        leftIcon={<Timer size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        onClick={e => setMenu(m => (m ? null : (e.currentTarget as HTMLElement).getBoundingClientRect()))}
        style={btnStyle}
      >
        {label}
      </Button>
      {count > 0 && onClear && (
        <Button variant="ghost" size="xs" onClick={onClear} style={{ color: C.danger, ...btnStyle }}>
          Очистить
        </Button>
      )}
      <Button variant="ghost" size="xs" onClick={onExit} style={btnStyle}>
        К списку
      </Button>

      {menu && (
        <Menu anchor={menu} onClose={() => setMenu(null)} minWidth={isMobile ? 240 : 230} maxHeight={260}>
          <div style={{
            padding: '7px 10px 3px', fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
            textTransform: 'uppercase', letterSpacing: '0.06em', fontFamily: FONT.sans,
          }}>
            Срок хранения архива
          </div>
          {RETENTION.map(r => {
            const active = r.days === (days ?? null);
            return (
              <MenuItem
                key={r.days ?? 'never'}
                isMobile={isMobile}
                onClick={() => { void pick(r.days); }}
                label={
                  <span style={{
                    display: 'flex', alignItems: 'center', justifyContent: 'space-between',
                    flex: 1, gap: SP.sm,
                  }}>
                    {r.days === null ? r.label : `Удалять через ${r.label}`}
                    {active && <Check size={ICON_SIZE.xs} strokeWidth={2.4} style={{ color: C.accent, flexShrink: 0 }} />}
                  </span>
                }
              />
            );
          })}
          <div style={{
            padding: '4px 10px 8px', fontSize: 11.5, color: C.textMuted,
            fontFamily: FONT.sans, lineHeight: 1.4,
          }}>
            Срок считается с момента архивации. Настройка личная и общая для всех проектов.
          </div>
        </Menu>
      )}
    </div>
  );
}
