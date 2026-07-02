import { PillSwitch } from './Toolbar';

export type HubTab = 'chats' | 'projects';

// Сегмент-переключатель хаба «Чаты | Проекты» — на общем PillSwitch.
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
      ]}
    />
  );
}
