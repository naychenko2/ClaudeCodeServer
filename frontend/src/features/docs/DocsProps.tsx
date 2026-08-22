// Свойства документа: лента-редактор под шапкой открытого файла в центре и плашка
// главного значения в шапке превью панели «Документы».
//
// Живёт в features (а не в pages/workspace) ровно поэтому: потребителя два — панель
// документации и центральный просмотрщик файла, который лежит в components.
//
// Значения живут строками «**Ключ:** значение» в самом md-файле — правка уходит в
// репозиторий, поэтому оптимистичной подмены значения здесь нет: откат врал бы про то,
// что лежит на диске. Пока летит запрос, контрол приглушён.

import { useEffect, useRef, useState } from 'react';
import { Check, X } from 'lucide-react';
import type { DocDetail, DocEntry, DocPropertyDef, DocTypeSchema } from '../../types';
import { C, FONT, FS, SP } from '../../lib/design';
import { Badge, Button, Dot, FileTypeTile, Menu, MenuItem, MenuSep, Modal, TextField } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { labelOfValue, propDotColor, propValue, toneOfValue } from '../../lib/docsTypes';
import { useIsMobile } from '../../lib/breakpoints';
import { useListAutoFocus } from '../../lib/listAutoFocus';

// Шапка складной секции превью — общая с блоком «Ссылаются сюда»: два разных вида
// у соседних секций одной панели читались бы как два разных элемента
export const docsSectionHeadStyle = {
  display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%',
  padding: `${SP.sm}px ${SP.md}px`, border: 'none', background: 'transparent', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary,
  textTransform: 'uppercase' as const, letterSpacing: '0.03em',
};

type SaveFn = (key: string, value: string | null) => void;

// Единая высота чипа в ленте. Примитивы приходят с разными габаритами (плашка — 26,
// кнопка xs — 24, поле ввода — все 40), и без общего значения ряд подскакивал на каждом
// раскрытии чипа в редактор: полоса под шапкой файла дёргалась от одного клика по дате
export function useChipHeight(): number {
  return useIsMobile() ? 40 : 26;
}

const chipBox = (h: number) => ({ height: h, minHeight: h });

// Поле ввода в ленте: собственный габарит контрола формы (padding 10/13, шрифт 14) выше
// чипа почти вдвое, поэтому в ряду его сжимаем до той же высоты
const fieldStyle = (h: number): React.CSSProperties => ({
  height: h, padding: `0 ${SP.sm}px`, fontSize: FS.xs,
});

// ─── Плашка значения выбора + меню смены ───────────────────────────────────

export function DocPropChip({ def, value, saving, onSave, label, tone, style }: {
  def: DocPropertyDef;
  value: string;
  saving: boolean;
  onSave: SaveFn;
  // Подпись плашки. Не передана — считается по схеме здесь же: иначе одно и то же
  // значение показывалось бы в шапке своим заголовком, а в ленте сырой строкой из файла
  label?: string;
  tone?: ReturnType<typeof toneOfValue>;
  style?: React.CSSProperties;
}) {
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const current = value.trim();
  // rect берём СРАЗУ: к моменту вызова функции-обновления React уже обнулил currentTarget
  const toggle = (e: React.MouseEvent) => {
    const rect = e.currentTarget.getBoundingClientRect();
    setAnchor(a => (a ? null : rect));
  };

  // Меню — СИБЛИНГОМ триггера, а не его ребёнком: событие из портала всплывает по
  // React-дереву, и внутри кнопки клик по пункту (и по подложке) долетал бы до неё же,
  // открывая меню заново — оно не закрывалось бы вовсе
  const menu = anchor && (
    <ValueMenu def={def} value={current} anchor={anchor}
      onClose={() => setAnchor(null)} onSave={onSave} />
  );

  if (!current) {
    return (
      <>
        <Button size="xs" variant="ghost" disabled={saving} style={style} onClick={toggle}>
          {def.title || def.key}: не задан
        </Button>
        {menu}
      </>
    );
  }

  return (
    <>
      <Badge
        tone={tone ?? toneOfValue(def, current)}
        dot
        active={!!anchor}
        disabled={saving}
        title={`${def.title || def.key}: ${current}`}
        style={{ ...style, opacity: saving ? 0.6 : 1 }}
        onClick={toggle}
      >
        {label ?? labelOfValue(def, current)}
      </Badge>
      {menu}
    </>
  );
}

