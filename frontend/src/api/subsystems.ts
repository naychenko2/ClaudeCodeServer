// API-методы экрана «Подсистемы» (Этап 5, волна 3) и панели «Компоненты» (S2c-2):
// чтение списка инстансных подсистем и запись тумблера одной строки. Эндпоинты —
// `/api/admin/subsystems` и `PUT /api/admin/subsystems/{key}` (бэкенд-контроллер
// `SubsystemsController.cs`, источник правды — `SubsystemStateStore.Snapshot`).
// Отдельный модуль — рядом с `api/chats.ts` и другими продуктовыми API:
// каждое направление держим в своей папке, чтобы их состав был виден за минуту.
//
// Контракт записи — по `SubsystemInfo` на бэке (PascalCase → camelCase JSON).
// Все поля ОБЯЗАТЕЛЬНЫЕ: бэк отдаёт record целиком, null-значения использования
// сериализуются как null — «данных нет» на фронте выражено null, а не отсутствием
// поля (отсутствующее поле в типе = «мог не прийти», и панель гадала бы).
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
// плашку «Перезапустите сервер». `PUT` (S2c-1 на бэке, S2c-2 здесь) пишет ТОЛЬКО
// тумблер панели — в `data/subsystems.json`, отдельным источником конфигурации после
// `appsettings.Local.json`; горячего применения нет (гейт читается один раз при
// сборке контейнера, ADR-014), и `restartRequired` в ответе — ровно то честное
// «нужен рестарт», что видит человек. Никакого оптимистичного применения на фронте:
// строка меняется целым ответом сервера или следующим GET.

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
  lastUsedAt: string | null;
  usedLast24h: number | null;
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

  // Запись тумблера строки (S2c-2): PUT с телом `{ enabled }` — единственный, что
  // читает бэк (SubsystemsController.Put: отсутствие поля = ошибка клиента, а не
  // «выключить»). Ответ — ЦЕЛАЯ строка (тот же SubsystemInfo с уже записанным
  // enabled и честным restartRequired), а не «ok»: источник правды строки — сервер.
  // Отказ приходит телом `{ code, message }` (400 core_subsystem / 404 / 503
  // write_failed): request() пробрасывает его с .status и .body, а текст message
  // отдаётся строке как есть — см. useComponentsAdmin.
  put: (key: string, enabled: boolean) =>
    request<Subsystem>(`/admin/subsystems/${encodeURIComponent(key)}`, {
      method: 'PUT',
      body: JSON.stringify({ enabled }),
    }),
} as const;
