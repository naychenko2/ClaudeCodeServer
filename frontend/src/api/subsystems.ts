// API-методы экрана «Подсистемы» (Этап 5, волна 3): чтение списка инстансных
// подсистем. Эндпоинт — `/api/admin/subsystems` (бэкенд-контроллер
// `SubsystemsController.cs`, источник правды — `SubsystemStateStore.Snapshot`).
// Отдельный модуль — рядом с `api/chats.ts` и другими продуктовыми API:
// каждое направление держим в своей папке, чтобы их состав был виден за минуту.
//
// Контракт записи — по `SubsystemInfo` на бэке (PascalCase → camelCase JSON).
// Шесть полей (плюс необязательные lastUsedAt и usedLast24h — использование):
//   key             — якорь строки и одновременно ключ `Subsystems:{Key}:Enabled`
//                     в `appsettings.json`;
//   title           — имя подсистемы («Заметки»);
//   description     — одно предложение о разделе (из реализации
//                     `IAppSubsystem.Description`);
//   enabled         — что записано в конфиге инстанса (`Subsystems:{Key}:Enabled`);
//   active          — что реально поднято в текущем процессе;
//   restartRequired — `enabled !== active`: чтобы изменение применилось, нужен
//                     рестарт сервера (гейт читается один раз при сборке контейнера);
//   canDisable      — можно ли отключать вертикаль вовсе (S2b-1): false — строка ядра
//                     (`core:` или вертикаль, объявленная ядром), тумблера ей нет.
//
// Экран показывает ДВА факта (`enabled` и `active`) и при их расхождении —
// плашку «Перезапустите сервер». `PUT` отсутствует намеренно: глобальный
// рубильник через API — отдельная фича со своими вопросами (кто может, что
// при живых ходах, как переживает рестарт). Пилот проверяет ОТКЛЮЧАЕМОСТЬ,
// а не администрирование; значение задаётся в `appsettings`.

import { request } from '../lib/offline';

export interface Subsystem {
  key: string;
  title: string;
  description: string;
  enabled: boolean;
  active: boolean;
  restartRequired: boolean;
  // false — строка ядра: не отключается, тумблера в панели ей не ставят (S2b-1).
  canDisable: boolean;
  // Использование (SubsystemUsageStore): null = данных нет (подсистема не наблюдается
  // или обращений ещё не было).
  lastUsedAt?: string | null;
  usedLast24h?: number | null;
}

export const subsystemsApi = {
  // Список подсистем инстанса с их настройками и фактическим состоянием.
  // GET без оптимистичной логики: страница открылась — пользователь увидел
  // ровно то, что отдаёт сервер, без локальных догадок.
  //
  // Форма ответа — ГОЛЫЙ МАССИВ: `SubsystemsController.List()` возвращает
  // `Ok(subsystems.Snapshot(config))` без обёртки-объекта. Обёртка
  // `{ subsystems: [...] }` здесь уже была и стоила экрана: деструктуризация
  // `({ subsystems })` давала `undefined`, и страница падала на `.filter`.
  // Форму обеих сторон держит сторож `lib/__tests__/subsystems.contract.test.ts`.
  get: () => request<Subsystem[]>('/admin/subsystems'),
} as const;
