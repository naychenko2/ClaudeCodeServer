import { useEffect, useState } from 'react';
import {
  ArrowDown, ArrowUp, ChevronLeft, Folder, FolderPlus, MessageSquareText,
  MoreHorizontal, Pencil, Plus, Settings2, Trash2, Ungroup,
} from 'lucide-react';
import { C, FONT, FS, MODAL_W, SP } from '../lib/design';
import {
  Button, ConfirmDialog, FieldLabel, IconButton, Menu, MenuItem, MenuSep,
  Modal, ModalActions, TextField, useIsMobileModal,
} from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import {
  QUICK_PHRASE_MAX_COUNT, QUICK_PHRASE_MAX_GROUP_LENGTH, QUICK_PHRASE_MAX_LENGTH,
  ensureQuickPhrases, flattenSections, groupQuickPhrases, movePhrase, quickPhrasesFailed,
  quickPhrasesLoaded, saveQuickPhrases, toSections, useQuickPhrases, type QuickPhraseSection,
} from '../lib/quickPhrases';

// Быстрые фразы композера: попап со списком готовых сообщений (клик — ход уходит
// в чат немедленно) и форма правки набора. Набор личный и общий для всех чатов
// (см. lib/quickPhrases.ts). Сама кнопка живёт в Composer рядом с микрофоном —
// там она делит стиль и защиту от long-press с соседями по ряду.

// Геометрия попапа: карточка узкая (пункты — короткие фразы), список скроллится
// внутри неё, а шапка с подписью и настройкой набора остаётся на месте
const MENU_MIN_W = 240;
const MENU_MAX_W = 420;
const MENU_MAX_H = 360;
const MENU_LIST_MAX_H = 260;
// Высота карточки формы на десктопе: она ЗАДАНА, а не считается по содержимому —
// набор правят подолгу, и окно, прыгающее в размере на каждую добавленную строку,
// заставляет заново искать глазами кнопки. Список внутри занимает всё, что осталось
// (flex), и скроллится сам, так что счётчик сверху и «Новая группа» снизу на месте
const FORM_CARD_H = 'min(760px, calc(100vh - 32px))';

