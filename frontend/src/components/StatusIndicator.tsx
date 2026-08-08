import { C } from '../lib/design'

export type SessionStatus = 'starting' | 'working' | 'active' | 'waiting' | 'orphaned' | 'finished' | 'error'

// Легенда статусов: подпись и цвет. Цвет — на основных (землистых) токенах;
// его же читает перелив фона карточки (STATUS_GLOW не несёт отдельного цвета).
// Цветные — те, по чьей карточке идёт движение: starting и working (accent —
// работа), waiting (warning — медовый, «нужен человек»), error (danger).
// Спокойные (active/orphaned/finished) — нейтрально-серые (textMuted): ход
// завершён, фон они не красят, и цвет им ни на что не влияет
export const STATUS_CONFIG: Record<SessionStatus, { label: string; color: string }> = {
  starting: { label: 'запуск',     color: C.accent    },
  working:  { label: 'работает',   color: C.accent    },
  active:   { label: 'активна',    color: C.textMuted },
  waiting:  { label: 'ждёт ввода', color: C.warning   },
  orphaned: { label: 'прервана',   color: C.textMuted },
  finished: { label: 'готово',     color: C.textMuted },
  error:    { label: 'ошибка',     color: C.danger    },
}

// Внешний glow-ореол карточки: alpha — сила свечения (0 = не светится, обычная
// карточка). В релевантном списке у большинства чатов active/finished — им glow
// не нужен, иначе список превратится в скопище колец. Светятся только те, что
// требуют внимания: живые (запуск/работа/ожидание) и ошибка. orphaned приравнен
// к finished — серо-бежевый без glow (различают только подписью).
// Цвет свечения берётся из STATUS_CONFIG (основной землистый токен) — отдельного
// насыщенного glow-цвета нет: точки и аура одного цвета
export const STATUS_GLOW: Record<SessionStatus, { alpha: number; breath: boolean; slow?: boolean }> = {
  starting: { alpha: 55, breath: true },
  working:  { alpha: 60, breath: true },
  waiting:  { alpha: 55, breath: true, slow: true },
  error:    { alpha: 72, breath: false },
  orphaned: { alpha: 0,  breath: false },
  active:   { alpha: 0,  breath: false },
  finished: { alpha: 0,  breath: false },
}

// Сам box-shadow карточки управляется классами cc-glow-* в index.css (анимация
// требует управления им целиком), здесь — только источник легенды (цвет + сила)
