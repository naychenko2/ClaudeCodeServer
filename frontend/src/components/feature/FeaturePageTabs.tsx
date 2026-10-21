import { useEffect, useRef } from 'react';
import { C } from '../../lib/design';
import { Tabs, type TabItem } from '../ui/Tabs';
import { FEATURE_TAB_LABEL, type FeaturePageTab } from '../../lib/featurePage';

// Вкладки страницы фичи (волна 3, Ф1): «Хронология» (по умолчанию) · «Планы и
// макеты» · «Чаты» · «Задачи» · «События» · «Статистика». Кит ui/Tabs (size md,
// R-1: 38/44, поля 10/12) — самодельных не заводить; счётчики (Чаты/Задачи/События)
// и точка «ждёт ответа» у «Чатов» — по макету §4.
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
  value: FeaturePageTab;
  onChange: (t: FeaturePageTab) => void;
  isMobile: boolean;
  // Счётчики; 0/null — без счётчика (макет: нулевые не рисуются).
  plansCount?: number | null;
  chatsCount?: number | null;
  tasksCount?: number | null;
  eventsCount?: number | null;
  // Точка «ждёт вашего ответа» у «Чатов»: фича ждёт ответа в чате.
  waitingDot?: boolean;
}

export function FeaturePageTabs({ value, onChange, isMobile, plansCount, chatsCount, tasksCount, eventsCount, waitingDot }: FeaturePageTabsProps) {
  const wrapRef = useRef<HTMLDivElement>(null);

  // На 360: активная вкладка в видимости (макет §4).
  useEffect(() => {
    if (!isMobile) return;
    const el = wrapRef.current?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]');
    el?.scrollIntoView({ inline: 'nearest', block: 'nearest' });
  }, [value, isMobile]);

  const items: TabItem<FeaturePageTab>[] = [
    { value: 'timeline', label: FEATURE_TAB_LABEL.timeline },
    { value: 'plans', label: FEATURE_TAB_LABEL.plans, count: plansCount != null && plansCount > 0 ? plansCount : undefined },
    {
      value: 'chats', label: FEATURE_TAB_LABEL.chats,
      count: chatsCount != null && chatsCount > 0 ? chatsCount : undefined,
      dot: waitingDot || undefined,
    },
    { value: 'tasks', label: FEATURE_TAB_LABEL.tasks, count: tasksCount != null && tasksCount > 0 ? tasksCount : undefined },
    { value: 'events', label: FEATURE_TAB_LABEL.events, count: eventsCount != null && eventsCount > 0 ? eventsCount : undefined },
    { value: 'stats', label: FEATURE_TAB_LABEL.stats },
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
