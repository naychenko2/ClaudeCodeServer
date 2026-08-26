// Значок роли в витрине и визитке. Если аватарка для ключа есть в
// assets/specialties — рендерит <img> в круге цвета роли; иначе — lucide-глиф
// из icon/color (с бэка, SpecialtyCatalogEntry.icon / color) в том же круге.
// Старый lucide-фолбэк с DynamicIcon остаётся как последняя опора: раздел живёт
// и без аватарок (например, до того как файл добавили в репозиторий).
//
// Источник значка и круга — единый, чтобы SpecialtyListView и SpecialtyRoleView
// не дублировали разметку; раньше у каждого был свой RoleIcon, и любая правка
// (подключение аватарок, смена палитры, новый размер) расходилась между ними.

import { useState } from 'react';
import { AGENT_COLORS } from '../AgentSelector';
import { C, FONT, R } from '../../lib/design';
import { GlyphIcon } from '../../lib/projectGlyphs';
import { roleAvatarUrl, roleIconName, roleColorKey } from '../../lib/specialties';
import type { SpecialtyCatalogEntry } from '../../types';

// Две буквы из подписи роли для фолбэка, когда ни картинка, ни глиф не справились.
// Режем по первому значимому слову: «Исполнитель бэкенда» → «ИБ», чтобы
// четырёх «Исполнителей» из каталога было хоть как-то различимо.
function initialsOfLabel(label: string): string {
  const words = label.replace(/[ёЁ]/g, 'е').trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return '??';
  if (words.length === 1) {
    const w = words[0]!;
    return (w.length >= 2 ? w.slice(0, 2) : w).toUpperCase();
  }
  return (words[0]![0]! + words[1]![0]!).toUpperCase();
}

export function RoleAvatar({ catalog, roleKey, size }: {
  catalog: SpecialtyCatalogEntry | null; roleKey: string; size: number;
}): React.ReactElement {
  const url = roleAvatarUrl(roleKey);
  const colorKey = roleColorKey(catalog, roleKey);
  const bg = AGENT_COLORS[colorKey] ?? AGENT_COLORS.brown;
  const iconName = roleIconName(catalog, roleKey);
  const initials = initialsOfLabel(catalog?.label ?? roleKey);
  // По onError проваливаемся на глиф: файл не отдался (404, .jpg не попал в репо,
  // vite ещё не отдал) — пустой круг никому не нужен. Дальше фолбэк глифа —
  // на инициалы, чтобы оставалась хоть какая-то подпись.
  const [imgFailed, setImgFailed] = useState(false);
  if (url && !imgFailed) {
    return (
      <span style={{
        flex: 'none', width: size, height: size, borderRadius: R.full,
        background: bg, color: 'white', overflow: 'hidden',
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      }}>
        <img src={url} alt=""
          width={size} height={size}
          onError={() => setImgFailed(true)}
          style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
      </span>
    );
  }
  return (
    <span title="Значок роли задан продуктом и не настраивается" style={{
      flex: 'none', width: size, height: size, borderRadius: R.full,
      background: bg, color: 'white',
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    }}>
      <GlyphIcon name={iconName} fallback={() => (
        // Инициалы подписи роли на заливке цвета роли — как у PersonaAvatar,
        // когда аватарка персоны не отдалась. Тот же приём: глиф не распознан →
        // человек всё равно видит, что за роль.
        <span style={{
          fontFamily: FONT.sans, fontSize: Math.round(size * 0.36),
          fontWeight: 700, color: C.onAccent, letterSpacing: '-0.01em',
          lineHeight: 1,
        }}>{initials}</span>
      )} size={Math.round(size * 0.55)} />
    </span>
  );
}