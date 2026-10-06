// Панель «Персонажи» отдельной панелью зоны (ADR-023 §Д1, 2к-2): библиотека живёт сама, вкладки
// «Картинки → Персонажи» нет. Персонаж не основной объект, а референс с ролью
// «персонаж»: «В контекст» / «В контексте ✓» по контексту этого чата. Форма персонажа открывается здесь же.

import { useState } from 'react';
import { Contact, Plus, Users } from 'lucide-react';
import { Button, EmptyState, GenerationPanel, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { enterScope, isPersonalScope } from '../scope';
import { CharactersPanel, type CharacterEditing } from './CharactersPanel';
import { useCharacters } from './useCharacters';

export const CHARACTERS_PANEL = 'characters';

const ic = (I: typeof Users, size: number = ICON_SIZE.sm) => <I size={size} strokeWidth={ICON_STROKE} />;

export function CharactersContextPanel({ ctx, layout = 'column' }: { ctx: WorkspacePanelDefCtx; layout?: 'column' | 'sheet' }) {
  const { sessionId } = ctx;
  const projectId = enterScope(ctx.projectId, sessionId);
  const personal = isPersonalScope(projectId);
  const { list } = useCharacters(personal ? null : projectId);
  const [editing, setEditing] = useState<CharacterEditing>(null);

  const foot = !personal && !editing ? (
    <div data-characters-foot="" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm, color: C.textSecondary }}>
      <span style={{ flex: 1, minWidth: 0 }}>Папка <code>characters/</code> проекта · «В контекст» кладёт лицо в запрос</span>
      <Button size="xs" variant="secondary" leftIcon={ic(Plus, ICON_SIZE.xs)} onClick={() => setEditing({ kind: 'new' })} style={{ flexShrink: 0 }}>
        Персонаж
      </Button>
    </div>
  ) : undefined;

  return (
    <GenerationPanel
      title="Персонажи"
      subtitle={list?.length ? `${list.length}` : undefined}
      icon={ic(Contact)}
      footContent={foot}
      onClose={ctx.onClose}
      layout={layout}
    >
      {personal
        ? <EmptyState compact icon={ic(Users)} title="Персонажи живут в проекте"
            subtitle="Персонажи хранятся в папке characters/ проекта. В личном чате лицо можно передать образцом с ролью «Лицо»." />
        : <CharactersPanel projectId={projectId} editing={editing} onEditing={setEditing} contextSessionId={sessionId} />}
    </GenerationPanel>
  );
}
