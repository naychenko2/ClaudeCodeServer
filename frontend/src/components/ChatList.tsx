import { useMemo, useState } from 'react';
import { Archive, FilterX, MessageCircle, Plus } from 'lucide-react';
import type { Session } from '../types';
import { api } from '../lib/api';
import { useOnline } from '../hooks/useOnline';
import { C, ISLAND, MODAL_W, SP } from '../lib/design';
import { ConfirmDialog, Modal, ModalActions, Button, PanelShell, useHasPanelHeader } from './ui';
import { groupChats, sortChatsFlat } from '../lib/chatGroups';
import { usePersonas, usePersonasVersion } from '../lib/personas';
import { ChatFilterResetActions } from './FilterBar';
import { ChatListToolbar } from './ChatListToolbar';
import { EmptyState } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { useChatFilters, useSanitizePersonaFilter, matchChatFilter, isDefaultFilters, defaultChatFiltersKeepingView, buildHiddenReason, chatCountWord, type ChatGroupBy } from '../lib/chatFilters';
import { buildChatTreeRows, splitChatTreeByRoots, useTreeCollapse } from '../lib/chatTree';
import { useBgWorkPresence } from '../lib/agentsPresence';
import { useLastMechanicVersion } from '../lib/lastMechanic';
import { ChatCard } from './ChatCard';
import { useHoverWarm } from '../hooks/useSession';
import { ChatTreeBranch, nestTreeRows } from './ChatTreeRow';
import { ChatGroupingDnd } from './ChatGroupingDnd';
import { ListDateDivider } from './ListDateDivider';
import { ArchiveNotice } from './ArchiveNotice';

interface Props {
  chats: Session[];
  activeId: string | null;
  onSelect: (chat: Session) => void;
  onNew: () => void;
  creating?: boolean;
  // Чат отредактирован/закреплён — обновить в списке
  onEdited: (updated: Session) => void;
  // Чат удалён — убрать из списка
  onDeleted: (id: string) => void;
  isMobile?: boolean;
  // Чат с активным workflow — плашка «WF» на его карточке
  workflowRunningFor?: string;
  // bare=true — рендерит только контент (toolbar + список), БЕЗ обёртки PanelShell.
  // Используется когда ChatList встроен в другую панель, которая сама несёт
  // PanelShell (напр. LeftPanelStack). Иначе двойной PanelShell = два заголовка.
  bare?: boolean;
}

// Режимы группировки глобального списка: реестра тегов у него нет — только Дни/Без
const GROUP_BY_OPTIONS: ChatGroupBy[] = ['days', 'none'];