export function QuickPhrasesMenu({ anchor, onClose, onPick, onEdit }: {
  // rect кнопки-триггера: попап открывается у нижней кромки экрана, и Menu сам
  // развернёт карточку вверх
  anchor: DOMRect;
  onClose: () => void;
  // Выбор фразы: отправляем как есть, поле ввода не трогаем
  onPick: (text: string) => void;
  onEdit: () => void;
}) {
  const phrases = useQuickPhrases();
  const loaded = quickPhrasesLoaded();
  const failed = quickPhrasesFailed();
  // Раскрытая группа (второй уровень попапа); null — корень набора
  const [openName, setOpenName] = useState<string | null>(null);

  // Список тянем в момент открытия: раньше он не нужен, а на каждый чат лишний запрос
  useEffect(() => { void ensureQuickPhrases(); }, []);

  // Закрытие по Esc — поведение вызывающей стороны (см. комментарий в ui/Menu)
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); onClose(); } };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  const { root, groups } = groupQuickPhrases(phrases);
  // Раскрытая группа показывается ВМЕСТО корневого списка, в той же карточке:
  // вложенное подменю сбоку на узком экране просто некуда открыть.
  // Имя, а не индекс: набор может обновиться (сохранение из формы) под открытым попапом
  const openGroup = openName ? groups.find(g => g.name === openName) : undefined;
  // Группа исчезла (переименовали/вычистили), пока попап открыт — молча возвращаемся в корень
  const inGroup = openGroup !== undefined;

  return (
    <Menu anchor={anchor} onClose={onClose} minWidth={MENU_MIN_W} maxWidth={MENU_MAX_W} maxHeight={MENU_MAX_H}>
      {/* Шапка попапа: подпись слева, настройка набора — кнопкой справа. Пунктом
          списка настройка стояла в один ряд с самими фразами, хотя фразу она не
          отправляет: соседство обещало не то действие */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: SP.sm,
        padding: `${SP.xxs}px ${SP.xs}px ${SP.xxs}px ${SP.sm}px`,
      }}>
        <FieldLabel>Быстрые фразы</FieldLabel>
        <span style={{ flex: 1 }} />
        <IconButton
          size="xs"
          title="Настроить фразы"
          onClick={() => { onClose(); onEdit(); }}
        >
          <Settings2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
      <MenuSep />
      {/* Список скроллится внутри карточки, шапка с настройкой остаётся на месте */}
      <div style={{ maxHeight: MENU_LIST_MAX_H, overflowY: 'auto' }}>
        {inGroup ? (
          <>
            {/* Шапка второго уровня: она же кнопка возврата — отдельная стрелка «назад»
                рядом с названием была бы второй целью для одного и того же действия */}
            <MenuItem
              icon={<ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label={openGroup.name}
              onClick={() => setOpenName(null)}
            />
            <MenuSep />
            {openGroup.phrases.map((p, i) => (
              <MenuItem
                key={`${p.text}-${i}`}
                label={p.text}
                onClick={() => { onPick(p.text); onClose(); }}
              />
            ))}
          </>
        ) : (
          <>
            {root.map((p, i) => (
              <MenuItem
                key={`${p.text}-${i}`}
                label={p.text}
                onClick={() => { onPick(p.text); onClose(); }}
              />
            ))}
            {root.length > 0 && groups.length > 0 && <MenuSep />}
            {groups.map(g => (
              <MenuItem
                key={g.name}
                icon={<Folder size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
                label={`${g.name} (${g.phrases.length})`}
                onClick={() => setOpenName(g.name)}
              />
            ))}
          </>
        )}
        {phrases.length === 0 && (
          <div style={{
            padding: `${SP.sm}px ${SP.sm}px ${SP.md}px`, fontFamily: FONT.sans, fontSize: FS.sm,
            color: C.textMuted, lineHeight: 1.45,
          }}>
            {failed
              ? 'Не удалось загрузить фразы — нет связи с сервером.'
              : loaded
                ? 'Фраз пока нет. Заведите те, что шлёте чаще всего, — они будут уходить одним нажатием.'
                : 'Загружаем…'}
          </div>
        )}
      </div>
    </Menu>
  );
}

// Иконка кнопки — общая точка: попап и кнопка в композере должны читаться как одно
export function QuickPhrasesIcon() {
  return <MessageSquareText size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
}

function newId(): string {
  try { return crypto.randomUUID(); } catch { return `qp-${Date.now()}-${Math.round(Math.random() * 1e6)}`; }
}

// Что именно подтверждаем удалением (непустое удаляем только с подтверждением:
// в строке живёт до 500 символов, а кнопка стоит вплотную к стрелке порядка)
type PendingDelete =
  | { kind: 'row'; sectionId: string; rowId: string; label: string }
  | { kind: 'section'; sectionId: string; label: string; count: number };

