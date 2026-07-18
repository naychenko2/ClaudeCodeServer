import { useState } from 'react';
import { History } from 'lucide-react';
import type { HomeSessionInfo } from '../../types';
import { api } from '../../lib/api';
import type { HubTab } from '../../components/HubTabs';
import { WidgetCard, WidgetAction, WidgetEmpty, MiniSegment } from './WidgetCard';
import { SessionRow, openSession } from './SessionRow';

// Режим показа: только чаты вне проектов или вместе с проектными сессиями
type RecentMode = 'all' | 'chats';
const MODE_KEY = 'cc_home_recent_mode';
const SHOWN = 8;

// «Недавние»: последние завершенные/простаивающие чаты и сессии по всем проектам —
// быстрый возврат к работе одним кликом. Режим фильтра персистится per-устройство.
export function RecentSessionsWidget({ recent, onHubTab }: {
  recent: HomeSessionInfo[];
  onHubTab: (t: HubTab) => void;
}) {
  // Дефолт — «Только чаты»; выбор персистится per-устройство (localStorage)
  const [mode, setMode] = useState<RecentMode>(() =>
    localStorage.getItem(MODE_KEY) === 'all' ? 'all' : 'chats');
  const changeMode = (m: RecentMode) => {
    localStorage.setItem(MODE_KEY, m);
    setMode(m);
  };

  const shown = (mode === 'chats' ? recent.filter(s => !s.projectId) : recent).slice(0, SHOWN);

  // «+» — новый чат вне проекта: создаем и отдаем listener'у App (cc-open-chat),
  // тот переключит раздел «Чаты» и откроет его
  const [creating, setCreating] = useState(false);
  const newChat = async () => {
    if (creating) return;
    setCreating(true);
    try {
      const chat = await api.chats.create();
      window.dispatchEvent(new CustomEvent('cc-open-chat', { detail: { chatId: chat.id } }));
    } catch {
      setCreating(false);
    }
  };

  return (
    <WidgetCard
      icon={<History size={16} strokeWidth={2} />}
      title="Недавние чаты"
      onCreate={() => void newChat()}
      createTitle="Новый чат"
      action={
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 10 }}>
          <MiniSegment<RecentMode>
            value={mode}
            onChange={changeMode}
            options={[
              { value: 'chats', label: 'Только чаты', title: 'Чаты вне проектов' },
              { value: 'all', label: 'С проектами', title: 'Чаты + сессии проектов' },
            ]}
          />
          <WidgetAction label="Все чаты →" onClick={() => onHubTab('chats')} />
        </span>
      }
    >
      {shown.length === 0
        ? <WidgetEmpty text={mode === 'chats' ? 'Чатов вне проектов пока нет.' : 'Пока пусто — начни первый чат.'} />
        : (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {/* В режиме «Только чаты» чип «Чат» избыточен — тут все строки чаты */}
            {shown.map(s => <SessionRow key={s.id} s={s} onOpen={openSession} hideChatBadge={mode === 'chats'} />)}
          </div>
        )}
    </WidgetCard>
  );
}
