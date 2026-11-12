// Реестр подвкладок вкладки «Вики» (макет llm-wiki-v2: «Каталог · Входящие · Журнал» плюс
// «Здоровье» и «Настройки»). Здесь — только порядок, подписи и заглушки: WikiTab.tsx рисует
// переключатель по этому списку и для строки без component ставит заглушку.
//
// Дальше по фиче: новый шаг — добавляет свой импорт в этом файле и подставляет его в component
// своей строки (так же, что S7a для «Входящих», S5f для «Здоровья», S7d-3 для «Настроек»).
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
import { WikiSettings } from './WikiSettings';

export type WikiSubTabId = 'pages' | 'inbox' | 'journal' | 'health' | 'settings';

export interface WikiSubTab {
  id: WikiSubTabId;
  label: string;
  icon: LucideIcon;
  /** Компонент подвкладки; нет — рисовать заглушку (подвкладка ещё не сделана).
   *  Получает выбранный раздел: подвкладка не читает состояние WikiTab.
   *  sectionScope — область панели (W9a/W9b): в панелях её задаёт чип шапки, в подвкладке мобилы не передаётся */
  component?: ComponentType<{ selectedKey: string | null; sectionScope?: string | null | 'all' }>;
  /** Что сказать в заглушке: каким шагом подкладка придёт */
  stubNote?: string;
}

export const WIKI_SUBTABS: WikiSubTab[] = [
  // Каталог выбранного раздела — сам экран вкладки, он в WikiTab.tsx (S5c)
  { id: 'pages', label: 'Страницы', icon: BookOpen },
  // «Решения» — свои с S7a и J-F1: при выключенном автоприменении это ручная очередь «Входящие»
  // (спорные пакеты, у каждой операции причина), при включённом — «Решения судьи» (только чтение).
  // Подпись одна на оба режима: режим выбирает тумблер в «Настройках», не навигация.
  { id: 'inbox', label: 'Решения', icon: ArrowDownToLine, component: WikiInbox },
  // «Журнал» — своё с S7c-1/S7c-2: хронология событий своей вики и откат всего источника
  { id: 'journal', label: 'Журнал', icon: History, component: WikiJournal },
  // «Здоровье» — своё с S5f: отчёт последней проверки выбранного раздела и запуск проверки
  { id: 'health', label: 'Здоровье', icon: Activity, component: WikiHealth },
  // «Настройки» — своё с S7d-3: контейнер блоков «Ночной разбор» (место — точка расширения,
  // блок делает шаг F2), «Применение» (S7d-1) и «Отбор чатов» (S7d-2)
  { id: 'settings', label: 'Настройки', icon: Settings, component: WikiSettings },
];

// Подвкладка → ключ панели в реестре зон (W3): подпись и значок панели обязаны совпадать со
// строкой WIKI_SUBTABS (тест wikiPanelKeys). У «Страниц» панели нет — это центр экрана.
export type WikiPanelId = 'wikiInbox' | 'wikiJournal' | 'wikiHealth' | 'wikiSettings';
export const WIKI_SUBTAB_PANEL: Record<Exclude<WikiSubTabId, 'pages'>, WikiPanelId> = {
  inbox: 'wikiInbox',
  journal: 'wikiJournal',
  health: 'wikiHealth',
  settings: 'wikiSettings',
};

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
