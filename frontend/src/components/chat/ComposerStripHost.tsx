// Хост полос над композером: одна полоса за раз из реестра `composer-strip`
// плюс встроенные полосы каркаса (Git — вкладом ChatPanel, ему нужны колбэки чата).
// Какая полоса активна и свёрнута ли она — решает стор lib/composerStrips.ts (фокус
// картинки → запомненная полоса чата → Git; свёрнутость — своя у каждой полосы чата).
// Хост рисует переключатель полос один раз для всех и отдаёт его полосе в ctx.switcher:
// полоса ставит его на место своего заголовка. Переключатель — ряд иконок-тоглов, по
// одной на полосу: щелчок по чужой — показать её, по активной — свернуть полосу в
// строку / развернуть. Имя и строка состояния полосы — в подсказке иконки (раньше жили
// в выпадающем меню «Git ▾», которое стоило лишний клик и ~50 px ширины). Пока полоса
// одна, переключателя нет — полоса выглядит как раньше.
import type { MouseEvent } from 'react';
import { C, SP } from '../../lib/design';
import { useComposerStrip } from '../../lib/composerStrips';
import { SLOT_COMPOSER_STRIP, useSlot } from '../../lib/subsystems/registry';
import type { ComposerStripApi, ComposerStripCtx, SlotContribution } from '../../lib/subsystems/registry';
import { Dot, IconButton } from '../ui';

export type ComposerStripContribution = SlotContribution<ComposerStripCtx, ComposerStripApi>;

export function ComposerStripHost({ projectId, sessionId, isMobile, builtins = [] }: {
  projectId: string;
  sessionId: string | null;
  isMobile: boolean;
  builtins?: ComposerStripContribution[];
}) {
  const fromSlot = useSlot<ComposerStripCtx, ComposerStripApi>(SLOT_COMPOSER_STRIP);

  const avail = { projectId, sessionId };
  // Встроенная полоса главнее одноимённого вклада: id полосы уникален
  const strips = [...builtins, ...fromSlot.filter(c => !builtins.some(b => b.name === c.name))]
    .filter(c => c.name && c.render && c.action && (c.action.isAvailable?.(avail) ?? true))
    .sort((a, b) => (a.order ?? 0) - (b.order ?? 0));
  const ids = strips.map(c => c.name!);
  const { active, pendingFocus, select, collapsed, setCollapsed } = useComposerStrip(sessionId, ids, isMobile);
  const current = strips.find(c => c.name === active);
  if (!current) return null;

  // Подсказка иконки: имя полосы, её состояние и — у активной — что сделает щелчок
  const hint = (s: ComposerStripContribution) => {
    const status = s.action!.status?.(avail);
    const head = typeof status === 'string' && status ? `${s.action!.title} · ${status}` : s.action!.title;
    if (s.name !== active) return head;
    if (!(s.action!.collapsible?.(avail) ?? true)) return head;
    return `${head} — ${collapsed ? 'развернуть полосу' : 'свернуть полосу в строку'}`;
  };

  // Выбранная картинка, чья полоса скрыта ручным выбором, — точка на иконке её полосы
  const switcher = strips.length > 1 ? (
    // Клик по переключателю в свёрнутой строке не должен её разворачивать
    <span data-composer-strip-switcher="" onClick={e => e.stopPropagation()}
      style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0 }}>
      {strips.map(s => {
        const on = s.name === active;
        return (
          <span key={s.name} style={{ position: 'relative', display: 'inline-flex' }}>
            <IconButton
              size={collapsed ? 'xs' : isMobile ? 'md' : 'sm'}
              active={on}
              title={hint(s)}
              onClick={(e: MouseEvent) => {
                e.stopPropagation();
                if (!on) select(s.name!);
                else if (s.action!.collapsible?.(avail) ?? true) setCollapsed(!collapsed);
              }}
            >
              {s.action!.icon}
            </IconButton>
            {!!pendingFocus && pendingFocus !== active && pendingFocus === s.name && (
              <span title="Картинка выбрана — её полоса сейчас не показана"
                style={{ position: 'absolute', top: 1, right: 1, display: 'inline-flex', pointerEvents: 'none' }}>
                <Dot color={C.accent} size={7} />
              </span>
            )}
          </span>
        );
      })}
      <span style={{ width: 1, height: collapsed ? 16 : 22, background: C.divider, flexShrink: 0, marginLeft: SP.xs }} />
    </span>
  ) : null;

  return <>{current.render!({ projectId, sessionId, isMobile, collapsed, setCollapsed, switcher })}</>;
}
