import { PillSwitch } from './Toolbar';
import { useFeature, FLAGS } from '../lib/featureFlags';

export type HubTab = 'chats' | 'projects' | 'calendar';

// Сегмент-переключатель хаба «Чаты | Проекты | Календарь» — на общем PillSwitch.
// «Календарь» появляется только при включённом фич-флаге tasks.
export function HubTabs({ value, onChange }: { value: HubTab; onChange: (t: HubTab) => void }) {
  const tasksEnabled = useFeature(FLAGS.tasks);
  return (
    <PillSwitch<HubTab>
      value={value}
      onChange={onChange}
      options={[
        { value: 'chats', label: 'Чаты' },
        { value: 'projects', label: 'Проекты' },
        ...(tasksEnabled ? [{ value: 'calendar' as const, label: 'Календарь' }] : []),
      ]}
    />
  );
}
