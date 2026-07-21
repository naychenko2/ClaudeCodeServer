import { useState } from 'react';
import { ChevronDown, ChevronUp } from 'lucide-react';
import type { ModelOption } from '../lib/models';
import { modelProvider, providerLabel } from '../lib/models';
import { C, R, FONT } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

// Выбор модели строками-карточками (как карточки режимов): название + бейдж окна
// справа + описание подзаголовком. Группировка по провайдеру (Claude первой),
// заголовок группы над её моделями. Курируемые модели видны сразу; легаси
// (curated:false, без описания) свёрнуты под «Другие модели <провайдер> (N)».
// Выбранная модель — accent-рамка и accentLight-фон.
//
// collapsible (по умолчанию true): если модель уже выбрана, показываем только её
// строку-карточку + кнопку «Сменить»; полный список групп раскрывается по клику.
// Держит формы компактными (в EditSessionDialog модель всегда выбрана → нет скролла).
interface Props {
  value: string;
  options: ModelOption[];
  onChange: (v: string) => void;
  columns?: number;      // не используется — оставлен для совместимости вызовов
  collapsible?: boolean; // сворачивать до выбранной модели (по умолчанию true)
}

const groupHeaderStyle: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: 0.4, margin: '2px 0 6px',
};

// Компактная подпись окна контекста: 1_000_000 → «1M», 200_000 → «200K»
function formatWindow(tokens?: number): string | null {
  if (!tokens || tokens <= 0) return null;
  if (tokens >= 1_000_000) {
    const m = tokens / 1_000_000;
    return `${Number.isInteger(m) ? m : m.toFixed(1)}M`;
  }
  return `${Math.round(tokens / 1000)}K`;
}

// Бейдж размера контекстного окна
function WindowBadge({ tokens }: { tokens?: number }) {
  const label = formatWindow(tokens);
  if (!label) return null;
  return (
    <span style={{
      flexShrink: 0, fontFamily: FONT.mono, fontSize: 10.5, fontWeight: 600,
      color: C.textMuted, background: C.bgPanel, borderRadius: R.sm,
      padding: '1px 6px', lineHeight: 1.5, whiteSpace: 'nowrap',
    }}>
      {label}
    </span>
  );
}

