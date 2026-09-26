// Поле «Промпт» (макет image-editor-v2, «.pcard»): на десктопе справа сверху, на телефоне
// внизу экрана. Авторазмер до потолка (240 / 132 px), дальше прокрутка внутри и подсказка
// «прокрутите ↓». Пока идёт генерация — только чтение и «Рисуем N вариантов… · Отменить».

import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { Sparkles, X } from 'lucide-react';
import { Button, IconButton, SegmentedControl, TextArea, ICON_SIZE, ICON_STROKE, C, FS, SP } from 'aihome_shell/kit';
import { PriceLine, SectionLabel } from './ProviderModelPicker';
import { variantsWord } from './format';
import { PROMPT_MIN_H, promptHeight, promptOverflows } from './layout';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

export function PromptCard({
  prompt, onPrompt, placeholder, busy, busyCount, onCancel, count, onCount, priceSum,
  canGenerate, blockedReason, onGenerate, above, below, mobile,
}: {
  prompt: string;
  onPrompt: (v: string) => void;
  placeholder: string;
  busy: boolean;
  busyCount: number;
  onCancel: () => void;
  count: number;
  onCount: (n: number) => void;
  // Только сумма: число вариантов видно рядом, в переключателе. null — цену не
  // показываем (рисовать нечем)
  priceSum: string | null;
  canGenerate: boolean;
  blockedReason: string;
  onGenerate: () => void;
  // Над полем (чип персонажа) и под ним (почему нельзя рисовать)
  above?: ReactNode;
  below?: ReactNode;
  mobile: boolean;
}) {
  const box = useRef<HTMLDivElement>(null);
  const [overflow, setOverflow] = useState(false);

  useLayoutEffect(() => {
    const el = box.current?.querySelector('textarea');
    if (!el) return;
    el.style.height = 'auto';
    const sh = el.scrollHeight;
    el.style.height = `${promptHeight(sh, mobile)}px`;
    setOverflow(promptOverflows(sh, mobile));
  }, [prompt, mobile]);

  const countPicker = (
    <div style={{ width: 132 }}>
      <SegmentedControl value={String(count)} onChange={v => onCount(Number(v))}
        options={['1', '2', '3', '4'].map(v => ({ value: v, label: v }))} />
    </div>
  );
  const generateBtn = (
    <Button variant="primary" size={mobile ? 'sm' : 'md'} fullWidth={!mobile} leftIcon={ic(Sparkles)} disabled={!canGenerate}
      title={blockedReason || undefined} onClick={onGenerate}>
      Сгенерировать
    </Button>
  );

  return (
    <div data-prompt-card="true" style={{
      flex: '0 0 auto', display: 'flex', flexDirection: 'column', gap: mobile ? SP.xs : SP.sm, background: C.bgPanel,
      padding: mobile ? `${SP.sm}px ${SP.md}px` : `${SP.md}px`,
      [mobile ? 'borderTop' : 'borderBottom']: `1px solid ${C.borderLight}`,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minHeight: 24 }}>
        <div style={{ flex: 1, minWidth: 0 }}><SectionLabel>Промпт</SectionLabel></div>
        {prompt && !busy && (
          <IconButton size="xs" title="Очистить промпт" ariaLabel="Очистить промпт" onClick={() => onPrompt('')}>{ic(X)}</IconButton>
        )}
      </div>
      {above}
      <div ref={box} style={{ position: 'relative' }}>
        <TextArea value={prompt} onChange={onPrompt} readOnly={busy} placeholder={placeholder}
          minHeight={PROMPT_MIN_H[mobile ? 'mobile' : 'desktop']}
          style={mobile ? { fontSize: 16 } : undefined} />
        {overflow && (
          <span style={{ position: 'absolute', right: SP.md, bottom: SP.xs, fontSize: FS.xs, color: C.textMuted, pointerEvents: 'none' }}>
            прокрутите ↓
          </span>
        )}
      </div>
      {below}
      {busy ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <span style={{ flex: 1, fontSize: FS.sm, color: C.textSecondary }}>Рисуем {variantsWord(busyCount)}…</span>
          <Button size="sm" variant="secondary" leftIcon={ic(X)} onClick={onCancel}>Отменить</Button>
        </div>
      ) : (
        <>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: mobile ? 'nowrap' : 'wrap' }}>
            {!mobile && <span style={{ fontSize: FS.sm, color: C.textMuted }}>Вариантов</span>}
            {countPicker}
            <span style={{ flex: 1 }} />
            {priceSum && <PriceLine text={priceSum} />}
            {mobile && generateBtn}
          </div>
          {!mobile && generateBtn}
        </>
      )}
    </div>
  );
}
