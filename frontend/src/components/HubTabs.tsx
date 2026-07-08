import { PillSwitch } from './Toolbar';
import { useFeature, FLAGS } from '../lib/featureFlags';

export type HubTab = 'chats' | 'projects' | 'calendar' | 'notes';

// Сегмент-переключатель хаба «Чаты | Проекты | Календарь | Заметки» — на общем PillSwitch.
// Вкладка «Заметки» появляется только при включённом фич-флаге notes.
export function HubTabs({ value, onChange }: { value: HubTab; onChange: (t: HubTab) => void }) {
  const notesOn = useFeature(FLAGS.notes);
  const options = [
    { value: 'chats' as HubTab, label: 'Чаты' },
    { value: 'projects' as HubTab, label: 'Проекты' },
    { value: 'calendar' as HubTab, label: 'Календарь' },
    ...(notesOn ? [{ value: 'notes' as HubTab, label: 'Заметки' }] : []),
  ];
  return (
    <PillSwitch<HubTab>
      value={value}
      onChange={onChange}
      draggable
      persistKey="hub-tabs"
      options={options}
    />
  );
}
