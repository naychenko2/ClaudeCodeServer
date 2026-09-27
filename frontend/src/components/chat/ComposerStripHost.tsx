// Хост полос над композером: одна полоса за раз из реестра `composer-strip`
// плюс встроенные полосы каркаса (Git — вкладом ChatPanel, ему нужны колбэки чата).
// Какая полоса активна — решает стор lib/composerStrips.ts (фокус картинки →
// запомненная полоса чата → Git). Хост рисует переключатель «Git ▾» один раз для
// всех полос и отдаёт его полосе в ctx.switcher: полоса ставит его на место своего
// заголовка. Пока полоса одна, переключателя нет — полоса выглядит как раньше.
import { useState } from 'react';
import type { MouseEvent } from 'react';
import { ChevronDown, ChevronsDownUp, ChevronsUpDown } from 'lucide-react';
import { C, FS, SP } from '../../lib/design';
import { useComposerStrip } from '../../lib/composerStrips';
import { SLOT_COMPOSER_STRIP, useSlot } from '../../lib/subsystems/registry';
import type { ComposerStripApi, ComposerStripCtx, SlotContribution } from '../../lib/subsystems/registry';
import { Button, Dot, Menu, MenuItem, MenuSep } from '../ui';
import { ICON_STROKE } from '../ui/icons';

export type ComposerStripContribution = SlotContribution<ComposerStripCtx, ComposerStripApi>;

// Свёрнутое состояние одно на все полосы и на устройство
const COLLAPSED_KEY = 'cc-composer-strip-collapsed';

function readCollapsed(): boolean {
  try { return localStorage.getItem(COLLAPSED_KEY) === '1'; } catch { return false; }
}

export function ComposerStripHost({ projectId, sessionId, isMobile, builtins = [] }: {
  projectId: string;
  sessionId: string | null;
  isMobile: boolean;
  builtins?: ComposerStripContribution[];
}) {
  const fromSlot = useSlot<ComposerStripCtx, ComposerStripApi>(SLOT_COMPOSER_STRIP);
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const [menu, setMenu] = useState<DOMRect | null>(null);

  const avail = { projectId, sessionId };
  // Встроенная полоса главнее одноимённого вклада: id полосы уникален
  const strips = [...builtins, ...fromSlot.filter(c => !builtins.some(b => b.name === c.name))]
    .filter(c => c.name && c.render && c.action && (c.action.isAvailable?.(avail) ?? true))
    .sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
  const ids = strips.map(c => c.name!);
  const { active, pendingFocus, select } = useComposerStrip(sessionId, ids);
  const current = strips.find(c => c.name === active);
  if (!current) return null;

  const toggleCollapsed = () => {
    const next = !collapsed;
    setCollapsed(next);
    try { localStorage.setItem(COLLAPSED_KEY, next ? '1' : '0'); } catch { /* приватный режим */ }
  };

  const switcher = strips.length > 1 ? (
    <span style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
      <Button
        variant="ghost" size="xs"
        leftIcon={current.action!.icon}
        title="Сменить полосу над композером"
        onClick={(e: MouseEvent) => setMenu((e.currentTarget as HTMLElement).getBoundingClientRect())}
      >
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
          {current.action!.title}
          <ChevronDown size={12} strokeWidth={ICON_STROKE} />
          {/* Полоса, которую запросил выбор картинки, скрыта ручным выбором — напоминаем */}
          {pendingFocus && pendingFocus !== active && <Dot color={C.accent} size={6} />}
        </span>
      </Button>
      {menu && (
        <Menu anchor={menu} onClose={() => setMenu(null)} minWidth={240}>
          {strips.map(s => {
            const status = s.action!.status?.(avail);
            return (
              <MenuItem
                key={s.name}
                icon={s.action!.icon}
                label={
                  <span>
                    {s.action!.title}
                    {status != null && <span style={{ color: C.textMuted, fontSize: FS.sm }}> · {status}</span>}
                  </span>
                }
                onClick={() => { setMenu(null); select(s.name!); }}
              />
            );
          })}
          <MenuSep />
          <MenuItem
            icon={collapsed ? <ChevronsUpDown size={15} strokeWidth={ICON_STROKE} /> : <ChevronsDownUp size={15} strokeWidth={ICON_STROKE} />}
            label={collapsed ? 'Развернуть полосу' : 'Свернуть полосу в строку'}
            onClick={() => { setMenu(null); toggleCollapsed(); }}
          />
        </Menu>
      )}
    </span>
  ) : null;

  return <>{current.render!({ projectId, sessionId, isMobile, collapsed, switcher })}</>;
}
