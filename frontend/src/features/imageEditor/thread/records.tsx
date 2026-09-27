// Записи модуля в ленте чата (ADR-019 §2): module_record { module, recordType, data,
// fallback }. Ядро отдаёт их в слот chat-item-tool по ключу `${module}:${recordType}`;
// тихие строки модуль рисует из fallback — текст готовит сервер.

import type { ReactNode } from 'react';
import { GitBranch, Image as ImageIcon, Save, Zap } from 'lucide-react';
import { C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ChatItem } from '../../../types';

export const MODULE = 'imageeditor';
export const recordKey = (recordType: string) => `${MODULE}:${recordType}`;

export interface ModuleRecordView {
  module: string;
  recordType: string;
  data: unknown;
  fallback: string | null;
}

// Запись ленты как module_record; другие виды записей — null
export function recordOf(item: ChatItem): ModuleRecordView | null {
  const r = item as unknown as { kind?: unknown; module?: unknown; recordType?: unknown; data?: unknown; fallback?: unknown };
  if (r.kind !== 'module_record' || typeof r.recordType !== 'string') return null;
  return {
    module: typeof r.module === 'string' ? r.module : '',
    recordType: r.recordType,
    data: r.data,
    fallback: typeof r.fallback === 'string' ? r.fallback : null,
  };
}

function SysLine({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <div data-image-sysline="" style={{
      alignSelf: 'center', maxWidth: 420, margin: '0 auto', textAlign: 'center', overflowWrap: 'anywhere',
      fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45,
    }}>
      <span style={{ display: 'inline-flex', verticalAlign: '-2px', marginRight: SP.xs }}>{icon}</span>{children}
    </div>
  );
}

const ICONS: Record<string, typeof Zap> = {
  image_launch: Zap, image_saved: Save, image_stack_forked: GitBranch, image_focus: ImageIcon,
};

// «Вы запустили: …», «Сохранено как …», «Шаги 3–5 не пропали…», «Claude взял в работу: …»
export function ThreadSysLine({ ctx }: { ctx: ChatItemToolCtx }) {
  const rec = recordOf(ctx.item);
  if (!rec?.fallback) return null;
  const I = ICONS[rec.recordType] ?? ImageIcon;
  return <SysLine icon={<I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>{rec.fallback}</SysLine>;
}
