import { PillSwitch } from './Toolbar';
import { useFeature, FLAGS } from '../lib/featureFlags';

export type HubTab = 'chats' | 'team' | 'projects';

// Сегмент-переключатель хаба «Чаты | Проекты | Сотрудники» — на общем PillSwitch.
// Вкладка «Сотрудники» (глобальный пул ролей) — за фич-флагом roles (dark launch).
export function HubTabs({ value, onChange }: { value: HubTab; onChange: (t: HubTab) => void }) {
  const rolesEnabled = useFeature(FLAGS.roles);
  return (
    <PillSwitch<HubTab>
      value={value}
      onChange={onChange}
      options={[
        { value: 'chats', label: 'Чаты' },
        { value: 'projects', label: 'Проекты' },
        ...(rolesEnabled ? [{ value: 'team' as HubTab, label: 'Сотрудники' }] : []),
      ]}
    />
  );
}
