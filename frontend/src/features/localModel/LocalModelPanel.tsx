// Панель «Локальные модели» — остров правого рельса. Админ — вкладки
// «LLM · Медиа · Стенды · Встречи»; не-админ — только «Медиа» (без переключателя,
// без опроса /stats).
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

import { useCallback, useState, type ReactNode } from 'react';
import { AudioLines, Clapperboard, Cpu, MessageSquareText } from 'lucide-react';
import { C, FONT, SP, ICON_SIZE, ICON_STROKE, useContainerWidth } from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../lib/subsystems/registryCore';
import { InlineSegmented, PanelHeaderSlot, useHasPanelHeader } from '../../components/ui';
import { openChatById } from '../../lib/openChat';
import { panelMetaResolved } from '../../pages/workspace/panelCatalog';
import { LOCAL_MODEL_PANEL } from './manifest';
import {
  isLocalModelPanelVisible,
  useLocalModelAdmin,
  useLocalModelStats,
} from './useLocalModelStats';
import { isLocalModelConfiguredAny } from './useLocalModelWatcher';
import { useLocalModelMedia } from './useLocalModelMedia';
import { useLocalModelEngines } from './useLocalModelEngines';
import { useLocalModelHardware, useLocalModelFarm, DEFAULT_HARDWARE_WINDOW, type HardwareWindow } from './useLocalModelHardware';
import { useTranscription } from './useTranscription';
import type { TranscriptionSnapshot } from './types';
import { farmPickNode } from './hardwareFormat';
import { TextTab } from './TextTab';
import { MediaTab } from './MediaTab';
import { HardwareTab } from './HardwareTab';
import { TranscriptionTab } from './TranscriptionTab';
import { DEFAULT_WINDOW, type Window as StatsWindow } from './ThroughputChart';

// Вкладка запоминается на устройство (localStorage): «LLM · Медиа · Стенды · Встречи» —
// выбор человека, не состояние сессии.
const TAB_KEY = 'cc_local_model_tab';
type Tab = 'text' | 'media' | 'hardware' | 'transcription';
export function loadTab(): Tab {
  try {
    const v = localStorage.getItem(TAB_KEY);
    if (v === 'media' || v === 'hardware' || v === 'text' || v === 'transcription') return v;
  } catch { /* приватный режим */ }
  return 'text';
}

// «Узкая лента» (предложение v1): на листе <340 px четыре подписи не помещаются —
// неактивные вкладки остаются иконкой (aria-label + подсказка), активная — с подписью.
// Меряем ширину панели: она равна полной ширине шапки, но переключатель в шапке
// делит её с названием панели (сжимается с ellipsis, Island.tsx:74), иконкой и
// группой действий — под свои подписи у него меньше, чем измерено.
const TABS_NARROW_PX = 340;

// Чистая логика активной вкладки (тестируема без React):
//   админ — сохранённая вкладка (text/media/hardware/transcription);
//   не-админ — всегда «Медиа», при любом сохранённом значении. «LLM», «Стенды» и
//   «Встречи» — админские: их эндпоинты /stats, /hardware, /transcription под
//   [Authorize(Roles="admin")], и раньше не-админ с сохранённой админом вкладкой
//   попадал в 'text' → 403 → «Не удалось получить данные» без выхода из состояния
//   (дефект №3 приёмки Ф2/Ф6).
export function resolveTab(isAdmin: boolean, stored: Tab): Tab {
  if (isAdmin) return stored;
  return 'media';
}

// Иконка вкладки с точкой-состоянием (6 px, C.danger/C.warning) — та же геометрия, что у «Стендов».
function tabIcon(node: ReactNode, dotLevel?: 'warn' | 'danger' | null) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 3 }}>
      {node}
      {dotLevel && (
        <span style={{ width: 6, height: 6, borderRadius: '50%', background: dotLevel === 'danger' ? C.danger : C.warning, display: 'inline-block' }} />
      )}
    </span>
  );
}

// Точка на вкладке «Встречи»: уровень берётся из снимка (его считает бэкенд — пороги
// «Завис?» и красное/жёлтое условие здесь не дублируем). Снимка нет или уровень 'ok' —
// точки нет; точка видна с любой вкладки (снимок живёт, пока смонтирована панель).
export function transcriptionDot(snapshot: TranscriptionSnapshot | null): 'warn' | 'danger' | null {
  const level = snapshot?.level;
  return level === 'warn' || level === 'danger' ? level : null;
}

