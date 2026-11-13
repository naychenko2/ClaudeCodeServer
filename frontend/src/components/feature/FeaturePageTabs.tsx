import { useEffect, useRef } from 'react';
import { C } from '../../lib/design';
import { Tabs, type TabItem } from '../ui/Tabs';
import { FEATURE_MAIN_TAB_LABEL, type FeatureMainTab } from '../../lib/featurePage';

// Вкладки страницы фичи: «Ход»/«Процесс» (по умолчанию) · «Материалы» · «Разбор».
// Вариант A (решение Андрея 13.11): у фичи с маршрутом первая вкладка — «Процесс»
// (её содержимое — граф и этап), у легаси — «Ход». Полоса стоит под шапкой страницы;
// подпись первой вкладки приходит снаружи (featureMainTabLabel), сама полоса её не выводит.
// Кит ui/Tabs (size md, R-1: 38/44, поля 10/12) — самодельных не заводить; счётчик
// есть только у «Материалов».
//
// Полоса липкая к верху тела (R-7: zIndex 1 в собственном контексте наложения
// страницы), фон bgWhite, линия снизу borderLight, горизонтальная прокрутка БЕЗ
// полосы (класс-инжект). На 360 активная вкладка докручивается в видимость.
const NO_SB_CLASS = 'cc-feature-page-tabs';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-page-tabs-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-page-tabs-style';
  el.textContent =
    `.${NO_SB_CLASS}::-webkit-scrollbar{display:none;}` +
    `.${NO_SB_CLASS}{scrollbar-width:none;}`;
  document.head.appendChild(el);
}

export interface FeaturePageTabsProps {
  // Выбранная вкладка (стор страницы: переживает скрытие/возврат).
  value: FeatureMainTab;
  onChange: (t: FeatureMainTab) => void;
  isMobile: boolean;
  // Подпись первой вкладки: «Процесс» (фича с маршрутом) / «Ход» (легаси) —
  // её выводит страница через featureMainTabLabel, полоса только показывает.
  progressLabel: string;
  // Счётчик «Материалов»; 0/null — без счётчика.
  plansCount?: number | null;
}

export function FeaturePageTabs({ value, onChange, isMobile, progressLabel, plansCount }: FeaturePageTabsProps) {
  const wrapRef = useRef<HTMLDivElement>(null);

  // На 360: активная вкладка в видимости (макет §4).
  useEffect(() => {
    if (!isMobile) return;
    const el = wrapRef.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]');
    el?.scrollIntoView({ inline: 'nearest', block: 'nearest' });
  }, [value, isMobile]);

  const items: TabItem<FeatureMainTab>[] = [
    { value: 'progress', label: progressLabel },
    { value: 'materials', label: FEATURE_MAIN_TAB_LABEL.materials, count: plansCount != null && plansCount > 0 ? plansCount : undefined },
    { value: 'review', label: FEATURE_MAIN_TAB_LABEL.review },
  ];

  return (
    <div
      ref={wrapRef}
      data-testid="feature-page-tabs"
      style={{
        position: 'sticky', top: -16, zIndex: 1,
        margin: '14px -16px', padding: '0 16px',
        background: C.bgWhite, borderBottom: `1px solid ${C.borderLight}`,
      }}
    >
      <Tabs
        transparent
        size="md"
        isMobile={isMobile}
        className={NO_SB_CLASS}
        ariaLabel="Разделы фичи"
        value={value}
        items={items}
        onChange={onChange}
      />
    </div>
  );
}
