import { useMemo } from 'react';
import { Select, type SelectOption } from '../../components/ui/Select';
import { C, FONT, FS } from '../../lib/design';
import { zoneFromValue, zoneHint, zoneLabel, zoneToValue } from '../../lib/personaZone';
import type { PersonaScope, Project, Sphere } from '../../types';

export interface PersonaZoneValue {
  scope: PersonaScope;
  projectId?: string;
  sphereId?: string;
}

// Единый выбор зоны персоны (флаг spheres): «Во всех моих данных» / сферы / проекты.
// Под селектом — одна строка о последствиях выбора.
export function PersonaZoneSelect({ value, onChange, projects, spheres, disabled }: {
  value: PersonaZoneValue;
  onChange: (zone: PersonaZoneValue) => void;
  projects: Project[];
  spheres: Sphere[];
  disabled?: boolean;
}) {
  const options = useMemo<SelectOption[]>(() => [
    { value: 'global', label: zoneLabel({ scope: 'global' }) },
    ...spheres.map(s => ({
      value: `sphere:${s.id}`,
      label: zoneLabel({ scope: 'sphere', sphereId: s.id }, { sphereName: () => s.name }),
      group: 'Сферы',
    })),
    ...projects.map(p => ({
      value: `project:${p.id}`,
      label: zoneLabel({ scope: 'project', projectId: p.id }, { projectName: () => p.name }),
      group: 'Проекты',
    })),
  ], [projects, spheres]);

  const hint = zoneHint(value, projects.filter(p => p.groupId === value.sphereId).length);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <Select
        value={zoneToValue(value)}
        onChange={v => onChange(zoneFromValue(v || 'global'))}
        options={options}
        disabled={disabled}
        title="Зона персоны"
      />
      <span style={{ fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{hint}</span>
    </div>
  );
}
