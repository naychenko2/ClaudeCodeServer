// «Чем: **Авто** · локально · Qwen3-TTS — бесплатно ▾» и список «Исполнитель» под ним (вариант А,
// docs/mockups/audio-panel-v3-proposal.md). Строки — executorRows.ts, рисует общий ExecutorList.

import { useState } from 'react';
import { ExecutorList, ExecutorSummaryRow, SP } from 'aihome_shell/kit';
import type { AudioCatalog } from '../api';
import { executorPatch, executorRows, executorSummary, executorValue } from './executorRows';
import type { PanelState, SettingsPatch } from './model';

export function ExecutorField({ catalog, state, personal, price, onChange, isMobile }: {
  catalog: AudioCatalog;
  state: PanelState;
  // Личный чат: «Локально» с замком
  personal: boolean;
  // Первая строка цены низа: «Бесплатно», «≈ $0.1 за 1000 симв.»
  price: string;
  onChange: (patch: SettingsPatch) => void;
  isMobile: boolean;
}) {
  const [open, setOpen] = useState(false);
  const { name, parts } = executorSummary(catalog, state);
  const free = state.provider?.priceUnit === 'free';
  return (
    <div data-sound-executor={open ? 'open' : 'closed'} style={{ marginTop: SP.md }}>
      <ExecutorSummaryRow name={name} parts={parts} price={{ label: price, tone: free ? 'success' : 'neutral' }}
        open={open} onToggle={() => setOpen(o => !o)} isMobile={isMobile} />
      {open && (
        <div style={{ marginTop: SP.xs }}>
          <ExecutorList rows={executorRows(catalog, state.op, personal)} value={executorValue(catalog, state)} isMobile={isMobile}
            onChange={id => onChange(executorPatch(id))} />
        </div>
      )}
    </div>
  );
}
