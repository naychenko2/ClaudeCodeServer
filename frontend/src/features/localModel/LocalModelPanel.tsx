// Панель «Локальные модели» — остров правого рельса (только админ). Каркас:
// шапка, переключатель «Текст · Медиа» (не прокручивается с телом) и тело
// вкладки. Блоки вкладок — следующие шаги, сейчас заглушки с заголовком.
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
import { Cpu, Clapperboard, MessageSquareText, X } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP, Z, ISLAND, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import type { WorkspacePanelDefCtx } from '../../lib/subsystems/registryCore';
import { FLAGS, useFeature } from '../../lib/featureFlags';
import { InlineSegmented } from '../../components/ui/InlineSegmented';
import { IconButton } from '../../components/ui';
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

// Вкладка запоминается на устройство (localStorage): «Текст · Медиа» — выбор
// человека, не состояние сессии
const TAB_KEY = 'cc_local_model_tab';
type Tab = 'text' | 'media';
function loadTab(): Tab {
  try { return localStorage.getItem(TAB_KEY) === 'media' ? 'media' : 'text'; } catch { return 'text'; }
}

// Переключатель «Текст · Медиа»: первый элемент тела, над статусом,
// не прокручивается вместе с телом. Дорожка TB.pillTrack, активный C.accentLight/C.accent
function TabSwitch({ value, onChange, isMobile }: { value: Tab; onChange: (t: Tab) => void; isMobile: boolean }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'center' }}>
      <InlineSegmented<Tab>
        value={value}
        isMobile={isMobile}
        options={[
          { value: 'text', label: 'Текст', icon: <MessageSquareText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
          { value: 'media', label: 'Медиа', icon: <Clapperboard size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> },
        ]}
        onChange={onChange}
      />
    </div>
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

  // Тяжёлый 2-с опрос — только АКТИВНОЙ вкладки (неактивная на паузе):
  // «Текст» — stats, «Медиа» — media; переключение — немедленный снимок.
  const { data: textData, loading: textLoading, refetch: textRefetch } = useLocalModelStats(visible && tab === 'text');
  const { data: mediaData, loading: mediaLoading, refetch: mediaRefetch } = useLocalModelMedia(visible && tab === 'media');

  // Клик по строке «Кто на модели»: открыть чат; на мобиле после перехода лист
  // закрывается (макет: «клик открывает чат (на мобиле лист закрывается)»)
  const onOpenChat = useCallback((sessionId: string) => {
    void openChatById(sessionId).then(ok => { if (ok && ctx.isMobile) ctx.onClose(); });
  }, [ctx]);

  // Не видно — null: keyAvailable прячет кнопку в рельсе
  if (!visible) return null;

  const close = (
    <IconButton size={ctx.isMobile ? 'lg' : 'xs'} title="Закрыть панель" onClick={ctx.onClose}>
      <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    </IconButton>
  );

  // Шапка: остров — 40 px на C.bgInset; лист — 44 px прозрачная (фон листа C.bgPanel)
  const header = (
    <div style={{
      height: ctx.isMobile ? 44 : ISLAND.headerH,
      flex: `0 0 ${ctx.isMobile ? 44 : ISLAND.headerH}px`,
      display: 'flex', alignItems: 'center', gap: SP.sm,
      padding: `0 ${SP.md}px`, minWidth: 0,
      background: ctx.isMobile ? 'transparent' : ISLAND.headerBg,
      borderBottom: `1px solid ${C.borderLight}`,
    }}>
      <span style={{ display: 'inline-flex', color: C.accent, flexShrink: 0 }}>
        <Cpu size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
      </span>
      <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}>
        Локальные модели
      </span>
      <span style={{ flex: 1 }} />
      {close}
    </div>
  );

  const switcher = (
    <div style={{ padding: `${SP.md}px ${SP.md}px 0`, flexShrink: 0 }}>
      <div style={{ width: '100%' }}>
        <TabSwitch value={tab} onChange={changeTab} isMobile={ctx.isMobile} />
      </div>
    </div>
  );

  const body = (
    <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {tab === 'text'
        ? <TextTab data={textData} loading={textLoading} isMobile={ctx.isMobile} onOpenChat={onOpenChat} onRefetch={textRefetch} />
        : <MediaTab data={mediaData} loading={mediaLoading} isMobile={ctx.isMobile} onOpenChat={onOpenChat} onRefetch={mediaRefetch} />}
    </div>
  );

  // Мобила — нижний лист: как другие панели рельса (R.sheet, граббер, 88 % высоты)
  if (ctx.isMobile) {
    return (
      <div role="dialog" aria-label="Локальные модели" style={{
        position: 'fixed', left: 0, right: 0, bottom: 0, zIndex: Z.modal,
        height: '88%', display: 'flex', flexDirection: 'column', overflow: 'hidden',
        background: C.bgPanel, borderRadius: `${R.sheet}px ${R.sheet}px 0 0`,
        boxShadow: SHADOW.sheet, fontFamily: FONT.sans,
      }}>
        <div style={{ flex: '0 0 auto', alignSelf: 'center', padding: `${SP.sm}px 0 ${SP.xxs}px` }}>
          <span style={{ display: 'block', width: 40, height: 4, borderRadius: R.max, background: C.track }} />
        </div>
        {header}
        {switcher}
        {body}
      </div>
    );
  }

  // Десктоп/планшет — остров: C.bgPanel, рамка C.borderLight, R.xxl, SHADOW.island
  return (
    <div role="complementary" aria-label="Локальные модели" style={{
      position: 'relative', display: 'flex', flexDirection: 'column', overflow: 'hidden',
      background: ISLAND.bg, border: `1px solid ${ISLAND.border}`,
      borderRadius: ISLAND.radius, boxShadow: ISLAND.shadow, fontFamily: FONT.sans,
    }}>
      {header}
      {switcher}
      {body}
    </div>
  );
}
