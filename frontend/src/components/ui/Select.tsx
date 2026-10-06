import { useState, type ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';
import { C, R, FIELD, SHADOW, FONT } from '../../lib/design';
import { ICON_SIZE } from './icons';

// Группа пунктов → <optgroup>. disabled гасит всю группу: так «С выбранным звуком»
// остаётся видна, но недоступна, пока звук не выбран
export interface SelectGroup {
  label: string;
  disabled?: boolean;
}

export interface SelectOption<T extends string = string> {
  value: T;
  label: string;
  disabled?: boolean;
  // Пункты с одинаковым label группы собираются в один <optgroup> на месте первого
  // из них; пункты без группы стоят на своих местах. disabled у группы — у любого
  // её пункта
  group?: string | SelectGroup;
}

interface Props<T extends string> {
  value: T | '';
  onChange: (value: T | '') => void;
  options: SelectOption<T>[];
  // Пустой первый пункт со значением '' («Без группы», «Выберите устройство»)
  placeholder?: string;
  disabled?: boolean;
  title?: string;
}

// Выпадающий список в стиле полей ввода (FIELD.*, фокус-тень, своя стрелка).
// Нативный <select> — корректно работает и на мобиле (системный пикер).
export function Select<T extends string = string>({ value, onChange, options, placeholder, disabled, title }: Props<T>) {
  const [focused, setFocused] = useState(false);
  return (
    <div style={{ position: 'relative', display: 'flex', alignItems: 'center' }}>
      <select
        value={value}
        onChange={e => onChange(e.target.value as T | '')}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        disabled={disabled}
        title={title}
        style={{
          appearance: 'none', WebkitAppearance: 'none', MozAppearance: 'none',
          width: '100%', boxSizing: 'border-box',
          background: FIELD.background,
          border: `1px solid ${focused ? FIELD.borderFocus : C.border}`,
          borderRadius: R.xl, padding: '10px 34px 10px 13px',
          fontSize: FIELD.fontSize, color: FIELD.color, fontFamily: FONT.sans,
          outline: 'none', cursor: disabled ? 'default' : 'pointer',
          opacity: disabled ? 0.6 : 1,
          boxShadow: focused ? SHADOW.focus : 'none',
          transition: 'border-color 0.15s, box-shadow 0.15s',
        }}
      >
        {placeholder !== undefined && <option value="">{placeholder}</option>}
        {renderOptions(options)}
      </select>
      <span style={{ position: 'absolute', right: 12, pointerEvents: 'none', color: C.textMuted, display: 'flex' }}>
        <ChevronDown size={ICON_SIZE.xs} strokeWidth={2} />
      </span>
    </div>
  );
}

function renderOptions<T extends string>(options: SelectOption<T>[]): ReactNode[] {
  const option = (o: SelectOption<T>) => <option key={o.value} value={o.value} disabled={o.disabled}>{o.label}</option>;
  const groups = new Map<string, { disabled: boolean; items: SelectOption<T>[] }>();
  const order: (SelectOption<T> | string)[] = [];
  for (const o of options) {
    if (o.group == null) { order.push(o); continue; }
    const g = typeof o.group === 'string' ? { label: o.group } : o.group;
    let entry = groups.get(g.label);
    if (!entry) {
      entry = { disabled: false, items: [] };
      groups.set(g.label, entry);
      order.push(g.label);
    }
    entry.disabled ||= !!g.disabled;
    entry.items.push(o);
  }
  return order.map(e => {
    if (typeof e !== 'string') return option(e);
    const g = groups.get(e)!;
    return <optgroup key={`group:${e}`} label={e} disabled={g.disabled || undefined}>{g.items.map(option)}</optgroup>;
  });
}
