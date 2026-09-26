// Поле «Промпт» (макет image-editor-v2, «.pcard»): на десктопе справа сверху, на телефоне
// внизу экрана. Авторазмер до потолка (240 / 132 px), дальше прокрутка внутри и подсказка
// «прокрутите ↓». Пока идёт генерация — только чтение и «Рисуем N вариантов… · Отменить».

import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { RotateCcw, Sparkles, X } from 'lucide-react';
import { Button, IconButton, SegmentedControl, TextArea, ICON_SIZE, ICON_STROKE, C, FS, R, SP } from 'aihome_shell/kit';
import { PriceLine, SectionLabel } from './ProviderModelPicker';
import { variantsWord } from './format';
import { PROMPT_MIN_H, promptHeight, promptOverflows } from './layout';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

export function PromptCard({
  prompt, onPrompt, placeholder, busy, busyCount, onCancel, count, onCount, priceSum,
  canGenerate, blockedReason, onGenerate, above, below, mobile, agentLabel, flash, draft, onRestoreDraft, countByAgent,
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
  // «✦ написал Claude»: промпт в поле поставил агент (ADR-018 §2); ручная правка метку снимает
  agentLabel?: string | null;
  // Растёт, когда поле заполнил агент: поле подсвечивается
  flash?: number;
  // Текст человека, который заменил агент, — «Вернуть мой текст»
  draft?: string | null;
  onRestoreDraft?: () => void;
  // Число вариантов поменял агент — рамка у переключателя
  countByAgent?: boolean;
}) {
  const box = useRef<HTMLDivElement>(null);
  const [overflow, setOverflow] = useState(false);
  // Подсветка гаснет через 1,4 с: погашенным считается номер вспышки, а не флаг
  const [litDone, setLitDone] = useState(0);
  const lit = !!flash && flash !== litDone;
  useEffect(() => {
    if (!flash) return;
    const t = setTimeout(() => setLitDone(flash), 1400);
    return () => clearTimeout(t);
  }, [flash]);

  useLayoutEffect(() => {
    const el = box.current?.querySelector('textarea');
    if (!el) return;
    el.style.height = 'auto';
    const sh = el.scrollHeight;
    el.style.height = `${promptHeight(sh, mobile)}px`;
    setOverflow(promptOverflows(sh, mobile));
  }, [prompt, mobile]);

  const countPicker = (
    <div data-count-agent={countByAgent ? '' : undefined} title={countByAgent ? 'Число вариантов поменял агент' : undefined}
      style={{ width: 132, borderRadius: R.lg, boxShadow: countByAgent ? `0 0 0 1px ${C.accent}` : undefined }}>
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
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: SP.xs }}>
          <SectionLabel>Промпт</SectionLabel>
          {agentLabel && <AgentTag>{agentLabel}</AgentTag>}
        </div>
        {prompt && !busy && (
          <IconButton size="xs" title="Очистить промпт" ariaLabel="Очистить промпт" onClick={() => onPrompt('')}>{ic(X)}</IconButton>
        )}
      </div>
      {above}
      <div ref={box} data-prompt-lit={lit ? '' : undefined} style={{
        position: 'relative', borderRadius: R.xl, transition: 'box-shadow .6s ease-out',
        boxShadow: lit ? `0 0 0 3px ${C.accentMuted}` : 'none',
      }}>
        <TextArea value={prompt} onChange={onPrompt} readOnly={busy} placeholder={placeholder}
          minHeight={PROMPT_MIN_H[mobile ? 'mobile' : 'desktop']}
          style={mobile ? { fontSize: 16 } : undefined} />
        {overflow && (
          <span style={{ position: 'absolute', right: SP.md, bottom: SP.xs, fontSize: FS.xs, color: C.textMuted, pointerEvents: 'none' }}>
            прокрутите ↓
          </span>
        )}
      </div>
      {draft && onRestoreDraft && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textMuted }}>
          <span style={{ flex: 1, minWidth: 0 }}>Ваш текст заменён — он сохранён</span>
          <Button size="sm" variant="ghost" leftIcon={ic(RotateCcw)} onClick={onRestoreDraft}>Вернуть мой текст</Button>
        </div>
      )}
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

// Метка «✦ … Claude» у поля и настроек, которые поменял агент (макет, «.by»)
export function AgentTag({ children }: { children: ReactNode }) {
  return (
    <span data-agent-tag="" style={{
      display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: FS.xs, lineHeight: 1.4, whiteSpace: 'nowrap',
      color: C.accent, background: C.accentLight, borderRadius: R.sm, padding: '1px 7px',
    }}>
      <Sparkles size={ICON_SIZE.xs - 2} strokeWidth={ICON_STROKE} />{children}
    </span>
  );
}
