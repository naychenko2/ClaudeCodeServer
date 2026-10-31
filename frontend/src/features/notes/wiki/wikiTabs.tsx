// Реестр подвкладок вкладки «Вики» (макет llm-wiki-v2: «Каталог · Входящие · Журнал» плюс
// «Здоровье» и «Настройки»). Здесь — только порядок, подписи и заглушки: WikiTab.tsx рисует
// переключатель по этому списку и для строки без component ставит заглушку.
//
// Дальше по фиче: S7c/S7d («Настройки») — добавляют свой импорт в этом файле и подставляют
// его в component своей строки (так же, что S7a для «Входящих» и S5f для «Здоровья»).
// WikiTab.tsx они не трогают: он читает только этот список.
//
// component — компонент (не функция, вызываемая из WikiTab): WikiTab рендерит его элементом
// и передаёт выбранный раздел. Прямой вызов подвкладки в теле WikiTab зачёл бы её хуки в
// родитель (переключение подвкладки меняло бы их число → «Rendered more hooks…»), а ключ
// раздела остался бы в состоянии WikiTab — следующим шагам пришлось бы трогать сам WikiTab.

import type { ComponentType } from 'react';
import type { LucideIcon } from 'lucide-react';
import { Activity, ArrowDownToLine, BookOpen, History, Settings } from 'lucide-react';
import { EmptyState } from '../../../components/ui/EmptyState';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { WikiHealth } from './WikiHealth';
import { WikiInbox } from './WikiInbox';
import { WikiJournal } from './WikiJournal';

export type WikiSubTabId = 'pages' | 'inbox' | 'journal' | 'health' | 'settings';

export interface WikiSubTab {
  id: WikiSubTabId;
  label: string;
  icon: LucideIcon;
  /** Компонент подвкладки; нет — рисовать заглушку (подвкладка ещё не сделана).
   *  Получает выбранный раздел: подвкладка не читает состояние WikiTab */
  component?: ComponentType<{ selectedKey: string | null }>;
  /** Что сказать в заглушке: каким шагом подкладка придёт */
  stubNote?: string;
}

export const WIKI_SUBTABS: WikiSubTab[] = [
  // Каталог выбранного раздела — сам экран вкладки, он в WikiTab.tsx (S5c)
  { id: 'pages', label: 'Страницы', icon: BookOpen },
  // «Входящие» — свои с S7a: только спорные пакеты, у каждой операции причина
  { id: 'inbox', label: 'Входящие', icon: ArrowDownToLine, component: WikiInbox },
  // «Журнал» — своё с S7c-1/S7c-2: хронология событий своей вики и откат всего источника
  { id: 'journal', label: 'Журнал', icon: History, component: WikiJournal },
  // «Здоровье» — своё с S5f: отчёт последней проверки выбранного раздела и запуск проверки
  { id: 'health', label: 'Здоровье', icon: Activity, component: WikiHealth },
  { id: 'settings', label: 'Настройки', icon: Settings, stubNote: 'Придёт с шагом «Настройки»: ночной разбор, применение и отбор чатов.' }, // S7c/S7d
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