// Меню значений: точка в цвете значения + галка у текущего — тот же язык, что у меню
// настроек панели и статуса задачи
function ValueMenu({ def, value, anchor, onClose, onSave }: {
  def: DocPropertyDef; value: string; anchor: DOMRect; onClose: () => void; onSave: SaveFn;
}) {
  return (
    <Menu anchor={anchor} minWidth={200} maxHeight={280} onClose={onClose}>
      {(def.choices ?? []).map(choice => (
        <MenuItem
          key={choice.value}
          icon={<Dot color={propDotColor(choice.color)} size={6} />}
          label={
            <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%' }}>
              {choice.title || choice.value}
              {choice.value.toLowerCase() === value.toLowerCase() && (
                <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
                  style={{ marginLeft: 'auto', color: C.accent }} />
              )}
            </span>
          }
          onClick={() => { onSave(def.key, choice.value); onClose(); }}
        />
      ))}
      {/* Обязательное свойство очистить нельзя — сервер всё равно откажет */}
      {value && !def.required && (
        <>
          <MenuSep />
          <MenuItem icon={<X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} label="Очистить"
            onClick={() => { onSave(def.key, null); onClose(); }} />
        </>
      )}
    </Menu>
  );
}

// ─── Выбор документа для свойства-ссылки ───────────────────────────────────

