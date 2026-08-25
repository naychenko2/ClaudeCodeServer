// Общие типы и компоненты для трёх экранов «Специальностей» (волна 4
// «Персонализация специальностей»): Scope, LayerSwitch (переключатель слоёв)
// и тонкие хелперы для подписей.
//
// Раньше они жили в PersonasSpecialties.tsx и SpecialRulesTab.tsx; с переездом
// на три экрана вынесены сюда, чтобы не дублировать. Используется только внутри
// features/personas — SpecialtyListView / SpecialtyRoleView / SpecialtyEditView.

import type { ReactElement, ReactNode } from 'react';
import { C, FONT, FS, R } from '../../lib/design';

// Слои настроек. Тот же контракт, что в PersonasSpecialties.tsx и SpecialRulesTab.tsx:
// «global» — общий (админ), «owner» — личный, «user» — назначение конкретного
// пользователя (админ). За пределами этих трёх значений ничего не рисуется.
export type Scope = 'global' | 'owner' | 'user';

// === Переключатель слоёв (P14) ===
//
// Сегменты в ряд: «Для всех» (только админ) · «Только для меня» · «Пользователю …».
// На мобиле растягивается flex:1, сегменты переносятся через flexWrap.
//
// Внешний вид совпадает с SpecialRulesTab.tsx: те же токены (bgSelected/white),
// тот же радиус pill, та же акцентная тень при active. Кнопки — нативные, не
// PillSwitch (в SpecialRulesTab тоже SegBtn — единая стилистика раздела).
export function LayerSwitch({ scope, onScope, isAdmin, isMobile }: {
  scope: Scope;
  onScope: (s: Scope) => void;
  isAdmin: boolean;
  isMobile: boolean;
}): ReactElement {
  const seg = (label: string, value: Scope, grow: boolean) => (
    <button type="button" onClick={() => onScope(value)} style={{
      font: 'inherit', fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 600,
      cursor: 'pointer', border: 'none', borderRadius: R.md, padding: '5px 11px',
      flex: grow ? '1 1 auto' : undefined, minWidth: 0,
      background: scope === value ? C.bgWhite : 'transparent',
      color: scope === value ? C.textHeading : C.textSecondary,
      boxShadow: scope === value ? 'var(--shadow-card)' : 'none',
      whiteSpace: 'nowrap',
    }}>{label}</button>
  );
  return (
    <>
      {isAdmin && seg('Для всех', 'global', isMobile)}
      {seg('Только для меня', 'owner', isMobile)}
      {isAdmin && seg('Пользователю …', 'user', isMobile)}
    </>
  );
}

// Подзаголовок под переключателем слоёв (P14) — единый текст для всех экранов
// раздела «Специальности». Подсказка о том, что меняет выбор слоя.
export const LAYER_SUBTITLE = 'Слой определяет, кого коснётся правило.';

// Заголовок раздела (для всех трёх экранов — одинаковый).
export const SPECIALTIES_TITLE = 'Специальности';
export const SPECIALTIES_SUBTITLE =
  'Роль задаёт, какие модели, доступы и инструкции получит персона по умолчанию.';

// Обёртка для содержимого экрана «Специальностей» в PersonasPage.
// Один и тот же max-width и стиль карточки во всех трёх экранах.
export function SpecialtyScreenFrame({ children }: { children: ReactNode }): ReactElement {
  // Внешняя карточка рисуется на уровне PersonasSpecialties (через свой wrap);
  // здесь просто рендерим контент без обёртки — чтобы три экрана могли
  // самостоятельно управлять вертикальной структурой.
  return <>{children}</>;
}