export function ChatList({ chats, activeId, onSelect, onNew, creating, onEdited, onDeleted, isMobile = false, workflowRunningFor, bare = false }: Props) {
  const online = useOnline();
  // История чата под курсором едет до клика — открытие обходится без спиннера
  const warmHover = useHoverWarm();
  // Подписка на стор персон — перерисоваться, когда список подгрузится (аватары чатов персон)
  usePersonasVersion();
  // Подписка на стор механик — перерисовать список при запуске новой механики
  useLastMechanicVersion();
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null);
  // Подтверждение полной очистки архива (удаление всех архивных чатов насовсем)
  const [clearArchiveAsk, setClearArchiveAsk] = useState(false);
  // Карточка под курсором — на ней показываем действия (на тач-устройствах hover нет, там действия видны всегда)
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  // Раскрытая свайпом карточка (мобильная раскладка): максимум одна; открытие
  // другой закрывает предыдущую. null — все закрыты
  const [openSwipeId, setOpenSwipeId] = useState<string | null>(null);

  // === Фильтры и оси списка чатов ===
  // Фильтры + оси вида (groupBy/sortOrder/hierarchy) — единый state, персистится
  // в localStorage отдельно от проектных списков (scope 'global')
  const { filters, patch } = useChatFilters('global');
  const { sortOrder, hierarchy } = filters;
  // groupBy из хранилища может быть недоступен глобальному списку ('tags',
  // напр., мигрирован из legacy cc_chat_view:global) — клампим к опциям, иначе
  // ни одна ветка рендера не сработает: пустой лист без empty-state. Хранилище
  // не перезаписываем — первый же явный выбор пользователя всё починит сам.
  const groupBy: ChatGroupBy = GROUP_BY_OPTIONS.includes(filters.groupBy) ? filters.groupBy : 'days';
  // Память свёрнутых веток дерева
  const { collapsedIds, toggleCollapse } = useTreeCollapse('global');
  // Чаты с живой фоновой работой (агенты или команда в фоне) — считаются работающими
  // в счётчике свёрнутой ветки, как и в переливе самой карточки
  const bgWorkIds = useBgWorkPresence();

  // Список лежит в карточке с шапкой — контролы уедут туда сами (PanelHeaderSlot),
  // и собственная полоса тулбара в теле не нужна
  const inHeader = useHasPanelHeader();

  const personas = usePersonas();

  // Персоны в списке (для селектора фильтра)
  const personaIdsInList = [...new Set(chats.filter(c => c.personaId).map(c => c.personaId!))];
  useSanitizePersonaFilter(filters, patch, personaIdsInList, chats.length > 0);

  // Применение фильтров (единый предикат — общий с проектным списком чатов)
  const isVisible = matchChatFilter(filters);
  const filteredChats = chats.filter(isVisible);
  // Архив области: счёт для подсказки кнопки и для плашки над списком. Обе стороны
  // развилки нужны отдельно — бейдж панели показывает размер ТЕКУЩЕГО вида
  const archivedChats = chats.filter(c => !!c.archivedAt);
  const inArchive = filters.archived;
  const viewTotal = inArchive ? archivedChats.length : chats.length - archivedChats.length;
  // Фильтр применяется ко всем узлам дерева (не только к корням) — множество видимых
  // чатов совпадает с плоским списком. Сборка леса мемоизирована — hover по карточке
  // (hoveredId) не пересобирает дерево; isVisible пересоздаётся каждый рендер, его
  // исходные данные покрывает зависимость filters
  const tree = useMemo(
    () => hierarchy
      ? buildChatTreeRows(chats, { isVisible, collapsedIds, activeId, sortOrder, bgWorkIds })
      : null,
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [chats, hierarchy, sortOrder, collapsedIds, activeId, filters, bgWorkIds],
  );
  // Скрыто фильтрами — одинаково для плоского и дерева: множество видимых чатов одно.
  // Считаем в пределах ТЕКУЩЕГО вида: архив — не «скрытые фильтром» чаты, и без этого
  // бейдж фильтров вечно горел бы числом архивных
  const hiddenCount = viewTotal - filteredChats.length;

  // Возврат чата из архива: выходим из архивного вида и открываем этот чат. Человек
  // достаёт чат из архива, чтобы продолжить в нём работу, — оставлять его после этого
  // в списке архива (где чат к тому же тут же исчезает из вида) было бы тупиком
  const leaveArchiveAndOpen = (chat: Session) => {
    if (filters.archived) patch({ archived: false });
    setOpenSwipeId(null);
    onSelect(chat);
  };

  const togglePin = async (chat: Session) => {
    try {
      const updated = await api.chats.update(chat.id, { pinned: !chat.isPinned });
      onEdited(updated);
    } catch { /* сеть упала — не блокируем */ }
  };

  // Переименование из карточки списка: ответ бэкенда отдаём владельцу — у активного
  // чата от него же обновляется шапка панели
  const renameChat = async (chat: Session, name: string) => {
    const updated = await api.chats.update(chat.id, { name });
    onEdited(updated);
  };

  const handleDelete = async () => {
    if (!deleteTarget) return;
    try {
      await api.chats.delete(deleteTarget.id);
    } catch {
      setDeleteTarget(null);
      return;
    }
    onDeleted(deleteTarget.id);
    setDeleteTarget(null);
  };

  // Полная очистка архива: удаляем архивные чаты по одному тем же эндпоинтом, что и
  // «Удалить» у карточки. Отдельного массового API нет намеренно — удаление чата уносит
  // и транскрипт claude CLI, и дублировать этот каскад пачкой ради одной кнопки незачем.
  // Упавшие удаления просто останутся в архиве: список обновит только реально удалённые.
  const clearArchive = async () => {
    const results = await Promise.allSettled(archivedChats.map(c => api.chats.delete(c.id)));
    results.forEach((r, i) => { if (r.status === 'fulfilled') onDeleted(archivedChats[i].id); });
    setClearArchiveAsk(false);
  };


  // === Композиция списка по осям (groupBy × sortOrder × hierarchy) ===
  // groupBy уже клампнут к GROUP_BY_OPTIONS выше — ветка 'tags' сюда не доходит.
  // Сегменты дерева — как в SessionList: корень секционируется по maxActivity поддерева.
  const treeSegments = useMemo(() => {
    if (!tree) return null;
    return splitChatTreeByRoots(tree.rows).map(seg => ({
      seg,
      rootChat: { ...seg[0].chat, updatedAt: new Date(seg[0].maxActivity).toISOString() } as Session,
    }));
  }, [tree]);
  const segByRootId = useMemo(
    () => treeSegments ? new Map(treeSegments.map(x => [x.seg[0].chat.id, x.seg])) : null,
    [treeSegments],
  );

  const dayGroups = groupBy === 'days'
    ? groupChats(treeSegments ? treeSegments.map(x => x.rootChat) : filteredChats, sortOrder)
    : [];
  const flatList = groupBy === 'none' && !treeSegments
    ? sortChatsFlat(filteredChats, sortOrder)
    : null;

  // leadingInset — место под контрол ветки в дереве (в плоском списке 0)
  const renderCard = (chat: Session, leadingInset = 0) => (
    <ChatCard
      key={chat.id}
      session={chat}
      leadingInset={leadingInset}
      isActive={chat.id === activeId}
      isMobile={isMobile}
      fallbackName="Новый чат"
      online={online}
      hovered={hoveredId === chat.id}
      workflowRunning={workflowRunningFor === chat.id}
      onSelect={() => { setOpenSwipeId(null); onSelect(chat); }}
      onHover={h => { setHoveredId(h ? chat.id : null); warmHover(h && online ? chat : null); }}
      onDelete={() => setDeleteTarget(chat)}
      onTogglePin={() => togglePin(chat)}
      onRename={online ? name => renameChat(chat, name) : undefined}
      onEdited={onEdited}
      onUnarchived={leaveArchiveAndOpen}
      swipeOpen={openSwipeId === chat.id}
      onSwipeToggle={open => setOpenSwipeId(open ? chat.id : null)}
    />
  );

  // === Общие части обоих режимов (bare и PanelShell) ===
  // Собраны по одному разу: иначе тулбар, список и модалки пришлось бы держать
  // продублированными в двух ветках return.
  const toolbar = (
    <ChatListToolbar
      onNew={onNew}
      creating={creating}
      sessions={chats}
      filters={{ ...filters, groupBy }}
      patch={patch}
      allPersonas={personas}
      hiddenCount={hiddenCount}
      isMobile={isMobile}
      groupByOptions={GROUP_BY_OPTIONS}
      archivedCount={archivedChats.length}
    />
  );

  // Плашка архивного вида — над списком, в обоих режимах (bare и PanelShell)
  const archiveNotice = inArchive ? (
    <ArchiveNotice
      count={archivedChats.length}
      isMobile={isMobile}
      onExit={() => patch({ archived: false })}
      onClear={() => setClearArchiveAsk(true)}
    />
  ) : null;


  // Содержимое списка. Пустой список — приглашение создать первый чат; если чаты
  // есть, но фильтры всё скрыли — подсказка со сбросом.
  const listContent = (
    <>
      {inArchive && archivedChats.length === 0 && (
        // Центрируем по высоте доступной зоны (как «Проект без git» в рельсе
        // изменений): без обёртки EmptyState прилипал к верху панели и пустой
        // контент выглядел недозагруженным, а не пустым. minHeight держит
        // центрирование и когда панель ниже контента
        <div style={{ minHeight: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center', boxSizing: 'border-box' }}>
          <EmptyState
            compact={!isMobile}
            icon={<Archive size={isMobile ? ICON_SIZE.xl : ICON_SIZE.lg} strokeWidth={2} />}
            title="В архиве пусто"
            subtitle="Убирайте сюда чаты, которые не нужны в списке, но которые жалко удалить."
            action={
              <Button variant="ghost" size="md" style={{ whiteSpace: 'nowrap' }} onClick={() => patch({ archived: false })}>
                Вернуться к списку
              </Button>
            }
          />
        </div>
      )}
      {/* Все чаты области лежат в архиве: chats.length > 0, так что empty «здесь будут
          чаты» не срабатывает, а viewTotal === 0 гасит и «Ничего не нашлось» — без этого
          состояния панель оставалась бы белой дырой. В отличие от «пусто», здесь человек
          должен вспомнить про архив, а не про создание */}
      {!inArchive && chats.length > 0 && viewTotal === 0 && (
        <div style={{ minHeight: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center', boxSizing: 'border-box' }}>
          <EmptyState
            compact={!isMobile}
            icon={<Archive size={isMobile ? ICON_SIZE.xl : ICON_SIZE.lg} strokeWidth={2} />}
            title="Все чаты в архиве"
            subtitle="Список пуст: живых чатов нет, архив не считается."
            action={
              <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
                <Button variant="ghost" size="md" style={{ whiteSpace: 'nowrap' }} onClick={() => patch({ archived: true })}>
                  Открыть архив
                </Button>
                <Button
                  variant="primary" size="md" loading={creating}
                  onClick={onNew}
                  leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={2} />}
                >
                  Создать первый чат
                </Button>
              </div>
            }
          />
        </div>
      )}
      {!inArchive && chats.length === 0 && (
        // Та же центрирующая обёртка, что у архивного empty: без неё «Создать первый
        // чат» прилипал к верху панели, когда список ещё пуст
        <div style={{ minHeight: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center', boxSizing: 'border-box' }}>
          <EmptyState
            compact={!isMobile}
            icon={<MessageCircle size={isMobile ? ICON_SIZE.xl : ICON_SIZE.lg} strokeWidth={2} />}
            title="Здесь будут ваши чаты"
            subtitle="Создавайте чаты с AI и персонами для личных тем, идей и задач."
            action={
              <Button
                variant="primary" size="md" loading={creating}
                onClick={onNew}
                leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={2} />}
              >
                Создать первый чат
              </Button>
            }
          />
        </div>
      )}
      {(tree ? tree.rows.length === 0 : filteredChats.length === 0) && viewTotal > 0 && (
        <div style={{ minHeight: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center', boxSizing: 'border-box' }}>
          <EmptyState
            compact
            icon={<FilterX size={20} strokeWidth={2} />}
            title="Ничего не нашлось"
            subtitle={buildHiddenReason(viewTotal, filters.search)}
            action={
              <ChatFilterResetActions
                search={filters.search}
                hasNonSearchFilters={!isDefaultFilters({ ...filters, search: '' })}
                onResetSearch={() => patch({ search: '' })}
                onResetAll={() => patch(defaultChatFiltersKeepingView(filters))}
              />
            }
          />
        </div>
      )}
      {treeSegments ? (
        <ChatGroupingDnd chats={chats} isMobile={isMobile} onEdited={onEdited}>
          {groupBy === 'none' ? (
            treeSegments.map(({ seg }) => nestTreeRows(seg).map(node => (
              <ChatTreeBranch key={node.row.chat.id} node={node} isMobile={isMobile} onToggleCollapse={toggleCollapse} renderCard={renderCard} />
            )))
          ) : (
            dayGroups.map(g => (
              <div key={g.title} style={{ marginBottom: 6, display: 'flow-root' }}>
                <ListDateDivider title={g.title} plain />
                {g.items.map(root => nestTreeRows(segByRootId!.get(root.id) ?? []).map(node => (
                  <ChatTreeBranch key={node.row.chat.id} node={node} isMobile={isMobile} onToggleCollapse={toggleCollapse} renderCard={renderCard} />
                )))}
              </div>
            ))
          )}
        </ChatGroupingDnd>
      ) : flatList ? (
        flatList.map(c => renderCard(c))
      ) : dayGroups.map(g => (
        // display:flow-root — иначе marginBottom последней карточки схлопывался бы
        // с отступом группы, и высота списка отличалась бы от режима «Иерархия» на
        // 5px за каждую группу: там последняя строка — flex-контейнер (ChatTreeRow),
        // её margin наружу не выходит. Панель растёт по контенту, и переключение
        // иерархии дёргало её на эту разницу
        <div key={g.title} style={{ marginBottom: 6, display: 'flow-root' }}>
          <ListDateDivider title={g.title} plain />
          {g.items.map(c => renderCard(c))}
        </div>
      ))}
    </>
  );

  // Модалка удаления — общая для обоих режимов. Прочие свойства чата правятся не
  // отсюда: имя — пунктом «Переименовать» в карточке, модель и усилие — в композере
  // (срок хранения и уведомления есть в меню карточки — пункты рисует сам ChatCard)
  const dialogs = (
    <>
      {clearArchiveAsk && (
        <ConfirmDialog
          title="Очистить архив?"
          confirmLabel="Удалить всё"
          confirmVariant="danger"
          subtitle={`${archivedChats.length} ${chatCountWord(archivedChats.length)} из архива будут удалены без возможности восстановления.`}
          onConfirm={clearArchive}
          onCancel={() => setClearArchiveAsk(false)}
        />
      )}
      {deleteTarget && (
        <Modal
          title="Удалить чат?"
          width={MODAL_W.confirm}
          onClose={() => setDeleteTarget(null)}
          subtitle={
            <>
              Чат «<strong style={{ color: C.textPrimary, fontWeight: 600 }}>{deleteTarget.name ?? 'Новый чат'}</strong>» будет удалён без возможности восстановления.
            </>
          }
          footer={
            <ModalActions
              confirmLabel="Удалить"
              confirmVariant="danger"
              onConfirm={handleDelete}
              onCancel={() => setDeleteTarget(null)}
            />
          }
        />
      )}
    </>
  );

  // Скроллящаяся зона списка. Отступ тот же, что у списка чатов проекта
  // (SessionList): один и тот же ChatCard в двух местах должен стоять одинаково,
  // а раньше здесь карточки шли вплотную к краям панели. Padding заодно решает
  // задачу прежнего отрицательного margin — тени и ховер больше не срезаются
  // краем скролл-контейнера.
  const scrollArea = (
    // Сверху отступ ужимается только под разделитель группы («Сегодня»): свой
    // верхний padding у него есть, и вместе с общим набегало 18px пустоты под
    // шапкой. Без группировки список начинается сразу карточкой — ей нужен
    // обычный отступ, иначе она липнет к заголовку.
    <div
      // Скролл списка закрывает раскрытую свайпом карточку: жест и прокрутка —
      // разные намерения, держать раскрытие во время прокрутки мешает обзору
      onScroll={() => { if (openSwipeId !== null) setOpenSwipeId(null); }}
      // Фон задан явно, хотя тело панели и так белое: строки чатов рамок не имеют
      // и в покое прозрачны, а липкая подпись группы закрашена тем же bgWhite —
      // оба приёма ломаются, если список поставить на чужой фон (мобильная
      // раскладка воркспейса своего фона колонке не задаёт)
      style={{ flex: 1, minHeight: 0, overflowY: 'auto', background: C.bgWhite, padding: `${groupBy === 'none' ? 8 : 2}px 8px 8px` }}>
      {listContent}
    </div>
  );

  // bare=true — только контент (тулбар + список), без своей PanelShell:
  // оболочку несёт вызывающая панель (напр. LeftPanelStack), иначе вышел бы
  // остров в острове с двумя заголовками. Оформление тулбара повторяет
  // PanelShell.toolbar, чтобы оба режима выглядели одинаково.
  if (bare) {
    return (
      <>
        {/* Полоса тулбара нужна, только когда контролы остались в теле. В карточке
            с шапкой они уезжают туда порталом, и обёртка стала бы пустой серой
            полосой под заголовком. */}
        {inHeader ? toolbar : (
          <div style={{
            flexShrink: 0,
            padding: '8px 10px 9px',
            borderBottom: `1px solid ${C.border}`,
            background: ISLAND.bg,
            display: 'flex', flexDirection: 'column', gap: 8,
          }}>
            {toolbar}
          </div>
        )}
        <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: C.bgWhite }}>
          {archiveNotice}
          {scrollArea}
        </div>
        {dialogs}
      </>
    );
  }

  return (
    <>
      <PanelShell
        icon={
          <MessageCircle
            size={ICON_SIZE.sm}
            strokeWidth={ICON_STROKE}
            color={C.textSecondary}
            style={{ flexShrink: 0 }}
          />
        }
        title={inArchive ? 'Чаты · архив' : 'Чаты'}
        badge={viewTotal > 0 ? String(viewTotal) : null}
        // fill=false: панель занимает по контенту, не растягивается на всю
        // высоту сайдбара — если чатов мало, низ остаётся свободным.
        fill={false}
      >
        {/* Тулбар — обычным ребёнком, а не через toolbar: контролы внутри шапки
            уедут порталом сами, и полоса под заголовком не останется пустой.
            Мобильная ступень порталу не подлежит и рисуется здесь же, в теле. */}
        {toolbar}
        {archiveNotice}
        {scrollArea}
      </PanelShell>
      {dialogs}
    </>
  );
}