// Список берём из уже загруженного индекса — сервер о выборе цели ничего не спрашивают
function DocRefPicker({ def, value, link, index, saving, onSave, style }: {
  def: DocPropertyDef; value: string; link?: string | null;
  index: DocEntry[]; saving: boolean; onSave: SaveFn; style?: React.CSSProperties;
}) {
  const isMobile = useIsMobile();
  const [open, setOpen] = useState<DOMRect | null>(null);
  const [q, setQ] = useState('');
  const searchAutoFocus = useListAutoFocus();

  const current = link ? index.find(d => d.path === link) : null;
  const matches = index
    .filter(d => !d.binary)
    .filter(d => !q.trim() || `${d.title} ${d.path}`.toLowerCase().includes(q.trim().toLowerCase()))
    .slice(0, 50);

  const list = (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
      <div style={{ padding: `${SP.xs}px ${SP.sm}px` }}>
        <TextField value={q} onChange={setQ} placeholder="Поиск документа" autoFocus={searchAutoFocus} />
      </div>
      {value && (
        <MenuItem icon={<X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} label="Очистить"
          onClick={() => { onSave(def.key, null); setOpen(null); }} />
      )}
      {matches.map(d => (
        <MenuItem
          key={d.path}
          icon={<FileTypeTile name={d.path} />}
          label={
            <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{d.title}</span>
              <span style={{ fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis' }}>{d.path}</span>
            </span>
          }
          onClick={() => { onSave(def.key, d.path); setOpen(null); setQ(''); }}
        />
      ))}
      {matches.length === 0 && (
        <div style={{ padding: SP.sm, fontSize: FS.xs, color: C.textMuted }}>Ничего не нашлось</div>
      )}
    </div>
  );

  return (
    <>
      <Button
        size="xs"
        variant="ghost"
        disabled={saving}
        onClick={e => { const rect = e.currentTarget.getBoundingClientRect(); setOpen(o => (o ? null : rect)); }}
        style={{ maxWidth: '100%', overflow: 'hidden', textOverflow: 'ellipsis', display: 'block', ...style }}
      >
        {current?.title || value || 'выбрать документ'}
      </Button>
      {/* На телефоне попап с полем ввода накрывает клавиатура, а Menu без якоря считает
          позицию от родителя — там Modal, он сам становится шторкой снизу. С кнопкой
          отмены: у шторки на весь экран иначе единственный выход — узкая полоска сверху */}
      {open && (isMobile
        ? (
          <Modal
            width={420}
            title={def.title || def.key}
            onClose={() => setOpen(null)}
            footer={<Button variant="ghost" onClick={() => setOpen(null)}>Закрыть</Button>}
          >{list}</Modal>
        )
        : <Menu anchor={open} minWidth={280} maxHeight={340} onClose={() => setOpen(null)}>{list}</Menu>)}
    </>
  );
}

// ─── Текстовое поле с черновиком ───────────────────────────────────────────

// Черновик локальный: правка уходит в файл, оттуда прилетает filesChanged, панель
// перечитывает документ — и без черновика набранный текст затирался бы на полуслове.
// Синхронизируемся с данными, только пока поле не в фокусе
function TextValue({ def, value, saving, onSave, autoFocus, style, title }: {
  def: DocPropertyDef; value: string; saving: boolean; onSave: SaveFn;
  autoFocus?: boolean; style?: React.CSSProperties; title?: string;
}) {
  const [draft, setDraft] = useState(value);
  const focused = useRef(false);
  const escaped = useRef(false);

  useEffect(() => { if (!focused.current) setDraft(value); }, [value]);

  return (
    <TextField
      value={draft}
      onChange={setDraft}
      disabled={saving}
      autoFocus={autoFocus}
      style={style}
      title={title}
      placeholder="—"
      onFocus={() => { focused.current = true; escaped.current = false; }}
      onBlur={() => {
        focused.current = false;
        // Esc — отказ от правки: возвращаем то, что лежит в файле, и ничего не пишем
        if (escaped.current) { setDraft(value); return; }
        if (draft.trim() !== value.trim()) onSave(def.key, draft.trim());
      }}
      onEnter={() => { if (draft.trim() !== value.trim()) onSave(def.key, draft.trim()); }}
      onEscape={() => { escaped.current = true; setDraft(value); }}
    />
  );
}

// ─── Лента свойств ─────────────────────────────────────────────────────────

// Ряд чипов-редакторов: выбор — плашка с меню, дата и текст раскрываются полем на месте,
// ссылка — выбор документа. Заворачивается по ширине, поэтому живёт и в полосе под
// панели документации, и в колонке свойств у открытого документа
export function DocsPropsBar({ type, doc, index, savingKey, onSave, flat }: {
  type: DocTypeSchema;
  doc: DocDetail;
  index: DocEntry[];
  savingKey: string | null;
  onSave: SaveFn;
  // Обрамление даёт хозяин полосы: свои отступы и черта снизу тут были бы вторыми
  flat?: boolean;
}) {
  // «Показать пустые» — свойство документа, а не панели: у следующего свой состав, и
  // запоминать раскрытие незачем
  const [all, setAll] = useState(false);
  useEffect(() => { setAll(false); }, [doc.path]);
  const h = useChipHeight();

  // Ключи, которые есть в файле, но схемой не описаны, лента НЕ показывает: править их
  // всё равно нечем (вид свойства неизвестен), а текст документа мы не режем — строка
  // «**Основание:** …» видна в нём на своём месте. Раньше их приходилось дублировать
  // здесь, потому что шапка вырезалась из превью
  const isEmpty = (def: DocPropertyDef) => (propValue(doc, def.key)?.value ?? '').trim().length === 0;
  const hidden = all ? [] : type.properties.filter(isEmpty);
  const shown = all ? type.properties : type.properties.filter(d => !isEmpty(d));

  if (shown.length === 0 && hidden.length === 0) return null;

  return (
    <div style={{
      display: 'flex', flexWrap: 'wrap', alignItems: 'center',
      // По горизонтали свойства разделены заметно, по вертикали (перенос строки) — плотно:
      // внутри одного свойства подпись и значение стоят рядом, и без этой разницы соседние
      // свойства читались бы как одно
      columnGap: SP.lg, rowGap: SP.xs,
      // Высота ряда задана чипом: без неё раскрытие чипа в поле ввода толкало бы полосу
      minHeight: h,
      ...(flat
        ? { flex: 1, minWidth: 0 }
        : { paddingBottom: SP.sm, marginBottom: SP.md, borderBottom: `1px solid ${C.border}` }),
    }}>
      {shown.map(def => (
        <PropChip key={def.key} def={def} doc={doc} index={index} h={h}
          saving={savingKey === def.key} onSave={onSave} />
      ))}
      {/* Незаполненные свёрнуты в счётчик: у типа с шестью свойствами лента иначе
          превращается в частокол прочерков. Совсем прятать их нельзя — незаполненное
          свойство так никто и не заполнит */}
      {hidden.length > 0 && (
        <Button size="xs" variant="ghost" onClick={() => setAll(true)} style={chipBox(h)}
          title="Показать незаполненные свойства">+{hidden.length}</Button>
      )}
    </div>
  );
}

// Ключ свойства перед значением: приглушён, потому что в ленте важнее само значение.
// У выбора ключа нет вовсе — цвет плашки и есть его подпись
const keyStyle: React.CSSProperties = { color: C.textMuted, marginRight: SP.xs };

// ─── Свойство: подпись + контрол ───────────────────────────────────────────

// Контрол значения БЕЗ подписи — общий для обоих представлений. `expanded` меняет только
// поведение даты и текста: в ленте они свёрнуты в чип (ряд полей ввода перестаёт читаться
// шапкой документа), в сайдбаре места хватает, и поле стоит раскрытым сразу
export function PropControl({ def, doc, index, saving, onSave, h, expanded }: {
  def: DocPropertyDef; doc: DocDetail; index: DocEntry[]; saving: boolean; onSave: SaveFn;
  h: number; expanded?: boolean;
}) {
  const prop = propValue(doc, def.key);
  const value = prop?.value ?? '';

  // Плашка остаётся по содержимому и в колонке: растянутая во всю ширину, она читается
  // как поле ввода, хотя это значение-метка
  if (def.kind === 'choice')
    return <DocPropChip def={def} value={value} saving={saving} onSave={onSave}
      style={{ maxWidth: 220, alignSelf: 'flex-start', ...chipBox(h) }} />;

  if (def.kind === 'docLink')
    return <DocRefPicker def={def} value={value} link={prop?.link} index={index}
      saving={saving} onSave={onSave} style={chipBox(h)} />;

  if (expanded)
    return def.kind === 'date'
      ? <DateValue def={def} value={value} saving={saving} onSave={onSave} style={fieldStyle(h)} />
      : <TextValue def={def} value={value} saving={saving} onSave={onSave} style={fieldStyle(h)} />;

  return <InlineChip def={def} value={value} saving={saving} onSave={onSave} h={h} />;
}

function PropChip({ def, doc, index, saving, onSave, h }: {
  def: DocPropertyDef; doc: DocDetail; index: DocEntry[]; saving: boolean; onSave: SaveFn; h: number;
}) {
  // У выбора подписи нет: цвет плашки и есть подпись. У остальных видов она снаружи
  // контрола — у даты и текста внутри самого InlineChip, чтобы не прыгать при раскрытии
  if (def.kind === 'choice' || def.kind === 'date' || def.kind === 'text')
    return <PropControl def={def} doc={doc} index={index} saving={saving} onSave={onSave} h={h} />;

  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', minWidth: 0, maxWidth: '100%', ...chipBox(h) }}>
      <span style={keyStyle}>{def.title || def.key}</span>
      <PropControl def={def} doc={doc} index={index} saving={saving} onSave={onSave} h={h} />
    </span>
  );
}

// Дата и текст: в ленте это чип, по клику раскрывающийся в поле на месте. Держать поля
// раскрытыми всё время нельзя — ряд полей ввода перестаёт читаться шапкой документа.
// Закрывается по уходу фокуса: blur поля всплывает сюда уже после того, как редактор
// решил, сохранять правку или откатить её.
//
// Подпись стоит СНАРУЖИ контрола и не двигается при раскрытии: внутри кнопки она уезжала
// бы в поле ввода и обратно, и ряд «моргал» на каждый клик
function InlineChip({ def, value, saving, onSave, h }: {
  def: DocPropertyDef; value: string; saving: boolean; onSave: SaveFn; h: number;
}) {
  const [editing, setEditing] = useState(false);

  return (
    <span
      onBlur={editing ? () => setEditing(false) : undefined}
      style={{ display: 'inline-flex', alignItems: 'center', minWidth: 0, ...chipBox(h) }}
    >
      <span style={keyStyle}>{def.title || def.key}</span>
      {editing ? (
        <span style={{ width: 150 }}>
          {def.kind === 'date'
            ? <DateValue def={def} value={value} saving={saving} onSave={onSave} autoFocus style={fieldStyle(h)} />
            : <TextValue def={def} value={value} saving={saving} onSave={onSave} autoFocus style={fieldStyle(h)} />}
        </span>
      ) : (
        <Button
          size="xs" variant="ghost" disabled={saving}
          onClick={() => setEditing(true)}
          title={`${def.title || def.key} — изменить`}
          style={{ maxWidth: 240, opacity: saving ? 0.6 : 1, ...chipBox(h) }}
        >
          {/* Значение показываем ровно как в файле: продукт и пишет его как есть */}
          {value || '—'}
        </Button>
      )}
    </span>
  );
}

// Поле даты. Значение не в формате ГГГГ-ММ-ДД (в файле бывает «23 июля 2026») — поле даты
// показало бы пустоту, а blur записал бы её вместо текста. В таком случае деградируем
// в обычное текстовое поле и говорим, какой формат ждём
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

function DateValue({ def, value, saving, onSave, autoFocus, style }: {
  def: DocPropertyDef; value: string; saving: boolean; onSave: SaveFn;
  autoFocus?: boolean; style?: React.CSSProperties;
}) {
  // Подсказку о формате даём подсказкой поля, а не строкой под ним: в ряду чипов вторая
  // строка растянула бы полосу ровно так же, как это делал крупный контрол
  if (value && !ISO_DATE.test(value.trim()))
    return (
      <TextValue def={def} value={value} saving={saving} onSave={onSave}
        autoFocus={autoFocus} style={style} title="Формат даты — ГГГГ-ММ-ДД" />
    );

  return (
    <DateValueField def={def} value={value} saving={saving} onSave={onSave}
      autoFocus={autoFocus} style={style} />
  );
}

// Пишем по завершении правки, а не на каждый ввод: при правке уже заполненной даты
// браузер отдаёт промежуточное пустое значение, и посимвольное сохранение уносило бы
// эту пустоту в репозиторий
function DateValueField({ def, value, saving, onSave, autoFocus, style }: {
  def: DocPropertyDef; value: string; saving: boolean; onSave: SaveFn;
  autoFocus?: boolean; style?: React.CSSProperties;
}) {
  const [draft, setDraft] = useState(value);
  const focused = useRef(false);

  useEffect(() => { if (!focused.current) setDraft(value); }, [value]);

  return (
    <TextField
      type="date"
      value={draft}
      disabled={saving}
      autoFocus={autoFocus}
      style={style}
      onChange={setDraft}
      onFocus={() => { focused.current = true; }}
      onBlur={() => {
        focused.current = false;
        const next = draft.trim();
        if (next === value.trim()) return;
        // Пустое поле — это «снять значение»; частично набранная дата не сохраняется
        if (next.length > 0 && !ISO_DATE.test(next)) { setDraft(value); return; }
        onSave(def.key, next.length > 0 ? next : null);
      }}
    />
  );
}

