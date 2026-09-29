// Хост полос над композером: одна полоса за раз из реестра `composer-strip`
// плюс встроенные полосы каркаса (Git — вкладом ChatPanel, ему нужны колбэки чата).
// Какая полоса активна и свёрнута ли она — решает стор lib/composerStrips.ts (фокус
// картинки → запомненная полоса чата → Git; свёрнутость — своя у каждой полосы чата).
// Хост рисует заголовок-переключатель «Git ▾» / «Картинки ▾» один раз для всех полос
// и отдаёт его полосе в ctx.switcher: полоса ставит его на место своего заголовка.
// В меню переключателя у каждой полосы строка состояния и пункт «Свернуть полосу в
// строку» / «Развернуть полосу»; на телефоне то же меню открывается шторкой (прототип
// docs/mockups/image-editor-v3-strips-prototype.html, вариант C). Пока полоса одна,
// переключателя нет — полоса выглядит как раньше.
import { useEffect, useState } from 'react';
import type { MouseEvent, ReactNode } from 'react';
import { Check, ChevronDown, ChevronsDownUp, ChevronsUpDown } from 'lucide-react';
import { C, FS, SP } from '../../lib/design';
import { useComposerStrip } from '../../lib/composerStrips';
import { SLOT_COMPOSER_STRIP, useSlot } from '../../lib/subsystems/registry';
import type { ComposerStripApi, ComposerStripCtx, SlotContribution } from '../../lib/subsystems/registry';
import { Button, Dot, Menu, MenuItem, MenuSep, Modal } from '../ui';
import { ICON_STROKE } from '../ui/icons';

export type ComposerStripContribution = SlotContribution<ComposerStripCtx, ComposerStripApi>;

function ItemLabel({ title, status }: { title: string; status: ReactNode }) {
  return (
    <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
      <span>{title}</span>
      {status != null && status !== '' && (
        <span style={{
          fontSize: FS.xs, color: C.textMuted, marginTop: 1,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{status}</span>
      )}
    </span>
  );
}

export function ComposerStripHost({ projectId, sessionId, isMobile, builtins = [] }: {
  projectId: string;
  sessionId: string | null;
  isMobile: boolean;
  builtins?: ComposerStripContribution[];
}) {
  const fromSlot = useSlot<ComposerStripCtx, ComposerStripApi>(SLOT_COMPOSER_STRIP);
  const [menu, setMenu] = useState<DOMRect | null>(null);
  const [sheet, setSheet] = useState(false);
  // Меню с якорем само Esc не ловит — закрываем здесь, как меню фиксации в ProjectGitBar
  useEffect(() => {
    if (!menu) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenu(null); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [menu]);

  const avail = { projectId, sessionId };
  // Встроенная полоса главнее одноимённого вклада: id полосы уникален
  const strips = [...builtins, ...fromSlot.filter(c => !builtins.some(b => b.name === c.name))]
    .filter(c => c.name && c.render && c.action && (c.action.isAvailable?.(avail) ?? true))
    .sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
  const ids = strips.map(c => c.name!);
  const { active, pendingFocus, select, collapsed, setCollapsed } = useComposerStrip(sessionId, ids, isMobile);
  const current = strips.find(c => c.name === active);
  if (!current) return null;

  const close = () => { setMenu(null); setSheet(false); };

  const items = (
    <>
      {strips.map(s => (
        <MenuItem
          key={s.name}
          icon={s.action!.icon}
          isMobile={isMobile}
          label={
            <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
              <span style={{ flex: 1, minWidth: 0 }}>
                <ItemLabel title={s.action!.title} status={s.action!.status?.(avail)} />
              </span>
              {s.name === active && <Check size={14} strokeWidth={ICON_STROKE} color={C.accent} style={{ flexShrink: 0 }} />}
            </span>
          }
          onClick={() => { close(); select(s.name!); }}
        />
      ))}
      {(current.action!.collapsible?.(avail) ?? true) && (
        <>
          <MenuSep />
          <MenuItem
            icon={collapsed ? <ChevronsUpDown size={15} strokeWidth={ICON_STROKE} /> : <ChevronsDownUp size={15} strokeWidth={ICON_STROKE} />}
            isMobile={isMobile}
            label={collapsed ? 'Развернуть полосу' : 'Свернуть полосу в строку'}
            onClick={() => { close(); setCollapsed(!collapsed); }}
          />
        </>
      )}
    </>
  );

  // Выбранная картинка, чья полоса скрыта ручным выбором, — точка на «▾»
  const dot = !!pendingFocus && pendingFocus !== active;
  const switcher = strips.length > 1 ? (
    // Клик по переключателю в свёрнутой строке не должен её разворачивать
    <span data-composer-strip-switcher="" onClick={e => e.stopPropagation()}
      style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm, flexShrink: 0 }}>
      <span style={{ position: 'relative', display: 'inline-flex' }}>
        <Button
          variant="ghost" size="xs"
          leftIcon={current.action!.icon}
          title={`${current.action!.title} — сменить полосу над полем ввода`}
          style={{ fontWeight: 600, color: C.textHeading, paddingLeft: SP.xs, paddingRight: SP.xs }}
          onClick={(e: MouseEvent) => {
            if (isMobile) setSheet(true);
            else setMenu((e.currentTarget as HTMLElement).getBoundingClientRect());
          }}
        >
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
            {!isMobile && current.action!.title}
            <ChevronDown size={12} strokeWidth={ICON_STROKE} color={C.textMuted} />
          </span>
        </Button>
        {dot && (
          <span title="Картинка выбрана — её полоса сейчас не показана"
            style={{ position: 'absolute', top: 2, right: 0, display: 'inline-flex', pointerEvents: 'none' }}>
            <Dot color={C.accent} size={7} />
          </span>
        )}
      </span>
      <span style={{ width: 1, height: collapsed ? 16 : 22, background: C.divider, flexShrink: 0 }} />
      {menu && (
        <Menu anchor={menu} onClose={() => setMenu(null)} minWidth={290} maxWidth={360} maxHeight={200}>
          {items}
        </Menu>
      )}
      {sheet && (
        <Modal title="Полоса над полем ввода" onClose={() => setSheet(false)}>
          <div style={{ display: 'flex', flexDirection: 'column' }}>{items}</div>
        </Modal>
      )}
    </span>
  ) : null;

  return <>{current.render!({ projectId, sessionId, isMobile, collapsed, setCollapsed, switcher })}</>;
}
