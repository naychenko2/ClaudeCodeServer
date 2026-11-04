// Обвязка единой раскладки «Заметки + Вики» (ADR-038, F1): панели вики в зонах раздела (W3;
// рельсу и раскладку держит стор зон) и шторки на 360. Навигации вики в контентной зоне нет —
// всё, что раньше было вкладками и «Назад», живёт здесь, по краям. Данных своих нет: список разделов панели —
// WIKI_SUBTABS (кроме «Страниц»: каталог — это центр), сама подвкладка рисуется своим
// компонентом из реестра.

import { useEffect, useRef } from 'react';
import type { ReactNode } from 'react';
import { X } from 'lucide-react';
import { ICON_SIZE } from '../../../components/ui/icons';
import {
  C, FS, R, SHADOW, SP, Z, useWikiReplacementDot, PanelHeaderSlot, parseSectionKey,
  type WikiSectionDto,
} from 'aihome_shell/kit';
import { Button } from '../../../components/ui/Button';
import { SectionChip, SECTION_CHIP_ALL_LABEL } from '../../../components/ui/SectionChip';
import { OVERRIDE_TEXT } from './wikiLabels';
import { resolveWikiScope, useWikiPanelScope } from './wikiPanelScope';
import { WIKI_SUBTABS, WikiSubTabStub, type WikiSubTabId } from './wikiTabs';

// Разделы правой панели: всё, кроме каталога («Страницы» — центр экрана)
// Функцией, не константой: WikiInbox тянет WikiTab, и на момент загрузки этого модуля реестр
// может быть ещё не заполнен (цикл импортов)
export const panelTabs = () => WIKI_SUBTABS.filter(t => t.id !== 'pages');

const DRAWER_W = 304;
const DRAWER_PANEL_W = 330;

/** Точка C.info: на странице есть замена ваших формулировок, которую ещё не вернули (F3b).
 *  Берётся из открытой страницы стора; после «Вернуть мою» страница перечитывается, и точка гаснет */
export function ReplacementDot({ top = 4, right = 4 }: { top?: number; right?: number }) {
  if (!useWikiReplacementDot()) return null;
  return (
    <span
      data-testid="wiki-replacement-dot"
      title={OVERRIDE_TEXT.dotTitle}
      style={{ position: 'absolute', top, right, width: 7, height: 7, borderRadius: '50%', background: C.info, pointerEvents: 'none' }}
    />
  );
}

/** Тело панели выбранного раздела: компонент из реестра или заглушка.
 *  sectionScope — область панели из чипа шапки (W9a/W9b); без него подвкладка живёт по selectedKey */
