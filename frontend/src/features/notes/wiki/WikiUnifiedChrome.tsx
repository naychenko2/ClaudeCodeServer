// Обвязка единой раскладки «Заметки + Вики» (ADR-038, F1): рельса кнопок справа, панель
// выбранного раздела и шторки на 360. Навигации вики в контентной зоне нет — всё, что раньше
// было вкладками и «Назад», живёт здесь, по краям. Данных своих нет: список разделов панели —
// WIKI_SUBTABS (кроме «Страниц»: каталог — это центр), сама подвкладка рисуется своим
// компонентом из реестра.

import type { CSSProperties, ReactNode } from 'react';
import { X } from 'lucide-react';
import { ICON_SIZE } from '../../../components/ui/icons';
import { C, FS, R, SHADOW, SP, Z } from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';
import { WIKI_SUBTABS, wikiSubTabIcon, WikiSubTabStub, type WikiSubTabId } from './wikiTabs';

// Разделы правой панели: всё, кроме каталога («Страницы» — центр экрана)
// Функцией, не константой: WikiInbox тянет WikiTab, и на момент загрузки этого модуля реестр
// может быть ещё не заполнен (цикл импортов)
export const panelTabs = () => WIKI_SUBTABS.filter(t => t.id !== 'pages');

export const RAIL_W = 44;
export const PANEL_W = 340;
const DRAWER_W = 304;
const DRAWER_PANEL_W = 330;

const railStyle: CSSProperties = {
  width: RAIL_W, flexShrink: 0, display: 'flex', flexDirection: 'column',
  alignItems: 'center', gap: SP.xs, paddingTop: SP.xs,
};

/** Рельса кнопок раздела: нажатие открывает панель, повторное — закрывает */
export function WikiRail({ active, onSelect }: { active: WikiSubTabId | null; onSelect: (id: WikiSubTabId | null) => void }) {
  return (
    <div data-testid="wiki-rail" role="toolbar" aria-label="Панель вики" aria-orientation="vertical" style={railStyle}>
      {panelTabs().map(t => (
        <Button
          key={t.id}
          variant={active === t.id ? 'secondary' : 'ghost'}
          size="md"
          dataTestId={`wiki-rail-${t.id}`}
          ariaLabel={t.label}
          title={t.label}
          ariaPressed={active === t.id}
          leftIcon={wikiSubTabIcon(t)}
          onClick={() => onSelect(active === t.id ? null : t.id)} >{null}</Button>
      ))}
    </div>
  );
}

/** Тело панели выбранного раздела: компонент из реестра или заглушка */
export function WikiPanelBody({ tab, selectedKey }: { tab: WikiSubTabId; selectedKey: string | null }) {
  const t = panelTabs().find(x => x.id === tab);
  if (!t) return null;
  const Comp = t.component;
  return (
    <div data-testid="wiki-panel-body" style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
      {Comp
        ? <Comp selectedKey={selectedKey} />
        : <div style={{ padding: SP.sm }}><WikiSubTabStub label={t.label} when={t.stubNote ?? ''} /></div>}
    </div>
  );
}

/** Панель десктопа: шапка с названием и крестиком, ниже — тело раздела */
export function WikiSidePanel({ tab, selectedKey, onClose }: { tab: WikiSubTabId; selectedKey: string | null; onClose: () => void }) {
  const t = panelTabs().find(x => x.id === tab);
  return (
    <aside
      data-testid="wiki-panel"
      aria-label={t?.label}
      style={{
        width: PANEL_W, flexShrink: 0, display: 'flex', flexDirection: 'column', minHeight: 0,
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg, overflow: 'hidden',
      }}
    >
      <PanelHead title={t?.label ?? ''} onClose={onClose} />
      <WikiPanelBody tab={tab} selectedKey={selectedKey} />
    </aside>
  );
}

export function PanelHead({ title, onClose }: { title: string; onClose: () => void }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.xs, padding: `0 ${SP.xs}px 0 ${SP.md}px`,
      minHeight: 48, borderBottom: `1px solid ${C.border}`, flexShrink: 0,
    }}>
      <span style={{ flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{title}</span>
      <Button variant="ghost" size="md" dataTestId="wiki-panel-close" ariaLabel="Закрыть" title="Закрыть"
        leftIcon={<X size={ICON_SIZE.sm} />} onClick={onClose}>{null}</Button>
    </div>
  );
}

/** Шторка на 360: подложка + выезжающая панель слева или справа */
export function WikiDrawer({ side, label, onClose, children }: {
  side: 'left' | 'right'; label: string; onClose: () => void; children: ReactNode;
}) {
  const left = side === 'left';
  return (
    <>
      <div
        data-testid="wiki-drawer-overlay"
        onClick={onClose}
        style={{ position: 'fixed', inset: 0, background: C.overlay, zIndex: Z.modal }}
      />
      <div
        data-testid={`wiki-drawer-${side}`}
        role="dialog"
        aria-label={label}
        style={{
          position: 'fixed', top: 0, bottom: 0, [left ? 'left' : 'right']: 0, zIndex: Z.modal + 1,
          width: `min(${left ? DRAWER_W : DRAWER_PANEL_W}px, 92vw)`, background: C.bgWhite,
          display: 'flex', flexDirection: 'column', boxShadow: SHADOW.dropdown,
          borderRadius: left ? `0 ${R.sheet}px ${R.sheet}px 0` : `${R.sheet}px 0 0 ${R.sheet}px`,
        }}
      >
        {children}
      </div>
    </>
  );
}
