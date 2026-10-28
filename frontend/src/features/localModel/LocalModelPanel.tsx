// Панель «Локальные модели» — остров правого рельса. Админ — вкладки
// «LLM · Медиа»; не-админ — только «Медиа» (без переключателя, без опроса /stats).
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
import { Clapperboard, Cpu, MessageSquareText } from 'lucide-react';
import { C, FONT, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../lib/subsystems/registryCore';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { InlineSegmented, PanelHeaderSlot, useHasPanelHeader } from '../../components/ui';
import { openChatById } from '../../lib/openChat';
import {
  isLocalModelPanelVisible,
  useLocalModelAdmin,
  useLocalModelStats,
} from './useLocalModelStats';
import { isLocalModelConfiguredAny } from './useLocalModelWatcher';
import { useLocalModelMedia } from './useLocalModelMedia';
import { useLocalModelEngines } from './useLocalModelEngines';
import { useLocalModelHardware, DEFAULT_HARDWARE_WINDOW, type HardwareWindow } from './useLocalModelHardware';
import { TextTab } from './TextTab';
import { MediaTab } from './MediaTab';
import { HardwareTab } from './HardwareTab';
import { DEFAULT_WINDOW, type Window as StatsWindow } from './ThroughputChart';

// Вкладка запоминается на устройство (localStorage): «LLM · Медиа · Железо» — выбор
// человека, не состояние сессии.
const TAB_KEY = 'cc_local_model_tab';
type Tab = 'text' | 'media' | 'hardware';
function loadTab(): Tab {
  try {
    const v = localStorage.getItem(TAB_KEY);
    if (v === 'media' || v === 'hardware' || v === 'text') return v;
  } catch { /* приватный режим */ }
  return 'text';
}

// Чистая логика активной вкладки (тестируема без React):
//   админ — сохранённая вкладка (text/media/hardware);
//   не-админ — всегда «Медиа», кроме случая, когда в localStorage 'hardware'
//   (оставлено админом) — тогда «LLM», чтобы не показать недоступную вкладку.
export function resolveTab(isAdmin: boolean, stored: Tab): Tab {
  if (isAdmin) return stored;
  return stored === 'hardware' ? 'text' : 'media';
}

// Переключатель «LLM · Медиа · Железо»: InlineSegmented, активный C.accentLight/C.accent.
// Точка (tabdot) 6 px на «Железо» при warn/danger — видна с любой вкладки; при stale нет.
function TabSwitch({ value, onChange, isMobile, dotLevel }: {
  value: Tab; onChange: (t: Tab) => void; isMobile: boolean; dotLevel?: 'warn' | 'danger' | null;
}) {
  return (
    <InlineSegmented<Tab>
      value={value}
      isMobile={isMobile}
      size="sm"
      options={[
        { value: 'text', label: 'LLM', icon: <MessageSquareText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
        { value: 'media', label: 'Медиа', icon: <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
        {
          value: 'hardware', label: 'Железо',
          icon: (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 3 }}>
              <Cpu size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              {dotLevel && (
                <span style={{ width: 6, height: 6, borderRadius: '50%', background: dotLevel === 'danger' ? C.danger : C.warning, display: 'inline-block' }} />
              )}
            </span>
          ),
        },
      ]}
      onChange={onChange}
    />
  );
}

export function LocalModelPanel({ ctx }: { ctx: WorkspacePanelDefCtx }) {
  const flag = useFeature(FLAGS.localModelStats);
  // Роль и опрос configured — в useLocalModelWatcher (смонтирован на странице всегда)
  const isAdmin = useLocalModelAdmin();

  // Тяжёлый 2-с опрос: только когда панель видна (открыта + флаг + admin +
  // configured); не видна — опрос стоит, таймера нет. «Настроен ли» — одно выражение
  // со значком рельса (isLocalModelConfiguredAny: основной/медиа ИЛИ последний
  // снимок второй дорожки): с другой формулой был бы значок без панели (пустая
  // карточка). Ререндер при смене — подписка сторожа на странице.
  const visible = isLocalModelPanelVisible({ flag, isAdmin, configured: isLocalModelConfiguredAny() });

  // Не-админ: вкладка всегда «Медиа» — переключателя нет, /stats (LLM) не опрашивается.
  // Если в localStorage 'hardware' (оставлено админом) и пользователь не админ —
  // открываем 'text', чтобы не показать недоступную вкладку (спецификация).
  const [storedTab, setStoredTab] = useState<Tab>(loadTab);
  const tab: Tab = resolveTab(isAdmin, storedTab);
  const changeTab = useCallback((t: Tab) => {
    setStoredTab(t);
    try { localStorage.setItem(TAB_KEY, t); } catch { /* приватный режим */ }
  }, []);

  // Окно графика (5m/15m/1h): держим здесь, чтобы и тяжёлый опрос (сервер отдаёт
  // средние за это окно), и вкладка «LLM» видели один и тот же выбор.
  // Дефолт 5m (DEFAULT_WINDOW): выбор не персистентен, новый пользователь видит 5m.
  const [window, setWindow] = useState<StatsWindow>(DEFAULT_WINDOW);

  // Окно графика «Температуры» вкладки «Железо»: своё, не общее с LLM; дефолт 15m.
  const [hwWindow, setHwWindow] = useState<HardwareWindow>(DEFAULT_HARDWARE_WINDOW);

  // Движок: список с сервера и выбор человека — стор Ф1 (useLocalModelEngines,
  // localStorage). Активен, только когда видна вкладка «LLM» (переключатель и стенд
  // живут там). Тот же engine читает тяжёлый опрос (URL stats?engine=) и TextTab:
  // опрос, стенд и переключатель видят один и тот же выбор.
  const { engines, engine, setEngine } = useLocalModelEngines(visible && tab === 'text');

  // Тяжёлый 2-с опрос — только АКТИВНОЙ вкладки (неактивная на паузе):
  // «LLM» — stats, «Медиа» — media, «Железо» — hardware; переключение — немедленный снимок.
  const { data: textData, loading: textLoading, error: textError, refetch: textRefetch } = useLocalModelStats(visible && tab === 'text', window, engine);
  const { data: mediaData, loading: mediaLoading, refetch: mediaRefetch } = useLocalModelMedia(visible && tab === 'media');
  const { data: hwData, loading: hwLoading } = useLocalModelHardware(visible && tab === 'hardware', hwWindow);

  // Точка на вкладке «Железо» (6 px, C.warning/C.danger): при warn/danger, видна с
  // любой вкладки; при stale — нет.
  const dotLevel: 'warn' | 'danger' | null =
    hwData && hwData.status !== 'stale' && (hwData.level === 'warn' || hwData.level === 'danger')
      ? hwData.level : null;

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
        ? <TextTab data={textData} loading={textLoading} error={textError} isMobile={ctx.isMobile} onOpenChat={onOpenChat} onRefetch={textRefetch} onOpenHardware={() => changeTab('hardware')} onOpenMedia={() => changeTab('media')} window={window} onWindowChange={setWindow} engines={engines} engine={engine} onEngineChange={setEngine} />
        : tab === 'hardware'
          ? <HardwareTab data={hwData} loading={hwLoading} isMobile={ctx.isMobile} window={hwWindow} onWindowChange={setHwWindow} />
          : <MediaTab data={mediaData} loading={mediaLoading} isMobile={ctx.isMobile} onOpenChat={onOpenChat} onRefetch={mediaRefetch} onOpenHardware={() => changeTab('hardware')} />}
    </div>
  );

  // Без PanelShell-шапки (мобила) переключатель — фиксированной строкой в теле,
  // над прокручиваемым блоком: не скроллится вместе с ним.
  const mobileSwitcher = !inHeader && isAdmin && (
    <div style={{ flexShrink: 0, padding: `${SP.md}px ${SP.md}px 0` }}>
      <TabSwitch value={tab} onChange={changeTab} isMobile={ctx.isMobile} dotLevel={dotLevel} />
    </div>
  );

  return (
    <>
      {inHeader && isAdmin && (
        <PanelHeaderSlot side="left">
          <TabSwitch value={tab} onChange={changeTab} isMobile={ctx.isMobile} dotLevel={dotLevel} />
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
