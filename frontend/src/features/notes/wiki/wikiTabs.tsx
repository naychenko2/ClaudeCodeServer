// Реестр подвкладок вкладки «Вики» (макет llm-wiki-v2: «Каталог · Входящие · Журнал» плюс
// «Здоровье» и «Настройки»). Здесь — только порядок, подписи и заглушки: WikiTab.tsx рисует
// переключатель по этому списку и для строки с component === null ставит заглушку.
//
// Дальше по фиче: S7a («Входящие», «Журнал»), S5f («Здоровье»), S7c/S7d («Настройки») —
// добавляют свой импорт в этом файле и подставляют его в component своей строки.
// WikiTab.tsx они не трогают: он читает только этот список.

import type { ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';
import { Activity, ArrowDownToLine, BookOpen, History, Settings } from 'lucide-react';
import { EmptyState } from '../../../components/ui/EmptyState';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';

export type WikiSubTabId = 'pages' | 'inbox' | 'journal' | 'health' | 'settings';

export interface WikiSubTab {
  id: WikiSubTabId;
  label: string;
  icon: LucideIcon;
  /** Компонент подвкладки; null — рисовать заглушку (подвкладка ещё не сделана) */
  component: (() => ReactNode) | null;
  /** Что сказать в заглушке: каким шагом подкладка придёт */
  stubNote?: string;
}

export const WIKI_SUBTABS: WikiSubTab[] = [
  // Каталог выбранного раздела — сам экран вкладки, он в WikiTab.tsx (S5c)
  { id: 'pages', label: 'Страницы', icon: BookOpen, component: null },
  { id: 'inbox', label: 'Входящие', icon: ArrowDownToLine, component: null, stubNote: 'Придёт с шагом «Входящие»: там только спорные правки, у каждой — почему она ждёт вас.' }, // S7a
  { id: 'journal', label: 'Журнал', icon: History, component: null, stubNote: 'Придёт с шагом «Журнал»: там откат пакета и откат всего чата.' }, // S7a/S7b
  { id: 'health', label: 'Здоровье', icon: Activity, component: null, stubNote: 'Придёт с шагом «Здоровье»: метрики вики и то, что просится наверх.' }, // S5f
  { id: 'settings', label: 'Настройки', icon: Settings, component: null, stubNote: 'Придёт с шагом «Настройки»: ночной разбор, применение и отбор чатов.' }, // S7c/S7d
];

export function wikiSubTabIcon(tab: WikiSubTab) {
  const Icon = tab.icon;
  return <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;
}

// Заглушка несделанной подкладки: имя и то, когда она придёт. Пустое состояние — тем же
// EmptyState, что и остальные пустые экраны фичи.
export function WikiSubTabStub({ label, when }: { label: string; when: string }) {
  return (
    <EmptyState
      compact
      inline
      icon={<span style={{ width: ICON_SIZE.lg }} />}
      title={label}
      subtitle={
        <>
          Этот раздел вики ещё не сделан.{' '}
          <span style={{ color: 'inherit', opacity: 0.75 }}>{when}</span>
        </>
      }
    />
  );
}