// Одна строка-карточка модели. compact — для легаси (только имя + окно, без описания)
function ModelRow({
  option, active, compact, onClick,
}: { option: ModelOption; active: boolean; compact?: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      style={{
        width: '100%', display: 'flex', alignItems: 'flex-start', gap: 8,
        padding: compact ? '6px 10px' : '8px 10px', borderRadius: R.md, cursor: 'pointer',
        textAlign: 'left', border: `1px solid ${active ? C.accent : C.border}`,
        background: active ? C.accentLight : C.bgWhite,
      }}
    >
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{
            flex: 1, minWidth: 0, fontSize: 13, fontWeight: 600,
            color: active ? C.textHeading : C.textPrimary,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {option.label}
          </span>
          <WindowBadge tokens={option.contextWindow} />
        </span>
        {option.description && (
          <span style={{
            display: 'block', fontSize: 11.5, color: C.textMuted, marginTop: 2, lineHeight: 1.35,
          }}>
            {option.description}
          </span>
        )}
      </span>
    </button>
  );
}

export function ModelPicker({ value, options: allOptions, onChange, collapsible = true }: Props) {
  // Развёрнутость блоков легаси, по ключу провайдера
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  // Показ полного списка при сворачивании (collapsible)
  const [listOpen, setListOpen] = useState(false);

  const providerOf = (o: ModelOption) => o.provider ?? modelProvider(o.value);

  // Бесплатные модели прямого HTTP-адаптера (openrouter-direct, id с префиксом "direct:")
  // предназначены только для фоновых one-shot задач — в чате идут агентские вызовы, где
  // они не годятся. Здесь их скрываем; выбор такой модели живёт лишь в диалоге «Фоновые задачи».
  const options = allOptions.filter(o =>
    providerOf(o) !== 'openrouter-direct' && !o.value.startsWith('direct:'));

  // Группы в порядке первого появления, Claude всегда первой
  const keys: string[] = [];
  for (const o of options) {
    const k = providerOf(o);
    if (!keys.includes(k)) keys.push(k);
  }
  keys.sort((a, b) => (a === 'claude' ? -1 : b === 'claude' ? 1 : 0));

  // Выбор модели: применяем и (в collapsible-режиме) сворачиваем список обратно
  const handlePick = (v: string) => {
    onChange(v);
    if (collapsible) setListOpen(false);
  };

  const renderRows = (list: ModelOption[]) =>
    list.map(o => (
      <ModelRow key={o.value} option={o} active={o.value === value} onClick={() => handlePick(o.value)} />
    ));

  // Свёрнутый вид: выбранная модель одной строкой + «Сменить». Работает, только
  // когда выбранная опция есть в списке (иначе показываем полный список).
  const selected = options.find(o => o.value === value);
  if (collapsible && !listOpen && selected) {
    return (
      <div style={{ display: 'flex', alignItems: 'stretch', gap: 8 }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <ModelRow option={selected} active onClick={() => setListOpen(true)} />
        </div>
        <button
          type="button"
          onClick={() => setListOpen(true)}
          style={{
            flexShrink: 0, display: 'flex', alignItems: 'center', gap: 5, padding: '0 12px',
            borderRadius: R.md, cursor: 'pointer',
            border: `1px solid ${C.border}`, background: C.bgWhite,
            fontFamily: FONT.sans, fontSize: 12.5, fontWeight: 600, color: C.accent,
          }}
        >
          Сменить
          <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        </button>
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: keys.length > 1 ? 12 : 5 }}>
      {/* Кнопка «свернуть» — только в collapsible-режиме, когда список раскрыт над выбранным */}
      {collapsible && selected && (
        <button
          type="button"
          onClick={() => setListOpen(false)}
          style={{
            alignSelf: 'flex-start', display: 'flex', alignItems: 'center', gap: 6,
            border: 'none', background: 'none', padding: '2px 2px', cursor: 'pointer',
            fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.textMuted,
          }}
        >
          <ChevronUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          Свернуть
        </button>
      )}
      {/* Моделей много (провайдеры + бесплатные) — фиксируем высоту списка и скроллим
          внутри него, чтобы список не распирал диалог целиком */}
      <div style={{
        display: 'flex', flexDirection: 'column', gap: keys.length > 1 ? 12 : 5,
        maxHeight: '46vh', overflowY: 'auto', margin: '0 -4px', padding: '2px 4px',
      }}>
      {keys.map(key => {
        const inGroup = options.filter(o => providerOf(o) === key);
        // Курируемые (с карточкой) отдельно от легаси (без описания)
        const curated = inGroup.filter(o => o.curated !== false);
        const legacy = inGroup.filter(o => o.curated === false);
        // Если выбрана легаси-модель — раскрываем блок, чтобы её было видно
        const legacyHasSelected = legacy.some(o => o.value === value);
        const isOpen = expanded[key] || legacyHasSelected;

        return (
          <div key={key} style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
            {keys.length > 1 && <div style={groupHeaderStyle}>{providerLabel(key)}</div>}
            {renderRows(curated)}

            {legacy.length > 0 && (
              <>
                <button
                  type="button"
                  onClick={() => setExpanded(s => ({ ...s, [key]: !isOpen }))}
                  disabled={legacyHasSelected}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 6, alignSelf: 'flex-start',
                    border: 'none', background: 'none', padding: '2px 2px',
                    cursor: legacyHasSelected ? 'default' : 'pointer',
                    fontFamily: FONT.sans, fontSize: 12, fontWeight: 600, color: C.textMuted,
                  }}
                >
                  {isOpen
                    ? <ChevronUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                    : <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
                  {isOpen
                    ? `Скрыть другие модели ${providerLabel(key)}`
                    : `Другие модели ${providerLabel(key)} (${legacy.length})`}
                </button>
                {isOpen && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
                    {legacy.map(o => (
                      <ModelRow
                        key={o.value}
                        option={o}
                        active={o.value === value}
                        compact
                        onClick={() => handlePick(o.value)}
                      />
                    ))}
                  </div>
                )}
              </>
            )}
          </div>
        );
      })}
      </div>
    </div>
  );
}
