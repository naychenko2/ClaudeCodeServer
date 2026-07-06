import { useState } from 'react';
import type { ModelOption } from '../lib/models';
import { modelProvider, providerLabel } from '../lib/models';
import { C, R, FONT } from '../lib/design';

// Выбор модели строками-карточками (как карточки режимов): название + бейдж окна
// справа + описание подзаголовком. Группировка по провайдеру (Claude первой),
// заголовок группы над её моделями. Курируемые модели видны сразу; легаси
// (curated:false, без описания) свёрнуты под «Другие модели <провайдер> (N)».
// Выбранная модель — accent-рамка и accentLight-фон.
interface Props {
  value: string;
  options: ModelOption[];
  onChange: (v: string) => void;
  columns?: number; // не используется — оставлен для совместимости вызовов
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

export function ModelPicker({ value, options, onChange }: Props) {
  // Развёрнутость блоков легаси, по ключу провайдера
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});

  const providerOf = (o: ModelOption) => o.provider ?? modelProvider(o.value);

  // Группы в порядке первого появления, Claude всегда первой
  const keys: string[] = [];
  for (const o of options) {
    const k = providerOf(o);
    if (!keys.includes(k)) keys.push(k);
  }
  keys.sort((a, b) => (a === 'claude' ? -1 : b === 'claude' ? 1 : 0));

  const renderRows = (list: ModelOption[]) =>
    list.map(o => (
      <ModelRow key={o.value} option={o} active={o.value === value} onClick={() => onChange(o.value)} />
    ));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: keys.length > 1 ? 12 : 5 }}>
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
                  <span style={{
                    display: 'inline-block', transition: 'transform 0.15s',
                    transform: isOpen ? 'rotate(90deg)' : 'none', fontSize: 10,
                  }}>
                    ▶
                  </span>
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
                        onClick={() => onChange(o.value)}
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
  );
}
