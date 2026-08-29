import { C } from '../lib/design'

export type SessionStatus = 'starting' | 'working' | 'active' | 'waiting' | 'orphaned' | 'finished' | 'error'

// Вид карточки в списке. Шире статуса CLI: ход уже завершён (сессия Active), но в чате
// доживает фоновая работа — 'agents' (фоновые агенты, Workflow) или 'command' (Bash в фоне:
// дев-сервер, watch), и карточка обязана выглядеть живой. Светятся оба ОДИНАКОВО; врозь их
// держим ради подписи и значка в строке имени. В сам SessionStatus не добавляем — тот
// описывает состояние процесса, а не картинку
export type VisualStatus = SessionStatus | 'agents' | 'command'

// Легенда статусов: подпись и цвет. Цвет — на основных (землистых) токенах;
// его же читает перелив фона карточки (STATUS_GLOW не несёт отдельного цвета).
// Цветные — те, по чьей карточке идёт движение: starting и working (accent —
// работа), waiting (warning — медовый, «нужен человек»), error (danger).
// Спокойные (active/orphaned/finished) — нейтрально-серые (textMuted): ход
// завершён, фон они не красят, и цвет им ни на что не влияет
export const STATUS_CONFIG: Record<VisualStatus, { label: string; color: string }> = {
  starting: { label: 'запуск',     color: C.accent    },
  working:  { label: 'работает',   color: C.accent    },
  agents:   { label: 'агенты работают', color: C.accent },
  command:  { label: 'фоновая команда', color: C.accent },
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
export const STATUS_GLOW: Record<VisualStatus, { alpha: number; breath: boolean; slow?: boolean }> = {
  starting: { alpha: 55, breath: true },
  working:  { alpha: 60, breath: true },
  // Фоновые агенты: волна как у работающего чата — работа там и правда идёт. Отличает
  // такой чат не оттенок (на 8-процентной разнице цвета его не различить), а значок
  // агентов в строке имени; см. ChatCard
  agents:   { alpha: 60, breath: true },
  // Фоновая команда (дев-сервер, watch) светится ровно как агенты: для человека это один
  // вопрос «идёт ли тут работа», и разная яркость читалась бы как разная важность, а не как
  // разный вид фона. Что именно работает — говорит значок терминала в строке имени
  command:  { alpha: 60, breath: true },
  waiting:  { alpha: 55, breath: true, slow: true },
  error:    { alpha: 72, breath: false },
  orphaned: { alpha: 0,  breath: false },
  active:   { alpha: 0,  breath: false },
  finished: { alpha: 0,  breath: false },
}

// Сам box-shadow карточки управляется классами cc-glow-* в index.css (анимация
// требует управления им целиком), здесь — только источник легенды (цвет + сила)
