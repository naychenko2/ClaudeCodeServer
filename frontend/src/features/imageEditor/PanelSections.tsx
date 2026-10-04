// Секция попапа «Редактор»: «Быстрые действия».
// Логика входа задачи — в editorInputs.ts.

import { useState } from 'react';
import { Expand, Layers, ScanFace, Scissors, Sparkles, X } from 'lucide-react';
import { Button, SegmentedControl, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
import { SectionHint } from './EditorSections';
import {
  fallbackLabel, OUTPAINT_RATIOS, QUICK_ACTIONS, QUICK_LABEL,
  type OutpaintRatio, type QuickAction, type QuickRoute,
} from './editorInputs';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

// ── Быстрые действия ──

const QUICK_ICON: Record<QuickAction, typeof X> = {
  removeBackground: Layers, upscale: Sparkles, removeMarked: Scissors, outpaint: Expand, enhanceFaces: ScanFace,
};

export function QuickActions({ actions = QUICK_ACTIONS, blockReason, fallback, ratio, onRatio, onRun }: {
  // Какие действия показывать: того, чего не умеет ни один поставщик каталога, нет вовсе
  actions?: QuickAction[];
  // Пусто — действие доступно
  blockReason: (a: QuickAction) => string;
  // Поставщик полосы не умеет — другой, который умеет: запуск им в один клик, только этот раз
  fallback?: (a: QuickAction) => QuickRoute | null;
  ratio: OutpaintRatio;
  onRatio: (r: OutpaintRatio) => void;
  onRun: (a: QuickAction, provider?: string) => void;
}) {
  // Открытая панель пропорций «Дорисовать за края»: provider — разовый поставщик
  const [outpaint, setOutpaint] = useState<{ provider?: string } | null>(null);
  const offers = actions.flatMap(a => {
    const f = blockReason(a) && fallback ? fallback(a) : null;
    return f ? [{ a, f }] : [];
  });
  const outpaintOpen = outpaint && (outpaint.provider ? offers.some(o => o.a === 'outpaint') : !blockReason('outpaint'));
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
        {actions.map(a => {
          const why = blockReason(a);
          const on = a === 'outpaint' && !!outpaintOpen && !outpaint?.provider;
          return (
            <Button key={a} size="sm" pill variant={on ? 'ghostAccent' : 'ghostFilled'} disabled={!!why} title={why || undefined}
              leftIcon={ic(QUICK_ICON[a])} onClick={() => { if (a === 'outpaint') setOutpaint(v => (v && !v.provider ? null : {})); else { setOutpaint(null); onRun(a); } }}>
              {QUICK_LABEL[a]}
            </Button>
          );
        })}
      </div>
      {offers.map(({ a, f }) => (
        <div key={a} data-quick-fallback={a} style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
          <span style={{ flex: '1 1 150px', minWidth: 0, fontSize: FS.xs, color: C.textMuted }}>
            «{QUICK_LABEL[a]}»: {blockReason(a)}
          </span>
          <Button size="xs" variant={outpaint?.provider === f.provider && a === 'outpaint' ? 'ghostAccent' : 'secondary'}
            onClick={() => { if (a === 'outpaint') setOutpaint({ provider: f.provider }); else { setOutpaint(null); onRun(a, f.provider); } }}>
            {fallbackLabel(f)}
          </Button>
        </div>
      ))}
      {outpaintOpen && (
        <div data-outpaint="true" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', padding: SP.sm, borderRadius: R.lg, border: `1px solid ${C.border}`, background: C.bgWhite }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>Пропорции</span>
          <div style={{ flex: 1, minWidth: 150 }}>
            <SegmentedControl value={ratio} onChange={v => onRatio(v)} options={OUTPAINT_RATIOS.map(r => ({ value: r, label: r }))} />
          </div>
          <Button size="sm" variant="primary" onClick={() => { const p = outpaint?.provider; setOutpaint(null); onRun('outpaint', p); }}>
            {outpaint?.provider ? `Дорисовать · ${offers.find(o => o.a === 'outpaint')?.f.providerLabel ?? ''}` : 'Дорисовать'}
          </Button>
        </div>
      )}
      <SectionHint>Запускаются сразу, без промпта. Число вариантов и цена — как в поле промпта.</SectionHint>
    </div>
  );
}
