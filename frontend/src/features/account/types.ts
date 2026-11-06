// Типы домена «Аккаунт» (методы — ./api.ts). Вынесены из lib/api.ts шагом S5-2
// (refactor/opt-frontend-api); публичный адрес типов — через ре-экспорт lib/api.ts
// (`export type *`), импорты потребителей не меняются.

// Личные/админские слоты моделей: сильная/средняя/слабая.
// null = наследовать глобальный слот, string = override, "" = сброс к наследованию.
export interface ModelTiers {
  strong: string | null;
  medium: string | null;
  weak: string | null;
}
