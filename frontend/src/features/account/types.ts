// Типы домена «Аккаунт» (методы — ./api.ts). Вынесены из lib/api.ts шагом S5-2
// (refactor/opt-frontend-api); публичный адрес типов — через ре-экспорт lib/api.ts
// (`export type *`), импорты потребителей не меняются.
// У шести имён P12 (Me, AuthState, UserProfile, AppSettings, ClaudeBilling,
// ModelTiers) и displayNameOf публичный адрес — также ре-экспорт types/index.ts
// (`export type *` :1711, `displayNameOf` :1714), а не только lib/api.ts.

// Личные/админские слоты моделей: сильная/средняя/слабая.
// null = наследовать глобальный слот, string = override, "" = сброс к наследованию.
export interface ModelTiers {
  strong: string | null;
  medium: string | null;
  weak: string | null;
}

// Тип доступа к Claude: подписка (стоимость ≈ API-эквивалент) или оплата по API-ключу (реальная цена)
export type ClaudeBilling = 'subscription' | 'api';

export interface AppSettings {
  defaultProjectsPath: string;
  claudeBilling?: ClaudeBilling;
  // Присылать ли утренний бриф по расписанию (настройка инстанса, тумблер в «Фоновых задачах»)
  dailyBriefingEnabled?: boolean;
  // Слоты «сильная/средняя/слабая» — три именованные модели инстанса (id модели любого
  // провайдера, напр. «claude-opus-5» / «glm-5.2» / «haiku»). На них ссылаются назначения
  // мест (новый чат, чат персоны, фоновые действия…) значением "tier:strong|medium|weak".
  // PATCH /api/settings: поле отсутствует = не трогать, "" = очистка слота (решает CLI).
  modelTierStrong?: string | null;
  modelTierMedium?: string | null;
  modelTierWeak?: string | null;
}

// Ответ GET /api/auth/me — профиль текущего пользователя + эффективные настройки.
// Поля онбординга: defaultPersonaId — личная дефолт-персона
// (null — онбординг не пройден), needsOnboarding — обязательный гейт
// онбординга при входе, onboardingSessionId — живая сессия интервью для резюма.
export interface Me {
  userId: string;
  username: string;
  displayName?: string | null;
  role: string;
  featureFlags?: Record<string, boolean>;
  // Активные подсистемы: массив ключей реально зарегистрированных в процессе
  // (отдаётся `SubsystemStateStore.ActiveKeys()` бэка). В список попадают
  // подсистемы, прошедшие гейт `Subsystems:{Key}:Enabled=true` И зарегистрированные
  // на старте; список короткий, детерминированный. Стороной `Record` бэк НЕ
  // шлёт — `{ ...arr }` на фронте даёт числовые ключи '0','1'…, и гейт ломается.
  // Заполняется App.tsx через `setAllSubsystems(me.subsystems)` в lib/subsystems;
  // стор разворачивает массив в `Record<string, boolean>` внутри. Отдельные
  // вкрапления UI (виджеты, пункты меню, кнопки) гейтятся хуком `useSubsystem('notes')`
  // и дают fail-closed false, пока стор пуст — это нормально, лишь бы поле
  // пришло в me-ответе. Контрактный сторож — subsystems.contract.test.ts.
  subsystems?: string[];
  contextThresholds?: { warnPct: number; dangerPct: number } | null;
  // Личные пороги предупреждения об остывании prompt cache (фича «Индикатор prompt
  // cache и перенос в новый чат», F-3): минуты; null — не настроено, работают
  // дефолты индикатора. Сидирует App.tsx в тот же момент, что и contextThresholds.
  cacheWarnThresholds?: { warn1hMin: number; warn5mMin: number } | null;
  defaultPersonaId?: string | null;
  needsOnboarding?: boolean;
  onboardingSessionId?: string | null;
  // Среда исполнения процессов пользователя: 'local' — на хосте, 'container' —
  // в общей Docker-песочнице cc-sandbox. Карточка каталога MCP-серверов рисует по
  // нему бейдж среды и предупреждение для stdio-записей у local-владельцев. Бэк
  // отдаёт поле в AuthController.MeResponse наравне с role
  executionEnvironment?: 'local' | 'container';
}

export interface AuthState {
  serverUrl: string;
  token: string;
  username: string;
  // Отображаемое имя из профиля («Григорий»); пусто — показываем username.
  // Для показа пользователю всегда через displayNameOf(), а не напрямую
  displayName?: string;
  role?: string;
  id?: string;
}

/** Как обращаться к пользователю: имя из профиля, иначе логин. */
export function displayNameOf(auth: { displayName?: string; username: string }): string {
  return auth.displayName?.trim() || auth.username;
}

export interface UserProfile {
  id: string;
  username: string;
  // Отображаемое имя из профиля; может отсутствовать — тогда показываем username
  displayName?: string;
  role: 'admin' | 'user';
  createdAt: string;
  // Среда исполнения процессов пользователя: сервер (полный доступ) или Docker-песочница
  executionEnvironment?: 'local' | 'container';
}
