import { C, FONT } from '../lib/design';

// Инициалы из имени роли: «Игорь Петров» → «ИП», «Игорь» → «ИГ»
function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

interface Props {
  name: string;
  avatar?: string;   // эмодзи; если пусто — рисуем инициалы
  color?: string;    // фон круга
  size?: number;
  title?: string;    // нативный тултип («Имя · Должность» в списках)
}

// Круглый аватар роли: эмодзи на цветном круге, либо инициалы-фолбэк, если эмодзи не задан.
export function RoleAvatar({ name, avatar, color, size = 32, title }: Props) {
  const bg = color || C.accent;
  const hasEmoji = !!avatar && avatar.trim().length > 0;
  return (
    <div
      title={title}
      style={{
        width: size, height: size, borderRadius: '50%', flexShrink: 0,
        background: bg, color: C.onAccent,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontSize: hasEmoji ? Math.round(size * 0.56) : Math.round(size * 0.42),
        fontWeight: 700, fontFamily: FONT.sans, lineHeight: 1, userSelect: 'none',
      }}
    >
      {hasEmoji ? avatar!.trim() : initials(name)}
    </div>
  );
}
