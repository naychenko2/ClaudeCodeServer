// Панель «Локальные модели» — остров правого рельса (только админ).
//
// Каркас (остров, шапку с иконкой+названием и крестик) рисует контейнер панели
// рельса (PanelShell зоны, как у «Персонажей»/«Голосов»/«Команды»): панель
// отдаёт ему шапку и рисует только переключатель «LLM · Медиа» и тело
// активной вкладки. Фон тела наследуется от контентной зоны контейнера.
//
// Переключатель: на десктопе (шапка есть) живёт в шапке через PanelHeaderSlot
// side="left" — штатное место переключателей вида; там, где PanelShell-шапки
// нет (узкий планшет/телефон), — фиксированной строкой в теле, над
// прокручиваемым блоком. Двойной шапки нет: свою шапку и остров панель не
// рисует.
//
// Видимость: флаг + admin + configured — все три; не выполнено — null, и
// keyAvailable PanelZone прячет кнопку в рельсе.
//
// МОНТАЖ: вклад слота workspace-panel-def создаётся WorkspacePage/ChatsPage
// при КАЖДОМ рендере страницы, а PanelZone монтирует его только для ОТКРЫТЫХ
// панелей (layout = открытые). Поэтому панель смонтирована, только когда
// открыта, и тяжёлый 2-с опрос (useLocalModelStats) живёт ровно столько.
// Лёгкий 60-с опрос configured и роль живут НЕ здесь, а в useLocalModelWatcher,
// смонтированном на страницах всегда: панель монтируется только при
// configured=true, опрос внутри неё замкнул бы круг. Панель читает значения
// из модульного стора.

import { useCallback, useState } from 'react';
import { Clapperboard, MessageSquareText } from 'lucide-react';
import { FONT, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../lib/subsystems/registryCore';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { InlineSegmented, PanelHeaderSlot, useHasPanelHeader } from '../../components/ui';
import { openChatById } from '../../lib/openChat';
import {
  isLocalModelPanelVisible,
  useLocalModelAdmin,
  useLocalModelConfigured,
  useLocalModelStats,
} from './useLocalModelStats';
import { useLocalModelMedia } from './useLocalModelMedia';
import { TextTab } from './TextTab';
import { MediaTab } from './MediaTab';
import type { Window as StatsWindow } from './ThroughputChart';

// Вкладка запоминается на устройство (localStorage): «LLM · Медиа» — выбор
// человека, не состояние сессии
const TAB_KEY = 'cc_local_model_tab';
type Tab = 'text' | 'media';
function loadTab(): Tab {
  try { return localStorage.getItem(TAB_KEY) === 'media' ? 'media' : 'text'; } catch { return 'text'; }
}

// Переключатель «LLM · Медиа»: InlineSegmented, активный C.accentLight/C.accent
function TabSwitch({ value, onChange, isMobile }: { value: Tab; onChange: (t: Tab) => void; isMobile: boolean }) {
  return (
    <InlineSegmented<Tab>
      value={value}
      isMobile={isMobile}
      size="sm"
      options={[
        { value: 'text', label: 'LLM', icon: <MessageSquareText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
        { value: 'media', label: 'Медиа', icon: <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
      ]}
      onChange={onChange}
    />
  );
}

export function LocalModelPanel({ ctx }: { ctx: WorkspacePanelDefCtx }) {
  const flag = useFeature(FLAGS.localModelStats);
  // Роль и опрос configured — в useLocalModelWatcher (смонтирован на странице всегда)
  const isAdmin = useLocalModelAdmin();
  const configured = useLocalModelConfigured();

  // Тяжёлый 2-с опрос: только когда панель видна (открыта + флаг + admin +
  // configured); не видна — опрос стоит, таймера нет
  const visible = isLocalModelPanelVisible({ flag, isAdmin, configured });

  const [tab, setTab] = useState<Tab>(loadTab);
  const changeTab = useCallback((t: Tab) => {
    setTab(t);
    try { localStorage.setItem(TAB_KEY, t); } catch { /* приватный режим */ }
  }, []);

  // Окно графика (5m/15m/1h): держим здесь, чтобы и тяжёлый опрос (сервер отдаёт
  // средние за это окно), и вкладка «LLM» видели один и тот же выбор.
  const [window, setWindow] = useState<StatsWindow>('15m');

  // Тяжёлый 2-с опрос — только АКТИВНОЙ вкладки (неактивная на паузе):
  // «LLM» — stats, «Медиа» — media; переключение — немедленный снимок.
  const { data: textData, loading: textLoading, refetch: textRefetch } = useLocalModelStats(visible && tab === 'text', window);
  const { data: mediaData, loading: mediaLoading, refetch: mediaRefetch } = useLocalModelMedia(visible && tab === 'media');

  // Клик по строке «Кто на модели»: открыть чат; на мобиле после перехода лист
  // закрывается (макет: «клик открывает чат (на мобиле лист закрывается)»)
  const onOpenChat = useCallback((sessionId: string) => {
    void openChatById(sessionId).then(ok => { if (ok && ctx.isMobile) ctx.onClose(); });
  }, [ctx]);

  // Не видно — null: keyAvailable прячет кнопку в рельсе
  if (!visible) return null;

  // Есть ли над панелью шапка (PanelShell зоны). На десктопе и планшете — да:
  // переключатель уезжает в шапку (PanelHeaderSlot side="left"). Там, где
  // PanelShell-шапки нет (узкий планшет/телефон), — строкой в теле.
  const inHeader = useHasPanelHeader();

  // Тело активной вкладки: поток блоков, скролл держит контентная зона контейнера.
  const body = (
    <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: SP.md, padding: SP.md }}>
      {tab === 'text'
        ? <TextTab data={textData} loading={textLoading} isMobile={ctx.isMobile} onOpenChat={onOpenChat} onRefetch={textRefetch} window={window} onWindowChange={setWindow} />
        : <MediaTab data={mediaData} loading={mediaLoading} isMobile={ctx.isMobile} onOpenChat={onOpenChat} onRefetch={mediaRefetch} />}
    </div>
  );

  // Без PanelShell-шапки (мобила) переключатель — фиксированной строкой в теле,
  // над прокручиваемым блоком: не скроллится вместе с ним.
  const mobileSwitcher = !inHeader && (
    <div style={{ flexShrink: 0, padding: `${SP.md}px ${SP.md}px 0` }}>
      <TabSwitch value={tab} onChange={changeTab} isMobile={ctx.isMobile} />
    </div>
  );

  return (
    <>
      {inHeader && (
        <PanelHeaderSlot side="left">
          <TabSwitch value={tab} onChange={changeTab} isMobile={ctx.isMobile} />
        </PanelHeaderSlot>
      )}
      <div role="complementary" aria-label="Локальные модели"
        style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column', fontFamily: FONT.sans }}>
        {mobileSwitcher}
        {body}
      </div>
    </>
  );
}
