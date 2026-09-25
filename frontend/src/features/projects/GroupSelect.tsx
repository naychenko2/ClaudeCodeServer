import type { ProjectGroup } from '../../types';
import { Select } from '../../components/ui';

interface Props {
  groups: ProjectGroup[];
  value: string;                    // groupId или '' для «без группы»
  onChange: (groupId: string) => void;
}

// Селект группы для диалогов проекта.
export function GroupSelect({ groups, value, onChange }: Props) {
  return (
    <Select
      value={value}
      onChange={onChange}
      placeholder="Без группы"
      options={groups.map(g => ({ value: g.id, label: g.name }))}
    />
  );
}
