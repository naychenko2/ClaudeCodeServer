import { useState } from 'react';
import type { PersonaAccess } from '../../types';
import { Badge, Checkbox, Chip, SegmentedControl, TextField } from '../../components/ui';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { toggleToolKey, type AccessDiff } from './personaInheritModel';

// Группа «Права» формы персоны (макет persona-inheritance.html, группа «Права»):
// карточка с переключателем «Как у специальности / Своё». При наследовании контролы
// отключены и показывают унаследованные значения, при «Своём» — редактируются, а
// отличия от специальности помечены «≠».

export const ACCESS_TOOL_LABELS: { key: string; label: string }[] = [
  { key: 'tasks', label: 'Задачи' },
  { key: 'notes', label: 'Заметки' },
  { key: 'web', label: 'Веб-поиск' },
];

const fieldLabel = { fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, marginTop: SP.md, marginBottom: SP.xs } as const;
const diffMark = { fontFamily: FONT.sans, fontSize: FS.sm, color: C.warningText, fontWeight: 600 } as const;
const hint = { fontFamily: FONT.sans, fontSize: FS.sm, lineHeight: 1.45, color: C.textMuted, marginTop: SP.sm } as const;

export interface PersonaAccessGroupProps {
  inherited: boolean;
  onModeChange: (own: boolean) => void;
  // Режим меняет только владелец проекта: участнику контролы отключены
  readOnly?: boolean;
  readOnlyHint?: string;
  specialtyKey: string;
  specialtyTitle: string;
  // Значения: при наследовании — эффективные (шаблон специальности), при «Своём» — свои
  access: PersonaAccess;
  tools: string[];
  disallowed: string[];
  onAccess: (a: PersonaAccess) => void;
  onTools: (t: string[]) => void;
  onDisallowed: (d: string[]) => void;
  // Отличия от специальности (только для «Своё»); null — сравнивать не с чем
  diff: AccessDiff | null;
}

export function PersonaAccessGroup(p: PersonaAccessGroupProps) {
  const [adding, setAdding] = useState(false);
  const [draft, setDraft] = useState('');
  const locked = p.inherited || !!p.readOnly;
  const hasSpecialty = p.specialtyKey !== 'none';

  const commitDraft = () => {
    const name = draft.trim();
    if (name && !p.disallowed.includes(name)) p.onDisallowed([...p.disallowed, name]);
    setDraft('');
    setAdding(false);
  };

  const toggleTool = (key: string, on: boolean) => p.onTools(toggleToolKey(p.tools, key, on));

  return (
    <div data-testid="access-group">
      <div style={{ fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4, marginBottom: SP.sm }}>
        Группы, которые приходят от специальности
      </div>
      <div style={{
        background: p.inherited ? C.bgCard : C.bgWhite,
        border: `1px solid ${C.border}`, borderRadius: R.lg, padding: SP.lg,
      }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          <span style={{ fontFamily: FONT.sans, fontSize: FS.md, fontWeight: 700, color: C.textPrimary }}>Права</span>
          {!p.inherited && <Badge tone="plan" size="xs">своё</Badge>}
          <span style={{ flex: 1 }} />
          <div style={{ minWidth: 260 }}>
            <SegmentedControl<'inherited' | 'own'>
              value={p.inherited ? 'inherited' : 'own'}
              onChange={v => p.onModeChange(v === 'own')}
              columns={2}
              options={[
                { value: 'inherited', label: 'Как у специальности', disabled: !!p.readOnly },
                { value: 'own', label: 'Своё', disabled: !!p.readOnly },
              ]}
            />
          </div>
        </div>

        {p.inherited && (
          <div data-testid="access-source" style={{ ...hint, marginTop: SP.sm }}>
            {hasSpecialty ? (
              <>↳ от специальности «<a href={`#/specialties/${p.specialtyKey}`} style={{ color: C.textMuted, textDecoration: 'underline' }}>{p.specialtyTitle}</a>» · обновится вместе с ней</>
            ) : (
              <>↳ по умолчанию: полный доступ без ограничений</>
            )}
          </div>
        )}

        <div style={fieldLabel}>Профиль доступа</div>
        <SegmentedControl<PersonaAccess>
          value={p.access}
          onChange={p.onAccess}
          columns={3}
          options={[
            { value: 'full', label: 'Полный', disabled: locked },
            { value: 'readOnly', label: 'Только чтение', disabled: locked },
            { value: 'custom', label: 'Свой', disabled: locked },
          ]}
        />
        {!p.inherited && p.diff?.access && <div style={diffMark}>≠ у специальности другой профиль</div>}

        <div style={fieldLabel}>Инструменты продукта</div>
        <div style={{ display: 'flex', gap: SP.lg, flexWrap: 'wrap' }}>
          {ACCESS_TOOL_LABELS.map(t => (
            <label key={t.key} style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm, fontFamily: FONT.sans, fontSize: FS.base, color: locked ? C.textMuted : C.textPrimary }}>
              <Checkbox
                checked={p.tools.includes(t.key)}
                onChange={v => toggleTool(t.key, v)}
                disabled={locked}
                ariaLabel={t.label}
              />
              {t.label}
              {!p.inherited && p.diff?.tools.includes(t.key) && <span data-testid={`access-diff-${t.key}`} style={diffMark}>≠</span>}
            </label>
          ))}
        </div>

        {(p.access === 'custom' || p.disallowed.length > 0) && (
          <>
            <div style={fieldLabel}>Запрещённые инструменты</div>
            <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', alignItems: 'center' }}>
              {p.disallowed.length === 0 && p.inherited && <span style={{ fontFamily: FONT.sans, fontSize: FS.base, color: C.textMuted }}>нет</span>}
              {p.disallowed.map(name => (
                <Chip
                  key={name}
                  onRemove={locked ? undefined : () => p.onDisallowed(p.disallowed.filter(d => d !== name))}
                  removeLabel={`Убрать запрет ${name}`}
                >
                  <span style={{ fontFamily: FONT.mono }}>{name}</span>
                </Chip>
              ))}
              {!p.inherited && p.diff && p.diff.extraDisallowed.length > 0 && (
                <span data-testid="access-diff-extra" style={diffMark}>≠ у специальности нет: {p.diff.extraDisallowed.join(', ')}</span>
              )}
              {!p.inherited && p.diff && p.diff.missingDisallowed.length > 0 && (
                <span data-testid="access-diff-missing" style={diffMark}>≠ у специальности запрещено: {p.diff.missingDisallowed.join(', ')}</span>
              )}
              {!locked && !adding && <Chip dashed onClick={() => setAdding(true)} keyboard>+ Инструмент</Chip>}
              {!locked && adding && (
                <div style={{ minWidth: 160 }}>
                  <TextField
                    value={draft}
                    onChange={setDraft}
                    placeholder="Bash"
                    autoFocus
                    onEnter={commitDraft}
                    onEscape={() => { setDraft(''); setAdding(false); }}
                    onBlur={commitDraft}
                  />
                </div>
              )}
            </div>
          </>
        )}

        {p.inherited && (
          <div style={hint}>
            {p.readOnly && p.readOnlyHint ? p.readOnlyHint
              : 'Править нельзя, пока права наследуются. Нужно иначе — «Своё»: скопируем текущие значения, дальше они ваши.'}
          </div>
        )}
        {!p.inherited && p.readOnly && p.readOnlyHint && <div style={hint}>{p.readOnlyHint}</div>}
      </div>
    </div>
  );
}
