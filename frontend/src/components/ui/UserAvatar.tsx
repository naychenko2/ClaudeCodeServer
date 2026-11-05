// Аватар человека — круглый инициал со стабильным цветом по userId.
//
// Зачем примитив: такой кружок уже был нарисован руками в трёх местах (NodeAvatar
// в «Аналитике токенов», UserManagementModal, AvatarMenu) — с разными габаритами и
// разными ключами цвета, поэтому один и тот же человек на разных экранах получал
// разные цвета. Это знак участника фичи «Общие проекты» (спецификация
// docs/mockups/shared-projects-v2-proposal.md, макет shared-projects-v2-projects.html):
// вид перенесён с NodeAvatar без изменений, новых токенов нет.
//
// Ключ цвета — userId, а не имя: иначе тёзки совпадут. Стабильность цвета —
// детерминированный хеш, тот же алгоритм, что у hashColor в features/spend/spendUi.tsx.

import type { CSSProperties } from 'react';
import { C, FONT, GROUP_COLORS, R } from '../../lib/design';

// Размеры по спецификации v2 (18 — подписи автора в ленте, 20 — строки и стопки,
// 28 — список участников, 34 — выбранный исполнитель в комбобоксе)
export type UserAvatarSize = 18 | 20 | 28 | 34;

// Кегль инициала по размеру — значения макета v2 (.ua/.s18/.s28/.s34): 9 вне шкалы FS,
// но это размер, подобранный под круг, а не размер текста интерфейса
const AVATAR_FONT: Record<UserAvatarSize, number> = { 18: 9, 20: 9, 28: 11, 34: 13 };

// Детерминированный цвет подложки по строке: тот же id → тот же тон при каждом
// рендере. -0x80000000 — единственный случай, когда Math.abs возвращает negative:
// индекс ограничивается нулём, иначе подложка осталась бы бесцветной
export function userAvatarColor(key: string): string {
  let h = 0;
  for (let i = 0; i < key.length; i++) h = (h * 31 + key.charCodeAt(i)) | 0;
  const idx = Math.abs(h) % GROUP_COLORS.length;
  return GROUP_COLORS[idx >= 0 ? idx : 0];
}

interface Props {
  userId: string;                  // ключ цвета — id участника, не имя
  displayName?: string | null;     // имя для инициала и aria-label
  username?: string | null;       // запасной источник, когда имени нет
  size?: UserAvatarSize;           // 20 — размер строк и стопок по умолчанию
  gone?: boolean;                  // «бывший участник»: нейтральный вид, цвет смысла не несёт
  style?: CSSProperties;
}

export function UserAvatar({ userId, displayName, username, size = 20, gone, style }: Props) {
  // Первый непустой источник: пустое или пробельное displayName не должно выключать username
  const fullName = displayName?.trim() || username?.trim() || '';
  const letter = (fullName.charAt(0) || '·').toUpperCase();
  return (
    <span
      role="img"
      aria-label={fullName || 'Аватар'}
      style={{
        width: size, height: size, borderRadius: R.full, flexShrink: 0,
        background: gone ? C.bgInset : userAvatarColor(userId),
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        fontSize: AVATAR_FONT[size], fontWeight: 700,
        color: gone ? C.textMuted : C.onDark,
        fontFamily: FONT.sans,
        ...style,
      }}
    >
      {letter}
    </span>
  );
}