// Переключатель «LLM · Медиа · Стенды · Встречи»: InlineSegmented, активный C.accentLight/C.accent.
// Точки (tabdot) 6 px на «Стенды» и «Встречи» при warn/danger — видны с любой вкладки; при ok/stale нет.
// narrow (лист <340 px): неактивные вкладки — только иконка (имя в aria-label/title),
// активная — с подписью (предложение v1, «Узкая лента»).
export function TabSwitch({ value, onChange, isMobile, dotLevel, transDotLevel, narrow }: {
  value: Tab; onChange: (t: Tab) => void; isMobile: boolean;
  dotLevel?: 'warn' | 'danger' | null; transDotLevel?: 'warn' | 'danger' | null; narrow?: boolean;
}) {
  const options: ({ value: Tab; label: string; icon: ReactNode } & { title?: string })[] = [
    { value: 'text', label: 'LLM', icon: <MessageSquareText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
    { value: 'media', label: 'Медиа', icon: <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
    { value: 'hardware', label: 'Стенды', icon: tabIcon(<Cpu size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, dotLevel) },
    { value: 'transcription', label: 'Встречи', icon: tabIcon(<AudioLines size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />, transDotLevel) },
  ];
  // Узкая лента: у неактивных подписей нет — InlineSegmented красит из них aria-label и title
  // (подсказка при наведении); активная остаётся с подписью.
  const items = narrow
    ? options.map(o => (o.value === value ? o : { ...o, label: '', title: o.label }))
    : options;
  return (
    <InlineSegmented<Tab>
      value={value}
      isMobile={isMobile}
      size="sm"
      options={items}
      onChange={onChange}
    />
  );
}

export function LocalModelPanel({ ctx }: { ctx: WorkspacePanelDefCtx }) {
  // Роль и опрос configured — в useLocalModelWatcher (смонтирован на странице всегда)
  const isAdmin = useLocalModelAdmin();

  // Тяжёлый 2-с опрос: только когда панель видна (открыта + admin +
  // configured); не видна — опрос стоит, таймера нет. «Настроен ли» — одно выражение
  // со значком рельса (isLocalModelConfiguredAny: основной/медиа ИЛИ последний
  // снимок второй дорожки): с другой формулой был бы значок без панели (пустая
  // карточка). Ререндер при смене — подписка сторожа на странице.
  const visible = isLocalModelPanelVisible(isLocalModelConfiguredAny());

  // Не-админ: вкладка всегда «Медиа» при любом сохранённом значении — переключателя
  // нет, админские эндпоинты (/stats, /hardware, /transcription) не опрашиваются.
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

  // Окно графика «Температуры» вкладки «Стенды»: своё, не общее с LLM; дефолт 15m.
  const [hwWindow, setHwWindow] = useState<HardwareWindow>(DEFAULT_HARDWARE_WINDOW);

  // Движок: список с сервера и выбор человека — стор Ф1 (useLocalModelEngines,
  // localStorage). Активен, только когда видна вкладка «LLM» (переключатель и стенд
  // живут там). Тот же engine читает тяжёлый опрос (URL stats?engine=) и TextTab:
  // опрос, стенд и переключатель видят один и тот же выбор.
  const { engines, engine, setEngine } = useLocalModelEngines(visible && tab === 'text');

  // Тяжёлый 2-с опрос активной вкладки — только АКТИВНОЙ вкладки (неактивная на паузе):
  // «LLM» — stats, «Медиа» — media, «Стенды» — отчёт узла; переключение — немедленный снимок.
  // Сводка фермы — исключение: опрашивается, пока смонтирована панель (ниже), — она кормит
  // точку тревоги на вкладке, а она обязана быть видна с любой вкладки.
  const { data: textData, loading: textLoading, error: textError, refetch: textRefetch } = useLocalModelStats(visible && tab === 'text', window, engine);
  const { data: mediaData, loading: mediaLoading, refetch: mediaRefetch } = useLocalModelMedia(visible && tab === 'media');
  // Опрос снимка конвейера (5 с) — пока панель смонтирована и пользователь админ
  // (эндпоинт админский): точка тревоги на «Встречах» обязана быть видна с любой
  // вкладки, опрос прекращается при закрытии ПАНЕЛИ, а не при уходе с вкладки
  // (дефект №1 приёмки Ф2/Ф6; решение по продукту: точка важнее экономии одного
  // запроса в 5 с). Не-админ не опрашивает: вкладка ему не предлагается вовсе.
  const { data: transcriptionData } = useTranscription(visible && isAdmin);
  // Выбор узла на вкладке «Стенды»: ключ, который коснулся человек (null — не выбирал);
  // показываемый узел — farmPickNode (выбор, если он ещё в ферме, иначе худший уровень, при
  // равенстве первый). Быстрые касания A → B → A: в состоянии последний выбор.
  const [chosenNode, setChosenNode] = useState<string | null>(null);
  const hwActive = visible && tab === 'hardware';
  // Опрос сводки фермы (2 с) — пока панель смонтирована и пользователь админ (эндпоинт
  // админский): точка тревоги на «Стендах» обязана быть видна с любой вкладки, опрос
  // прекращается при закрытии ПАНЕЛИ, а не при уходе с вкладки — тот же порядок, что у
  // «Встреч» (находка ревью Р-Ф2д; по продукту: точка важнее экономии одного запроса
  // в 2 с). Не-админ не опрашивает: вкладка ему не предлагается вовсе.
  const { data: farm, loading: farmLoading } = useLocalModelFarm(visible && isAdmin);
  const nodeKey = farmPickNode(farm, chosenNode);
  const node = farm?.farmNodes.find(n => n.key === nodeKey) ?? null;
  const { data: hwData, loading: hwLoading } = useLocalModelHardware(hwActive, hwWindow, nodeKey ?? undefined);
  // Движок выбранного узла — первый ключ node.engines; медиа — только у узла с media.
  // Поздний ответ уже не выбранного движка отбрасывает createStatsPoller (engineRef).
  const nodeEngine = node?.engines[0];
  const { data: nodeStats } = useLocalModelStats(hwActive && nodeEngine != null, window, nodeEngine ?? 'main');
  const { data: nodeMedia } = useLocalModelMedia(hwActive && node?.media === true);

  // Точка на вкладке «Стенды» (6 px, C.warning/C.danger): по уровню фермы, видна с любой
  // вкладки; при warn/danger. Молчащий узел уровня не имеет (level 'ok'), точки не даёт.
  const dotLevel: 'warn' | 'danger' | null =
    farm && (farm.level === 'warn' || farm.level === 'danger') ? farm.level : null;
  // Точка «Встреч» — по level последнего снимка (считает бэкенд): видна с любой вкладки,
  // пока смонтирована панель (тот же порядок, что у «Стендов»).
  const transDotLevel = transcriptionDot(transcriptionData);

  // Ширина панели для «узкой ленты» (TABS_NARROW_PX): до первого замера — широкая
  // раскладка (тот же договор, что в useContainerWidth).
  const [panelRef, panelWidth] = useContainerWidth<HTMLDivElement>();
  const tabsNarrow = panelWidth !== null && panelWidth < TABS_NARROW_PX;

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
          ? <HardwareTab farm={farm} farmLoading={farmLoading} selected={nodeKey} onSelect={setChosenNode} report={hwData} loading={hwLoading} stats={nodeStats} media={nodeMedia} isMobile={ctx.isMobile} window={hwWindow} onWindowChange={setHwWindow} />
          : tab === 'transcription'
            // Тело вкладки «Встречи»: верх (Ф3) — конвейер, «Сейчас», очередь; нижняя
            // часть (сбои, встречи, сверка, зависимости) — шаг Ф4.
            ? <TranscriptionTab data={transcriptionData} />
            : <MediaTab data={mediaData} loading={mediaLoading} isMobile={ctx.isMobile} onOpenChat={onOpenChat} onRefetch={mediaRefetch} onOpenHardware={() => changeTab('hardware')} />}
    </div>
  );

  // Без PanelShell-шапки (мобила) переключатель — фиксированной строкой в теле,
  // над прокручиваемым блоком: не скроллится вместе с ним.
  const mobileSwitcher = !inHeader && isAdmin && (
    <div style={{ flexShrink: 0, padding: `${SP.md}px ${SP.md}px 0` }}>
      <TabSwitch value={tab} onChange={changeTab} isMobile={ctx.isMobile} dotLevel={dotLevel} transDotLevel={transDotLevel} narrow={tabsNarrow} />
    </div>
  );

  // Доступное имя панели берётся из того же реестра, что видимый заголовок шапки
  // (тот же резолвер, что у PanelZone.tsx:128): переименование панели не требует
  // правки строки здесь и не разъезжается с рельсой.
  return (
    <>
      {inHeader && isAdmin && (
        <PanelHeaderSlot side="left">
          <TabSwitch value={tab} onChange={changeTab} isMobile={ctx.isMobile} dotLevel={dotLevel} transDotLevel={transDotLevel} narrow={tabsNarrow} />
        </PanelHeaderSlot>
      )}
      <div ref={panelRef} role="complementary" aria-label={panelMetaResolved(LOCAL_MODEL_PANEL).title}
        style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column', fontFamily: FONT.sans }}>
        {mobileSwitcher}
        {body}
      </div>
    </>
  );
}
