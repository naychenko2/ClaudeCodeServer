import type { ProjectGroup } from '../../types';
import { Select } from '../../components/ui';
import { FLAGS, useFeature } from '../../lib/featureFlags';

interface Props {
  groups: ProjectGroup[];
  value: string;                    // groupId или '' для «без группы»
  onChange: (groupId: string) => void;
}

// Селект группы для диалогов проекта.
export function GroupSelect({ groups, value, onChange }: Props) {
  const spheres = useFeature(FLAGS.spheres);
  return (
    <Select
      value={value}
      onChange={onChange}
      placeholder={spheres ? 'Без сферы' : 'Без группы'}
      options={groups.map(g => ({ value: g.id, label: g.name }))}
    />
  );
}
