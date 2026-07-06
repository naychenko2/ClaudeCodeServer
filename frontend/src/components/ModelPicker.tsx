import type { ModelOption } from '../lib/models';
import { modelProvider, providerLabel } from '../lib/models';
import { C, FONT } from '../lib/design';
import { SegmentedControl } from './ui';

// Выбор модели с визуальным разделением по провайдеру: группа Claude и группа
// каждого CLI-провайдера (DeepSeek, GLM, …) под своими подписями.
// Если все модели одного провайдера — рендерим плоско, без заголовка.
interface Props {
  value: string;
  options: ModelOption[];
  onChange: (v: string) => void;
  columns?: number;
}

const groupHeaderStyle: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: 0.4, margin: '2px 0 6px',
};

export function ModelPicker({ value, options, onChange, columns = 2 }: Props) {
  // Провайдер опции: явный provider, иначе по префиксу value (modelProvider)
  const providerOf = (o: ModelOption) => o.provider ?? modelProvider(o.value);

  // Группы в порядке первого появления, Claude всегда первой
  const keys: string[] = [];
  for (const o of options) {
    const k = providerOf(o);
    if (!keys.includes(k)) keys.push(k);
  }
  keys.sort((a, b) => (a === 'claude' ? -1 : b === 'claude' ? 1 : 0));

  // Только один провайдер — привычный плоский список без заголовков
  if (keys.length <= 1) {
    return <SegmentedControl value={value} options={options} onChange={onChange} columns={columns} />;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      {keys.map(key => (
        <div key={key}>
          <div style={groupHeaderStyle}>{providerLabel(key)}</div>
          <SegmentedControl
            value={value}
            options={options.filter(o => providerOf(o) === key)}
            onChange={onChange}
            columns={columns}
          />
        </div>
      ))}
    </div>
  );
}
