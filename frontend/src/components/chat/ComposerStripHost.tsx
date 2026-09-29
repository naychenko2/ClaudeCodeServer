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
import { C, SP } from '../../lib/design';
import { useComposerStrip } from '../../lib/composerStrips';
import { SLOT_COMPOSER_STRIP, useSlot } from '../../lib/subsystems/registry';
import type { ComposerStripApi, ComposerStripCtx, SlotContribution } from '../../lib/subsystems/registry';
import { Dot, IconSegmented } from '../ui';

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

  // Ряд иконок с едущей мягкой подложкой (IconSegmented, тихий вид) — переключается
  // движением, как пилюля главного меню. Высота 24 влезает и в свёрнутую строку
  // 30 px, и в slim-полосу телефона. Выбранная картинка, чья
  // полоса скрыта ручным выбором, — точка на иконке её полосы
  const switcher = strips.length > 1 ? (
    // Клик по переключателю в свёрнутой строке не должен её разворачивать
    <span data-composer-strip-switcher="" onClick={e => e.stopPropagation()}
      style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm, flexShrink: 0 }}>
      {/* quiet — без дорожки и белой плашки; persistKey — переключатель пересоздаётся
          вместе с полосой, и без памяти позиции плашка не ехала бы, а возникала */}
      <IconSegmented
        quiet
        persistKey="composer-strip"
        value={active!}
        options={strips.map(s => ({
          value: s.name!,
          label: hint(s),
          icon: pendingFocus && pendingFocus !== active && pendingFocus === s.name
            ? (
              <span style={{ position: 'relative', display: 'inline-flex' }}>
                {s.action!.icon}
                <span style={{ position: 'absolute', top: -3, right: -4, display: 'inline-flex', pointerEvents: 'none' }}>
                  <Dot color={C.accent} size={6} />
                </span>
              </span>
            )
            : s.action!.icon,
        }))}
        onChange={name => {
          // Щелчок по активной — свернуть/развернуть полосу (если она это умеет)
          if (name !== active) select(name);
          else if (current.action!.collapsible?.(avail) ?? true) setCollapsed(!collapsed);
        }}
      />
      <span style={{ width: 1, height: collapsed ? 16 : 22, background: C.divider, flexShrink: 0 }} />
    </span>
  ) : null;

  return <>{current.render!({ projectId, sessionId, isMobile, collapsed, setCollapsed, switcher })}</>;
}
