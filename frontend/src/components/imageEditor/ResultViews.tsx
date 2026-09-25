// Центральная зона редактора после «Сгенерировать»: прогресс (экран 4), варианты
// (экран 5) и ошибка (экран 8). Тексты — дословно из макета image-editor-v1.

import { useRef, useState, type PointerEvent as RPointerEvent } from 'react';
import { AlertTriangle, Check, Coins, Pencil, Sparkles, X } from 'lucide-react';
import { Button, SegmentedControl } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, FS, R, SHADOW, SP } from '../../lib/design';
import type { EditCost } from '../../api/imageEditor';
import type { JobFailure } from './useImageEditJob';
import { money, variantsWord } from './format';

const icon = (I: typeof Check) => <I size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;

const area = (mobile: boolean) => ({
  flex: mobile ? '0 0 auto' : 1, minHeight: 0, overflow: 'auto', padding: SP.lg,
  display: 'flex', flexDirection: 'column' as const, gap: SP.md,
});

export function GenerationView({ count, progress, onCancel, mobile }: {
  count: number; progress: number; onCancel: () => void; mobile: boolean;
}) {
  return (
    <div style={area(mobile)}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>Рисуем {variantsWord(count)}</span>
        <span style={{ fontSize: FS.sm, color: C.textMuted }}>обычно 20–40 секунд</span>
        <span style={{ flex: 1 }} />
        <Button size="sm" variant="secondary" leftIcon={icon(X)} onClick={onCancel}>Отменить</Button>
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: mobile ? '1fr 1fr' : 'repeat(auto-fill, minmax(180px, 1fr))', gap: SP.md }}>
        {Array.from({ length: count }, (_, i) => (
          <div key={i} style={{
            aspectRatio: '16 / 10', borderRadius: R.xl, background: C.bgInset, border: `1px solid ${C.borderLight}`,
            display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', padding: SP.sm, gap: SP.xs,
          }}>
            <span style={{ fontSize: FS.sm, color: C.textSecondary }}>Вариант {i + 1} · {Math.floor(progress)}%</span>
            <div style={{ height: 4, borderRadius: R.max, background: C.track, overflow: 'hidden' }}>
              <div style={{ width: `${progress}%`, height: '100%', background: C.accent, transition: 'width .25s linear' }} />
            </div>
          </div>
        ))}
      </div>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>
        Можно продолжать работать в проекте — готовые варианты дождутся вас здесь. Отмена до конца генерации кредиты не списывает.
      </div>
    </div>
  );
}

// Шторка «до / после»: ползунок тянется мышью или пальцем
function CompareSlider({ before, after }: { before: string; after: string }) {
  const [pos, setPos] = useState(50);
  const ref = useRef<HTMLDivElement>(null);
  const dragging = useRef(false);
  const move = (e: RPointerEvent) => {
    const r = ref.current?.getBoundingClientRect();
    if (r) setPos(Math.min(96, Math.max(4, ((e.clientX - r.left) / r.width) * 100)));
  };
  return (
    <div ref={ref} style={{ position: 'relative', userSelect: 'none', touchAction: 'none', cursor: 'ew-resize' }}
      onPointerDown={e => { dragging.current = true; e.currentTarget.setPointerCapture(e.pointerId); move(e); }}
      onPointerMove={e => { if (dragging.current) move(e); }}
      onPointerUp={() => { dragging.current = false; }}>
      <img src={after} alt="" draggable={false} style={{ display: 'block', width: '100%', borderRadius: R.lg }} />
      <img src={before} alt="" draggable={false} style={{
        position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'fill', borderRadius: R.lg,
        clipPath: `inset(0 ${100 - pos}% 0 0)`,
      }} />
      <div style={{ position: 'absolute', top: 0, bottom: 0, left: `${pos}%`, width: 2, background: C.bgWhite, boxShadow: SHADOW.card, transform: 'translateX(-1px)' }} />
      <Tag side="left">До</Tag>
      <Tag side="right">После</Tag>
    </div>
  );
}

function Tag({ side, children }: { side: 'left' | 'right'; children: string }) {
  return (
    <span style={{
      position: 'absolute', top: SP.sm, [side]: SP.sm, background: C.mediaScrim, color: C.onDark,
      fontSize: FS.xs, padding: `${SP.xxs}px ${SP.sm}px`, borderRadius: R.max,
    }}>{children}</span>
  );
}