export function WikiPanelBody({ tab, selectedKey, sectionScope }: {
  tab: WikiSubTabId; selectedKey: string | null; sectionScope?: string | null | 'all';
}) {
  const t = panelTabs().find(x => x.id === tab);
  if (!t) return null;
  const Comp = t.component;
  return (
    <div data-testid="wiki-panel-body" style={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
      {Comp
        ? <Comp selectedKey={selectedKey} sectionScope={sectionScope} />
        : <div style={{ padding: SP.sm }}><WikiSubTabStub label={t.label} when={t.stubNote ?? ''} /></div>}
    </div>
  );
}

// Подпись чипа: короткое имя раздела (макет — «Общее», «Личное», имя проекта или сферы)
function chipLabelOf(key: string, sections: readonly WikiSectionDto[]): string {
  const s = sections.find(x => x.key === key);
  const ref = parseSectionKey(key);
  if (ref?.kind === 'common') return 'Общее';
  if (ref?.kind === 'personal') return 'Личное';
  return s?.name ?? (ref?.kind === 'sphere' ? 'Сфера' : 'Раздел');
}

const PANEL_SCOPE_KEY = { inbox: 'wikiInbox', journal: 'wikiJournal', health: 'wikiHealth' } as const;

/** Панель вики в зоне раздела «Заметки» (W3): тело подвкладки; у «Входящих», «Журнала» и «Здоровья» в
 *  закреплённом слоте шапки — чип области. Состояния открытости и ширины здесь нет — их держит стор зон.
 *  followKey — раздел открытой страницы (или умолчание): чип в режиме follow идёт за ним.
 *  Пока вики не начата (нет разделов), чипа нет, а телу область не передаётся. */
export function WikiZonePanel({ tab, followKey, sections, started }: {
  tab: Exclude<WikiSubTabId, 'pages'>; followKey: string | null;
  sections: readonly WikiSectionDto[]; started: boolean;
}) {
  if (tab === 'settings') return <WikiPanelBody tab="settings" selectedKey={null} />;
  return <WikiScopedPanel tab={tab} followKey={followKey} sections={sections} started={started} />;
}

function WikiScopedPanel({ tab, followKey, sections, started }: {
  tab: 'inbox' | 'journal' | 'health'; followKey: string | null;
  sections: readonly WikiSectionDto[]; started: boolean;
}) {
  const [value, setValue] = useWikiPanelScope(PANEL_SCOPE_KEY[tab]);
  const scope = resolveWikiScope(value, { followKey, sectionKeys: sections.map(x => x.key) });
  const label = scope === 'all' ? SECTION_CHIP_ALL_LABEL : scope === null ? 'Раздел' : chipLabelOf(scope, sections);
  return (
    <>
      {started && (
        <PanelHeaderSlot pinned>
          <SectionChip
            value={value} label={label} onChange={setValue}
            options={sections.map(x => ({ key: x.key, label: chipLabelOf(x.key, sections) }))}
            allowAll={tab !== 'health'} />
        </PanelHeaderSlot>
      )}
      {/* Здоровью область нужна одним разделом (selectedKey), остальным — sectionScope */}
      <WikiPanelBody tab={tab} selectedKey={scope === 'all' ? null : scope}
        sectionScope={tab === 'health' ? undefined : scope} />
    </>
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

/** Шторка на 360: подложка + выезжающая панель слева или справа.
 *  Доступность: aria-modal, Esc закрывает, фокус уходит внутрь при открытии и возвращается
 *  на элемент, бывший в фокусе до неё (кнопку-триггер), при закрытии. Esc — слушатель document,
 *  как у Modal: общий useEscapeClose тут не годится — он молчит при любом [role=dialog] в
 *  документе, а сама шторка и есть такой диалог. */
export function WikiDrawer({ side, label, id, onClose, children }: {
  side: 'left' | 'right'; label: string; id?: string; onClose: () => void; children: ReactNode;
}) {
  const left = side === 'left';
  const boxRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef(onClose);
  closeRef.current = onClose;

  useEffect(() => {
    const prev = document.activeElement as HTMLElement | null;
    boxRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented) return;
      e.preventDefault();
      closeRef.current();
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      prev?.focus?.();
    };
  }, []);

  return (
    <>
      <div
        data-testid="wiki-drawer-overlay"
        onClick={onClose}
        style={{ position: 'fixed', inset: 0, background: C.overlay, zIndex: Z.modal }}
      />
      <div
        ref={boxRef}
        id={id}
        tabIndex={-1}
        data-testid={`wiki-drawer-${side}`}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        style={{
          position: 'fixed', top: 0, bottom: 0, [left ? 'left' : 'right']: 0, zIndex: Z.modal + 1,
          width: `min(${left ? DRAWER_W : DRAWER_PANEL_W}px, 92vw)`, background: C.bgWhite,
          display: 'flex', flexDirection: 'column', boxShadow: SHADOW.dropdown, outline: 'none',
          borderRadius: left ? `0 ${R.sheet}px ${R.sheet}px 0` : `${R.sheet}px 0 0 ${R.sheet}px`,
        }}
      >
        {children}
      </div>
    </>
  );
}
