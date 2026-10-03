// Панель «Голоса» отдельной панелью зоны (ADR-023 §Д1, 2з-3): библиотека живёт сама, вкладки «Звук → Голоса» нет. Голос не основной объект, а референс с ролью «голос»:
// «В контекст» / «В контексте ✓» по контексту этого чата. «Обучить голос» — кнопка под списком.

import { Mic } from 'lucide-react';
import { GenerationPanel, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
import { audioScope } from '../scope';
import { VoicesTab } from './VoicesTab';

export function VoicesContextPanel({ ctx, layout = 'column' }: { ctx: WorkspacePanelDefCtx; layout?: 'column' | 'sheet' }) {
  const { sessionId } = ctx;
  const scope = audioScope(ctx.projectId);
  return (
    <GenerationPanel
      title="Голоса"
      icon={<Mic size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      footContent={(
        <span style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Папка <code>voices/</code> проекта · «В контекст» кладёт голос в озвучку
        </span>
      )}
      onClose={ctx.onClose}
      layout={layout}
    >
      <div style={{ paddingTop: SP.sm }}>
        <VoicesTab scope={scope} sessionId={sessionId} contextSessionId={sessionId} />
      </div>
    </GenerationPanel>
  );
}
