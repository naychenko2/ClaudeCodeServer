// Строка чипов действий над текстом поля ввода (ADR-023 §Д2, макет composer-actions-v1): «Чат» первым
// (его ставит хост), дальше действия вида. Режим (радио, сплошная рамка) решает, что сделает кнопка
// запуска; вход в редактор и меню (пунктир) выбор не меняют. Вопрос параметра — строкой под чипами.
// Здесь только отрисовка по готовой модели: операций и подписей вертикалей хост не знает, цену и подпись
// кнопки считает useActionRun.
import { useState, type KeyboardEvent } from 'react';
import { ChevronDown, MessageSquare, Pencil } from 'lucide-react';
import { actionChips, chipSelectable, CHAT_CHIP_ID, type ActionChip } from '../../lib/chatContext/actionChips';
import type { ContextAction } from '../../lib/chatContext/types';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { InlineSegmented, Menu, MenuItem } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';

export const ACTION_CHIP_H = 26;

export interface ComposerActionRowProps {
  actions: readonly ContextAction[];
  // Выбранное run-действие; null — «Чат»
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  // Ответ на вопрос выбранного действия; null — вопроса нет
  question: { value: string; onChange: (v: string) => void } | null;
  isMobile: boolean;
}

const act = (fn: () => void) => (e: KeyboardEvent) => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fn(); }
};

function Chip({ c, selected, onPick }: {
  c: ActionChip; selected: boolean; onPick: (rect: DOMRect | null) => void;
}) {
  const gray = c.kind === 'run' && !!c.action?.disabledReason;
  const solid = c.kind === 'chat' || c.kind === 'run';
  const click = (el: HTMLElement) => onPick(el.getBoundingClientRect());
  return (
    <span
      data-action-chip={c.id}
      data-kind={c.kind}
      {...(gray ? { 'data-gray': '' } : null)}
      role={solid ? 'radio' : 'button'}
      aria-checked={solid ? selected : undefined}
      aria-disabled={gray || undefined}
      tabIndex={0}
      title={c.hint}
      onClick={e => click(e.currentTarget)}
      onKeyDown={e => act(() => click(e.currentTarget as HTMLElement))(e)}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, boxSizing: 'border-box',
        height: ACTION_CHIP_H, padding: `0 ${SP.md - 2}px`, borderRadius: R.lg,
        border: `1px ${solid ? 'solid' : 'dashed'} ${selected ? C.accent : C.border}`,
        background: selected ? C.accentLight : C.bgCard, color: selected ? C.accent : C.textSecondary,
        fontSize: FS.sm, fontWeight: selected ? 600 : 400, fontFamily: FONT.sans, whiteSpace: 'nowrap',
        cursor: gray ? 'not-allowed' : 'pointer', opacity: gray ? 0.5 : 1,
      }}
    >
      {c.kind === 'chat' && <MessageSquare size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
      {c.kind === 'editor' && <Pencil size={ICON_SIZE.xs - 2} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
      {c.label}
      {c.kind === 'menu' && <ChevronDown size={ICON_SIZE.xs - 2} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
    </span>
  );
}

export function ComposerActionRow({ actions, selectedId, onSelect, question, isMobile }: ComposerActionRowProps) {
  // Бросает в dev на седьмом действии: вид обязан отдавать не больше пяти
  const chips = actionChips(actions);
  const [menu, setMenu] = useState<{ action: ContextAction; rect: DOMRect } | null>(null);
  const picked = selectedId ?? CHAT_CHIP_ID;
  const pick = (c: ActionChip, rect: DOMRect | null) => {
    if (c.kind === 'editor') { c.action?.open?.(); return; }
    if (c.kind === 'menu') { if (c.action && rect) setMenu({ action: c.action, rect }); return; }
    // Серое действие не выбирается: причина — в тултипе чипа
    if (!chipSelectable(c)) return;
    onSelect(c.kind === 'chat' ? null : c.id);
  };
  const items = menu?.action.items?.() ?? [];
  const q = selectedId ? actions.find(a => a.id === selectedId)?.question : undefined;
  return (
    <div data-composer-actions="" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0, padding: `0 ${SP.xxs}px ${SP.xs}px` }}>
      <div
        role="radiogroup" aria-label="Что сделает запуск"
        style={{ display: 'flex', alignItems: 'center', gap: SP.sm - 2, minWidth: 0, overflowX: 'auto', scrollbarWidth: 'none' }}
      >
        {chips.map(c => <Chip key={c.id} c={c} selected={c.id === picked} onPick={rect => pick(c, rect)} />)}
      </div>
      {q && question && (
        <div data-action-question="" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0, flexWrap: isMobile ? 'wrap' : 'nowrap' }}>
          <span style={{ fontSize: FS.sm, color: C.textMuted, flexShrink: 0 }}>{q.title}:</span>
          <InlineSegmented isMobile={isMobile} value={question.value} onChange={question.onChange}
            options={q.options.map(o => ({ value: o.value, label: o.label }))} />
        </div>
      )}
      {menu && (
        <Menu anchor={menu.rect} onClose={() => setMenu(null)} minWidth={220} maxWidth={320} preferUp anchorAlign="start">
          {items.map(it => (
            <MenuItem key={it.id} label={it.label} hint={it.disabledReason} disabled={!!it.disabledReason}
              onClick={() => { setMenu(null); it.run(); }} />
          ))}
        </Menu>
      )}
    </div>
  );
}
