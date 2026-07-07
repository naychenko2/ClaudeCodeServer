import { PillSwitch } from './Toolbar';

export type HubTab = 'chats' | 'projects' | 'calendar';

// Сегмент-переключатель хаба «Чаты | Проекты | Календарь» — на общем PillSwitch.
export function HubTabs({ value, onChange }: { value: HubTab; onChange: (t: HubTab) => void }) {
  return (
    <PillSwitch<HubTab>
      value={value}
      onChange={onChange}
      draggable
      persistKey="hub-tabs"
      options={[
        { value: 'chats', label: 'Чаты' },
        { value: 'projects', label: 'Проекты' },
        { value: 'calendar', label: 'Календарь' },
      ]}
    />
  );
}
