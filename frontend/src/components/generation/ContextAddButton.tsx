// «В контекст ▾» (ADR-023, шаг 2к-2): одна кнопка на карточке ленты, в «Файлах» и в «Персонажах». Роль
// референса берётся у вида основного объекта (rolesFor): одна роль — кладём без вопроса, несколько —
// меню под кнопкой, ни одной — кнопка серая с причиной. Уже лежащий в контексте объект кнопка сменяет
// плашкой «✓ В контексте · роль» (в «Персонажах» — переключателем «В контексте ✓»).
import { useState } from 'react';
import { Check, ChevronDown, Plus } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Badge } from '../ui/Badge';
import { Button } from '../ui/Button';
import { Menu, MenuItem } from '../ui/Menu';
import { attachRef, detachRef, useChatContext } from '../../lib/chatContext/store';
import { refOf, rolesFor, type ContextCandidate } from '../../lib/chatContext/fill';
import { roleLabel } from '../../lib/chatContext/roleLabels';
import type { ContextKindCtx } from '../../lib/chatContext/types';

const ic = (I: typeof Check, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

export const NO_PRIMARY_REASON = 'Сначала выберите, с чем работать: «Работать с этой» на карточке ленты или в «Файлах»';
export const NO_ROLE_REASON = 'Выбранный объект такой референс не берёт';
// Подписи размера lg (главное действие карточки персонажа)
export const LG_ADD_LABEL = 'В контекст чата';
export const LG_IN_LABEL = 'В контексте';

export function ContextAddButton({ sessionId, projectId, candidate, size = 'xs', toggle, isMobile = false }: {
  sessionId: string;
  projectId: string | null;
  candidate: ContextCandidate;
  size?: 'xs' | 'sm' | 'lg';
  // Уже в контексте: true — кнопка «В контексте ✓» снимает референс, false — плашка без действия
  toggle?: boolean;
  isMobile?: boolean;
}) {
  const state = useChatContext(sessionId);
  const [at, setAt] = useState<DOMRect | null>(null);
  const kindCtx: ContextKindCtx = { projectId, sessionId, isMobile };
  const inCtx = refOf(state, candidate);

  const lg = size === 'lg';

  if (inCtx) {
    if (lg) {
      return (
        <span data-context-add="in" style={{ display: 'flex', width: '100%' }}>
          <Button variant="secondary" size="lg" fullWidth leftIcon={ic(Check, ICON_SIZE.sm)} title="Убрать из контекста"
            onClick={() => { void detachRef(sessionId, inCtx.id); }}>
            {LG_IN_LABEL}
          </Button>
        </span>
      );
    }
    const role = roleLabel(inCtx.role);
    if (toggle) {
      return (
        <span data-context-add="in" style={{ display: 'inline-flex' }}>
          <Button size={size} variant="secondary" leftIcon={ic(Check)} title="Убрать из контекста"
            onClick={() => { void detachRef(sessionId, inCtx.id); }}>
            {LG_IN_LABEL}
          </Button>
        </span>
      );
    }
    return <span data-context-add="in" style={{ display: 'inline-flex' }}><Badge size="xs" tone="success" icon={ic(Check)}>{`В контексте${role ? ` · ${role}` : ''}`}</Badge></span>;
  }

  const roles = rolesFor(kindCtx, state, candidate.kind);
  const reason = !state.primary ? NO_PRIMARY_REASON : roles.length === 0 ? NO_ROLE_REASON : undefined;
  const attach = (role: string) => { setAt(null); void attachRef(sessionId, { ...candidate, role }); };
  const many = roles.length > 1;
  const title = reason ?? (many ? 'Выбрать роль и положить в контекст' : 'Положить в контекст');
  const onAdd = (e: { currentTarget: EventTarget }) => {
    if (many) setAt((e.currentTarget as HTMLElement).getBoundingClientRect());
    else attach(roles[0].role);
  };
  const menu = at && (
    <Menu anchor={at} anchorAlign="start" minWidth={220} onClose={() => setAt(null)}>
      {roles.map(r => <MenuItem key={r.role} label={r.label} onClick={() => attach(r.role)} />)}
    </Menu>
  );
  if (lg) {
    return (
      <span data-context-add={reason ? 'off' : 'add'} style={{ display: 'flex', width: '100%' }}>
        <Button variant="primary" size="lg" fullWidth disabled={!!reason} title={title}
          leftIcon={ic(Plus, ICON_SIZE.sm)} onClick={onAdd}>
          {LG_ADD_LABEL}
        </Button>
        {menu}
      </span>
    );
  }
  return (
    <span data-context-add={reason ? 'off' : 'add'} style={{ display: 'inline-flex' }}>
      <Button size={size} variant="ghost" disabled={!!reason} title={reason ?? (many ? 'Выбрать роль и положить в контекст' : 'Положить в контекст')}
        onClick={e => {
          if (many) setAt((e.currentTarget as HTMLElement).getBoundingClientRect());
          else attach(roles[0].role);
        }}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>В контекст{many && ic(ChevronDown)}</span>
      </Button>
      {at && (
        <Menu anchor={at} anchorAlign="start" minWidth={220} onClose={() => setAt(null)}>
          {roles.map(r => <MenuItem key={r.role} label={r.label} onClick={() => attach(r.role)} />)}
        </Menu>
      )}
    </span>
  );
}