export function VariantsView({ variants, variantUrl, before, cost, selected, onSelect, onApply, onBase, onMore, onBack, mobile }: {
  variants: number[];
  variantUrl: (n: number) => string;
  // Исходник для сравнения; null — рисовали с нуля, сравнивать не с чем
  before: string | null;
  cost: EditCost | null;
  selected: number;
  onSelect: (n: number) => void;
  onApply: () => void;
  onBase: () => void;
  onMore: () => void;
  onBack: () => void;
  mobile: boolean;
}) {
  const [compare, setCompare] = useState<'slider' | 'toggle'>('slider');
  const [showAfter, setShowAfter] = useState(true);
  const after = variantUrl(selected);
  return (
    <div style={area(mobile)}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>Готово: {variantsWord(variants.length)}</span>
        {cost && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textMuted }}>
            <Coins size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />Списано {money(cost.amount, cost.unit)}
          </span>
        )}
        <span style={{ flex: 1 }} />
        {before && (
          <div style={{ width: 200 }}>
            <SegmentedControl value={compare} onChange={setCompare}
              options={[{ value: 'slider', label: 'Шторка' }, { value: 'toggle', label: 'До / после' }]} />
          </div>
        )}
        {before && compare === 'toggle' && (
          <Button size="sm" variant="secondary" onClick={() => setShowAfter(v => !v)}>
            {showAfter ? 'Показать «до»' : 'Показать «после»'}
          </Button>
        )}
      </div>

      {/* Сравнение не шире, чем влезает по высоте: кнопки действий должны остаться на экране */}
      <div style={{ width: mobile ? '100%' : 'min(100%, calc((100vh - 380px) * 1.6))', alignSelf: 'center', flexShrink: 0 }}>
      {!before
        ? <img src={after} alt="" style={{ display: 'block', width: '100%', borderRadius: R.lg }} />
        : compare === 'slider'
          ? <CompareSlider before={before} after={after} />
          : (
            <div style={{ position: 'relative' }}>
              <img src={showAfter ? after : before} alt="" style={{ display: 'block', width: '100%', borderRadius: R.lg }} />
              <Tag side="left">{showAfter ? 'После' : 'До'}</Tag>
            </div>
          )}
      </div>

      {/* flexShrink: 0 — у прокручиваемой строки min-height 0, колонка сжала бы её в полоску */}
      <div style={{ display: 'flex', gap: SP.sm, overflowX: 'auto', paddingBottom: SP.xxs, flexShrink: 0 }}>
        {variants.map((n, i) => (
          <Button key={n} variant={n === selected ? 'ghostAccent' : 'ghost'} size="sm" onClick={() => onSelect(n)}
            title={`Вариант ${i + 1}`}
            style={{ padding: SP.xxs, flexShrink: 0, border: `2px solid ${n === selected ? C.accent : C.borderLight}` }}>
            <span style={{ position: 'relative', display: 'block' }}>
              <img src={variantUrl(n)} alt="" style={{ display: 'block', width: mobile ? 88 : 112, aspectRatio: '16 / 10', objectFit: 'cover', borderRadius: R.md }} />
              <span style={{
                position: 'absolute', left: SP.xs, top: SP.xs, background: C.mediaScrim, color: C.onDark,
                fontSize: FS.xs, borderRadius: R.max, padding: `0 ${SP.xs}px`,
              }}>{i + 1}</span>
            </span>
          </Button>
        ))}
      </div>

      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        <Button variant="primary" leftIcon={icon(Check)} onClick={onApply}>Применить</Button>
        <Button variant="secondary" leftIcon={icon(Pencil)} onClick={onBase}>Взять за основу</Button>
        <Button variant="secondary" leftIcon={icon(Sparkles)} onClick={onMore}>Ещё варианты</Button>
        <Button variant="ghost" onClick={onBack}>Вернуться к правке</Button>
      </div>
      <div style={{ fontSize: FS.sm, color: C.textMuted, textAlign: 'center' }}>
        «Применить» сохранит вариант новым файлом рядом с оригиналом. «Взять за основу» — продолжить править этот вариант.
      </div>
    </div>
  );
}

export function ErrorView({ failure, providerLabel, priceUnit, needText, oneVariantPrice, onRetry, onRetryOne, onEdit, mobile }: {
  failure: JobFailure;
  providerLabel: string;
  priceUnit: string;
  // «≈ 6 кредитов, 3 варианта» — сколько стоил запуск
  needText: string | null;
  oneVariantPrice: string | null;
  onRetry: () => void;
  onRetryOne: () => void;
  onEdit: () => void;
  mobile: boolean;
}) {
  const credits = failure.outcome === 'insufficientCredits';
  const title = credits
    ? (priceUnit === 'usd' ? `Не хватает денег на балансе ${providerLabel}` : 'Не хватает кредитов')
    : 'Сервис рисования не ответил';
  const text = credits
    ? `${needText ? `На этот запуск нужно ${needText}. ` : ''}Можно попросить меньше вариантов или пополнить счёт.`
    : 'Сервис, который рисует картинки, сейчас отказал. Так бывает при большой нагрузке — обычно помогает повтор через минуту.';
  // «Кредиты не списаны» — только при точном charged=false (ADR-016, раздел 4)
  const kept = failure.charged === false
    ? 'Запрос, пометки и образцы сохранены. Кредиты не списаны.'
    : 'Запрос, пометки и образцы сохранены. Списание уточняется у сервиса.';
  const Ic = credits ? Coins : AlertTriangle;
  return (
    <div style={{ ...area(mobile), alignItems: 'center', justifyContent: 'center' }}>
      <div style={{ maxWidth: 440, display: 'flex', flexDirection: 'column', gap: SP.md, alignItems: 'flex-start' }}>
        <div style={{ width: 44, height: 44, borderRadius: R.full, background: C.dangerBg, color: C.danger, display: 'grid', placeItems: 'center' }}>
          <Ic size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />
        </div>
        <div style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>{title}</div>
        <div style={{ fontSize: FS.base, color: C.textSecondary, lineHeight: 1.5 }}>{text}</div>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.successText }}>
          <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />{kept}
        </div>
        <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
          <Button variant="primary" onClick={onRetry}>Повторить</Button>
          {credits && oneVariantPrice
            ? <Button variant="secondary" onClick={onRetryOne}>Нарисовать 1 вариант ({oneVariantPrice})</Button>
            : <Button variant="secondary" onClick={onEdit}>Изменить запрос</Button>}
        </div>
      </div>
    </div>
  );
}