// Форма правки набора. Список ведётся СЕКЦИЯМИ (корень + группы), потому что попап
// двухуровневый: в плоском списке стрелки порядка врали бы — перестановка через
// границу группы в попапе ничего бы не изменила.
export function QuickPhrasesDialog({ onClose }: { onClose: () => void }) {
  const saved = useQuickPhrases();
  // Черновик правится локально и уезжает на сервер целиком по «Сохранить»:
  // построчный PUT на каждый символ бил бы по users.json без нужды
  const [sections, setSections] = useState<QuickPhraseSection[]>(() => toSections(saved, newId));
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // id секции, чьё имя правится прямо в заголовке
  const [renamingId, setRenamingId] = useState<string | null>(null);
  // id строки, которой отдать фокус после появления (добавили через Enter/кнопку)
  const [focusRowId, setFocusRowId] = useState<string | null>(null);
  const [rowMenu, setRowMenu] = useState<{ sectionId: string; rowId: string; anchor: DOMRect } | null>(null);
  const [sectionMenu, setSectionMenu] = useState<{ sectionId: string; anchor: DOMRect } | null>(null);
  const [pendingDelete, setPendingDelete] = useState<PendingDelete | null>(null);
  // На узком экране действия строки уходят под поле и берут тач-размер
  // (гайд: цель не меньше 40)
  const isMobile = useIsMobileModal();
  const btnSize = isMobile ? 'lg' : 'sm';

  const patchSection = (id: string, fn: (s: QuickPhraseSection) => QuickPhraseSection) =>
    setSections(list => list.map(s => (s.id === id ? fn(s) : s)));

  const setRowText = (sectionId: string, rowId: string, text: string) =>
    patchSection(sectionId, s => ({ ...s, rows: s.rows.map(r => (r.id === rowId ? { ...r, text } : r)) }));

  const addRow = (sectionId: string) => {
    const id = newId();
    patchSection(sectionId, s => ({ ...s, rows: [...s.rows, { id, text: '' }] }));
    setFocusRowId(id);
  };

  const removeRow = (sectionId: string, rowId: string) =>
    patchSection(sectionId, s => ({ ...s, rows: s.rows.filter(r => r.id !== rowId) }));

  // Порядок строк ВНУТРИ секции — он же порядок пунктов внутри группы попапа
  const moveRow = (sectionId: string, rowId: string, delta: number) =>
    patchSection(sectionId, s => {
      const i = s.rows.findIndex(r => r.id === rowId);
      return { ...s, rows: movePhrase(s.rows, i, delta) };
    });

  const moveRowToEdge = (sectionId: string, rowId: string, edge: 'start' | 'end') =>
    patchSection(sectionId, s => {
      const row = s.rows.find(r => r.id === rowId);
      if (!row) return s;
      const rest = s.rows.filter(r => r.id !== rowId);
      return { ...s, rows: edge === 'start' ? [row, ...rest] : [...rest, row] };
    });

  // Перенос строки в другую секцию: строка встаёт в её конец — так же, как встала бы
  // заново заведённая, и человеку не надо гадать, куда она делась
  const moveRowToSection = (fromId: string, rowId: string, toId: string) => {
    if (fromId === toId) return;
    setSections(list => {
      const row = list.find(s => s.id === fromId)?.rows.find(r => r.id === rowId);
      if (!row) return list;
      return list.map(s => {
        if (s.id === fromId) return { ...s, rows: s.rows.filter(r => r.id !== rowId) };
        if (s.id === toId) return { ...s, rows: [...s.rows, row] };
        return s;
      });
    });
  };

  // Новая группа рождается сразу с пустой строкой и в режиме переименования:
  // пустой группы на сервере не существует (она выводится из фраз), поэтому
  // заводить безымянную секцию «на будущее» бессмысленно
  const addSection = (rowToMove?: { sectionId: string; rowId: string }) => {
    const id = newId();
    const rowId = newId();
    setSections(list => {
      const moved = rowToMove
        ? list.find(s => s.id === rowToMove.sectionId)?.rows.find(r => r.id === rowToMove.rowId)
        : undefined;
      const cleaned = rowToMove
        ? list.map(s => (s.id === rowToMove.sectionId ? { ...s, rows: s.rows.filter(r => r.id !== rowToMove.rowId) } : s))
        : list;
      return [...cleaned, { id, name: '', rows: moved ? [moved] : [{ id: rowId, text: '' }] }];
    });
    setRenamingId(id);
  };

  const renameSection = (id: string, name: string) =>
    patchSection(id, s => ({ ...s, name }));

  // Порядок ГРУПП в попапе. Корневая секция всегда первая и в перестановке
  // не участвует — фразы без группы показываются до любых групп
  const moveSection = (id: string, delta: number) =>
    setSections(list => {
      const i = list.findIndex(s => s.id === id);
      if (i <= 0 || i + delta <= 0) return list;
      return movePhrase(list, i, delta);
    });

  // Разгруппировать: фразы уезжают в корень (в конец), сама секция исчезает
  const ungroupSection = (id: string) =>
    setSections(list => {
      const target = list.find(s => s.id === id);
      if (!target) return list;
      return list
        .filter(s => s.id !== id)
        .map(s => (s.name === null ? { ...s, rows: [...s.rows, ...target.rows] } : s));
    });

  const removeSection = (id: string) => setSections(list => list.filter(s => s.id !== id));

  // Имя правится «вживую», а по завершении нормализуется: пустое имя = не группа,
  // поэтому секция разгруппировывается, а не остаётся безымянной
  const finishRename = (id: string) => {
    setRenamingId(null);
    const target = sections.find(s => s.id === id);
    if (!target) return;
    const name = (target.name ?? '').trim();
    if (!name) ungroupSection(id);
    else if (name !== target.name) renameSection(id, name);
  };

  const phrases = flattenSections(sections);
  const full = phrases.length >= QUICK_PHRASE_MAX_COUNT;
  const groupSections = sections.filter(s => s.name !== null);
  const rootSection = sections.find(s => s.name === null);
  // Заголовок корня нужен только когда есть группы: у простого набора он лишний шум
  const showRootHeader = groupSections.length > 0;

  const handleSave = async () => {
    setLoading(true);
    setError(null);
    try {
      // Пустые строки, дубли и потолок вычищает сервер — его итог и станет набором
      await saveQuickPhrases(phrases);
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось сохранить фразы');
    } finally {
      setLoading(false);
    }
  };

  const askRemoveRow = (sectionId: string, rowId: string, text: string) => {
    // Пустую строку сносим молча: подтверждать нечего
    if (!text.trim()) { removeRow(sectionId, rowId); return; }
    setPendingDelete({ kind: 'row', sectionId, rowId, label: text.trim() });
  };

  const askRemoveSection = (s: QuickPhraseSection) => {
    const count = s.rows.filter(r => r.text.trim()).length;
    if (count === 0) { removeSection(s.id); return; }
    setPendingDelete({ kind: 'section', sectionId: s.id, label: s.name ?? '', count });
  };

  const renderRow = (s: QuickPhraseSection, row: { id: string; text: string }, i: number) => {
    const actions = (
      <>
        <IconButton size={btnSize} title="Выше" disabled={i === 0} onClick={() => moveRow(s.id, row.id, -1)}>
          <ArrowUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
        <IconButton
          size={btnSize}
          title="Ниже"
          disabled={i === s.rows.length - 1}
          onClick={() => moveRow(s.id, row.id, 1)}
        >
          <ArrowDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
        <IconButton
          size={btnSize}
          title="Действия фразы"
          onClick={e => setRowMenu({
            sectionId: s.id, rowId: row.id,
            anchor: (e.currentTarget as HTMLElement).getBoundingClientRect(),
          })}
        >
          <MoreHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </IconButton>
      </>
    );

    const field = (
      <TextField
        value={row.text}
        onChange={v => setRowText(s.id, row.id, v.slice(0, QUICK_PHRASE_MAX_LENGTH))}
        placeholder="Например: продолжай"
        autoFocus={row.id === focusRowId}
        // Enter в списке добавляет СЛЕДУЮЩУЮ строку, а не сохраняет набор:
        // отправлять форму из любого из двух десятков полей — ловушка
        onEnter={() => addRow(s.id)}
      />
    );

    // Мобила: действия отдельной линией под полем — три тач-цели в один ряд
    // с полем не оставляют самой фразе места
    return isMobile ? (
      <div key={row.id} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        {field}
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <span style={{ flex: 1 }} />
          {actions}
        </div>
      </div>
    ) : (
      <div key={row.id} style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <div style={{ flex: 1, minWidth: 0 }}>{field}</div>
        {actions}
      </div>
    );
  };

  const renderSection = (s: QuickPhraseSection, showHeader: boolean) => {
    const filled = s.rows.filter(r => r.text.trim()).length;
    return (
      <div key={s.id} style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        {showHeader && (
          <div style={{
            position: 'sticky', top: 0, zIndex: 1, background: C.bgMain,
            display: 'flex', alignItems: 'center', gap: SP.sm,
            padding: `${SP.xs}px 0`, borderBottom: `1px solid ${C.borderLight}`,
          }}>
            {s.name !== null && <Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.textMuted} />}
            {renamingId === s.id ? (
              <div style={{ flex: 1, minWidth: 0 }}>
                <TextField
                  value={s.name ?? ''}
                  onChange={v => renameSection(s.id, v.slice(0, QUICK_PHRASE_MAX_GROUP_LENGTH))}
                  placeholder="Название группы"
                  autoFocus
                  onEnter={() => finishRename(s.id)}
                  onBlur={() => finishRename(s.id)}
                  onEscape={() => finishRename(s.id)}
                />
              </div>
            ) : (
              <>
                <FieldLabel>{s.name ?? 'Без группы'}</FieldLabel>
                <span style={{ fontSize: FS.xs, color: C.textMuted }}>({filled})</span>
              </>
            )}
            <span style={{ flex: 1 }} />
            {s.name !== null && renamingId !== s.id && (
              <IconButton
                size={btnSize}
                title="Действия группы"
                onClick={e => setSectionMenu({
                  sectionId: s.id,
                  anchor: (e.currentTarget as HTMLElement).getBoundingClientRect(),
                })}
              >
                <MoreHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              </IconButton>
            )}
          </div>
        )}
        <div style={{
          display: 'flex', flexDirection: 'column', gap: isMobile ? SP.md : SP.sm,
          paddingLeft: showHeader ? (isMobile ? SP.sm : SP.md) : 0,
        }}>
          {s.rows.map((row, i) => renderRow(s, row, i))}
          {/* Кнопка по содержимому и прижата влево: на всю ширину пунктирная плашка
              весила как строка набора, хотя это служебное действие. На мобиле —
              размер md, чтобы тач-цель осталась 40 */}
          <div style={{ display: 'flex' }}>
            <Button
              variant="ghost"
              size={isMobile ? 'md' : 'sm'}
              disabled={full}
              leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              onClick={() => addRow(s.id)}
            >
              Добавить фразу
            </Button>
          </div>
        </div>
      </div>
    );
  };

  const menuSection = rowMenu ? sections.find(s => s.id === rowMenu.sectionId) : undefined;
  const menuRowIndex = menuSection && rowMenu ? menuSection.rows.findIndex(r => r.id === rowMenu.rowId) : -1;
  const openSection = sectionMenu ? sections.find(s => s.id === sectionMenu.sectionId) : undefined;
  const openSectionIndex = openSection ? sections.indexOf(openSection) : -1;

  return (
    <>
      <Modal
        title="Быстрые фразы"
        subtitle="Фраза из списка уходит в чат одним нажатием — без правки в поле ввода. Набор личный и работает во всех чатах."
        width={MODAL_W.wide}
        cardStyle={isMobile ? undefined : { height: FORM_CARD_H }}
        onClose={onClose}
        footer={
          <ModalActions
            confirmLabel={loading ? 'Сохраняем…' : 'Сохранить'}
            onConfirm={handleSave}
            loading={loading}
            onCancel={onClose}
          />
        }
      >
        <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: SP.sm }}>
          <FieldLabel>Фразы</FieldLabel>
          {/* Счётчик и блокировка «Добавить» считают ОДНО И ТО ЖЕ число: иначе
              человек видит «23 из 24» при погасшей кнопке */}
          <span style={{ fontSize: FS.xs, color: full ? C.warningText : C.textMuted }}>
            {phrases.length} из {QUICK_PHRASE_MAX_COUNT}
          </span>
        </div>
        {full && (
          <div style={{ fontSize: FS.xs, color: C.textMuted }}>
            Набор заполнен: чтобы добавить фразу, удалите ненужную.
          </div>
        )}

        <div style={{
          display: 'flex', flexDirection: 'column', gap: SP.lg,
          // Десктоп: список забирает всю свободную высоту карточки и скроллится сам.
          // minHeight:0 обязателен — без него flex-элемент не даёт себя сжать, и
          // скролл уезжает наружу, унося шапку со счётчиком
          ...(isMobile ? null : { flex: 1, minHeight: 0, overflowY: 'auto', paddingRight: SP.xs }),
        }}>
          {rootSection && renderSection(rootSection, showRootHeader)}
          {groupSections.map(s => renderSection(s, true))}
        </div>

        <div style={{ display: 'flex' }}>
          <Button
            variant="ghost"
            size={isMobile ? 'md' : 'sm'}
            leftIcon={<FolderPlus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            onClick={() => addSection()}
          >
            Новая группа
          </Button>
        </div>

        {error && (
          <div style={{
            padding: SP.sm, borderRadius: 12,
            background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
            fontSize: FS.sm, color: C.dangerText,
          }}>
            {error}
          </div>
        )}
      </Modal>

      {/* Меню строки: дальние переносы и смена группы — здесь, чтобы в самой строке
          осталось поле, а не пять кнопок */}
      {rowMenu && menuSection && (
        <Menu anchor={rowMenu.anchor} onClose={() => setRowMenu(null)} minWidth={MENU_MIN_W}>
          <MenuItem
            icon={<ArrowUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="В начало"
            disabled={menuRowIndex <= 0}
            onClick={() => { moveRowToEdge(rowMenu.sectionId, rowMenu.rowId, 'start'); setRowMenu(null); }}
          />
          <MenuItem
            icon={<ArrowDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="В конец"
            disabled={menuRowIndex === menuSection.rows.length - 1}
            onClick={() => { moveRowToEdge(rowMenu.sectionId, rowMenu.rowId, 'end'); setRowMenu(null); }}
          />
          <MenuSep />
          {sections.filter(s => s.id !== rowMenu.sectionId).map(s => (
            <MenuItem
              key={s.id}
              icon={s.name === null
                ? undefined
                : <Folder size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              label={`Перенести в «${s.name === null ? 'Без группы' : s.name}»`}
              onClick={() => { moveRowToSection(rowMenu.sectionId, rowMenu.rowId, s.id); setRowMenu(null); }}
            />
          ))}
          <MenuItem
            icon={<FolderPlus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="В новую группу…"
            onClick={() => { addSection({ sectionId: rowMenu.sectionId, rowId: rowMenu.rowId }); setRowMenu(null); }}
          />
          <MenuSep />
          <MenuItem
            icon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Удалить фразу"
            danger
            onClick={() => {
              const row = menuSection.rows.find(r => r.id === rowMenu.rowId);
              setRowMenu(null);
              if (row) askRemoveRow(rowMenu.sectionId, row.id, row.text);
            }}
          />
        </Menu>
      )}

      {/* Меню группы: имя, порядок групп и судьба самой группы */}
      {sectionMenu && openSection && (
        <Menu anchor={sectionMenu.anchor} onClose={() => setSectionMenu(null)} minWidth={MENU_MIN_W}>
          <MenuItem
            icon={<Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Переименовать"
            onClick={() => { setRenamingId(openSection.id); setSectionMenu(null); }}
          />
          <MenuItem
            icon={<ArrowUp size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Выше"
            disabled={openSectionIndex <= 1}
            onClick={() => { moveSection(openSection.id, -1); setSectionMenu(null); }}
          />
          <MenuItem
            icon={<ArrowDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Ниже"
            disabled={openSectionIndex === sections.length - 1}
            onClick={() => { moveSection(openSection.id, 1); setSectionMenu(null); }}
          />
          <MenuSep />
          <MenuItem
            icon={<Ungroup size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Разгруппировать"
            onClick={() => { ungroupSection(openSection.id); setSectionMenu(null); }}
          />
          <MenuItem
            icon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
            label="Удалить с фразами"
            danger
            onClick={() => { setSectionMenu(null); askRemoveSection(openSection); }}
          />
        </Menu>
      )}

      {pendingDelete && (
        <ConfirmDialog
          title={pendingDelete.kind === 'row' ? 'Удалить фразу?' : 'Удалить группу?'}
          subtitle={pendingDelete.kind === 'row'
            ? `«${pendingDelete.label}» исчезнет из попапа композера.`
            : `Группа «${pendingDelete.label}» и ${pendingDelete.count} фраз(ы) в ней исчезнут из попапа.`}
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => {
            if (pendingDelete.kind === 'row') removeRow(pendingDelete.sectionId, pendingDelete.rowId);
            else removeSection(pendingDelete.sectionId);
            setPendingDelete(null);
          }}
          onCancel={() => setPendingDelete(null)}
        />
      )}
    </>
  );
}
