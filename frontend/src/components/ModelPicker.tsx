import type { ModelOption } from '../lib/models';
import { C, FONT } from '../lib/design';
import { SegmentedControl } from './ui';

// Выбор модели с визуальным разделением по провайдеру: группа Claude и группа DeepSeek
// под своими подписями. Если моделей одного провайдера — рендерим плоско, без заголовка.
interface Props {
  value: string;
  options: ModelOption[];
  onChange: (v: string) => void;
  columns?: number;
}

const PROVIDER_LABEL: Record<string, string> = {
  claude: 'Claude',
  deepseek: 'DeepSeek',
};

const groupHeaderStyle: React.CSSProperties = {
  fontFamily: FONT.sans, fontSize: 11, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: 0.4, margin: '2px 0 6px',
};

export function ModelPicker({ value, options, onChange, columns = 2 }: Props) {
  // Провайдер опции: явный provider, иначе по префиксу value (deepseek-*), иначе claude
  const providerOf = (o: ModelOption) =>
    o.provider ?? (o.value.toLowerCase().startsWith('deepseek') ? 'deepseek' : 'claude');

  const claude = options.filter(o => providerOf(o) === 'claude');
  const deepseek = options.filter(o => providerOf(o) === 'deepseek');

  // Только один провайдер — привычный плоский список без заголовков
  if (claude.length === 0 || deepseek.length === 0) {
    return <SegmentedControl value={value} options={options} onChange={onChange} columns={columns} />;
  }

  const group = (label: string, opts: ModelOption[]) => (
    <div>
      <div style={groupHeaderStyle}>{label}</div>
      <SegmentedControl value={value} options={opts} onChange={onChange} columns={columns} />
    </div>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      {group(PROVIDER_LABEL.claude, claude)}
      {group(PROVIDER_LABEL.deepseek, deepseek)}
    </div>
  );
}
