// Редактор типов документов: какие типы бывают, к каким папкам привязаны, какие у них
// свойства и какие значения у выбора.
//
// Схема живёт ТОЛЬКО в файле .docs репозитория — тип документа это свойство самого корпуса
// («всё в docs/adr — решения»), а не предпочтение конкретного владельца папки. Поэтому файла
// нет → он создаётся вместе с действующей областью, и кнопка честно об этом говорит.
//
// Три «страницы» внутри одной модалки (типы → свойства типа → значения выбора): тот же приём,
// что у меню создания в панели. Одна модалка — одно закрытие и одна анимация.

import { useEffect, useState } from 'react';
import { ArrowLeft, Check, ChevronDown, ChevronUp, Plus, Tags, Trash2 } from 'lucide-react';
import type {
  DocPropertyChoice, DocPropertyColor, DocPropertyDef, DocPropertyKind, DocsScopeInfo, DocTypeSchema,
} from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Badge, Button, EmptyState, IconButton, Menu, MenuItem, Modal, ModalActions, TextField } from '../../components/ui';
import { ScopeRow, SectionTitle } from './DocsScopeDialog';
import { COLOR_LABEL, COLOR_ORDER, KIND_LABEL, PROP_TONE } from '../../lib/docsTypes';

const SCOPE_FILE = '.docs';
const KINDS: DocPropertyKind[] = ['choice', 'date', 'text', 'docLink'];

// Меню внутри диалога открываются ПО ЯКОРЮ (fixed по координатам кнопки): обычное меню
// в потоке формы растягивало её по высоте и добавляло диалогу полосы прокрутки, а само
// выглядело вложенным во второй попап. Плата за fixed — координаты протухают при прокрутке
// формы, поэтому на скролл меню закрывается
function useCloseOnScroll(open: boolean, close: () => void) {
  useEffect(() => {
    if (!open) return;
    const onScroll = () => close();
    window.addEventListener('scroll', onScroll, true);
    return () => window.removeEventListener('scroll', onScroll, true);
  }, [open, close]);
}

// id генерится один раз при создании и при переименовании НЕ пересчитывается: по нему
// документы связаны со своим типом, и пересборка порвала бы связь у всех разом
function newId(prefix: string): string {
  try { return `${prefix}-${crypto.randomUUID().slice(0, 8)}`; } catch { return `${prefix}-${Date.now()}`; }
}

// Пресет под то, что в этом репозитории уже лежит: самый дешёвый способ показать, зачем
// фича нужна, — предложить готовый тип вместо пустого списка
function adrPreset(): DocTypeSchema {
  return {
    id: 'adr',
    title: 'Решение (ADR)',
    folders: ['docs/adr'],
    match: 'ADR-*.md',
    badgeProperty: 'Статус',
    properties: [
      {
        key: 'Статус', kind: 'choice', required: true,
        choices: [
          { value: 'Предложено', color: 'info' },
          { value: 'Принято', color: 'success' },
          { value: 'Отклонено', color: 'danger' },
          { value: 'Заменено', color: 'gray' },
          { value: 'Устарело', color: 'warning' },
        ],
      },
      { key: 'Дата', kind: 'date', autoUpdate: true },
      { key: 'Принимающие решение', kind: 'text' },
      { key: 'Заменено', kind: 'docLink' },
    ],
  };
}

interface Props {
  projectId: string;
  info: DocsScopeInfo;
  onSaved: (info: DocsScopeInfo) => void;
  onClose: () => void;
}

export function DocsTypesDialog({ projectId, info, onSaved, onClose }: Props) {
  // Правим ПАТЧАМИ по объектам, пришедшим с сервера, а не пересобираем схему с нуля:
  // .docs версионируется и правится руками, и новая версия формата не должна молча
  // урезаться этим фронтом до полей, о которых он знает
  const [rows, setRows] = useState<DocTypeSchema[]>(() => (info.docTypes ?? []).map(t => ({ ...t })));
  const [page, setPage] = useState<{ typeIdx?: number; propIdx?: number }>({});
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [confirmClose, setConfirmClose] = useState(false);
  // Что-то правили: сравниваем с тем, что пришло с сервера
  const dirty = JSON.stringify(rows) !== JSON.stringify(info.docTypes ?? []);

  const type = page.typeIdx !== undefined ? rows[page.typeIdx] : null;
  const prop = type && page.propIdx !== undefined ? type.properties[page.propIdx] : null;

  const patchType = (i: number, patch: Partial<DocTypeSchema>) =>
    setRows(rs => rs.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));

  const patchProp = (ti: number, pi: number, patch: Partial<DocPropertyDef>) =>
    setRows(rs => rs.map((r, idx) => (idx !== ti ? r : {
      ...r,
      properties: r.properties.map((p, j) => (j === pi ? { ...p, ...patch } : p)),
    })));

  const moveIn = <T,>(list: T[], i: number, dir: -1 | 1): T[] => {
    const j = i + dir;
    if (j < 0 || j >= list.length) return list;
    const next = [...list];
    [next[i], next[j]] = [next[j], next[i]];
    return next;
  };

  const validate = (): string | null => {
    for (const t of rows) {
      if (!t.title.trim()) return 'У каждого типа должно быть название';
      if (t.folders.length === 0) return `Тип «${t.title}» ни к чему не привязан — выберите папку`;
      if (t.properties.length === 0) return `У типа «${t.title}» нет ни одного свойства`;
      const keys = new Set<string>();
      for (const p of t.properties) {
        const key = p.key.trim().toLowerCase();
        if (!key) return `У типа «${t.title}» есть свойство без ключа`;
        if (keys.has(key)) return `Свойство «${p.key}» у типа «${t.title}» повторяется`;
        keys.add(key);
        if (p.kind === 'choice' && !(p.choices ?? []).some(c => c.value.trim()))
          return `У свойства «${p.key}» нет ни одного значения`;
      }
    }
    if (new Set(rows.map(t => t.title.trim().toLowerCase())).size !== rows.length)
      return 'Названия типов повторяются';
    return null;
  };

  const save = async () => {
    const problem = validate();
    if (problem) { setError(problem); return; }
    setSaving(true);
    setError(null);
    try {
      const res = await api.docs.setDocTypes(projectId, rows);
      onSaved(res.scope);
      onClose();
    } catch {
      setSaving(false);
      setError('Не удалось сохранить типы документов');
    }
  };

  // Битый .docs перезаписывать нельзя: мы не знаем, что в нём было написано руками
  const broken = !!info.scopeFileError;

  return (
    <Modal
      width={460}
      title={type ? (prop ? `${type.title} · ${prop.key || 'свойство'}` : type.title) : 'Типы документов'}
      subtitle={type
        ? undefined
        : 'Какие бывают документы и какие у них свойства — например статус решения'}
      onClose={() => {
        // Крестик и Esc поднимают на уровень вверх, а не закрывают всё разом
        if (page.propIdx !== undefined) { setPage(p => ({ typeIdx: p.typeIdx })); return; }
        if (page.typeIdx !== undefined) { setPage({}); return; }
        // С первой страницы — выход. Правки живут только здесь и уйдут вместе с окном,
        // поэтому молча выбрасывать их нельзя
        if (dirty && !confirmClose) { setConfirmClose(true); return; }
        onClose();
      }}
      footer={
        <ModalActions
          confirmLabel={info.scopeSource === 'file' ? 'Сохранить' : `Создать ${SCOPE_FILE} и сохранить`}
          onConfirm={save}
          loading={saving}
          confirmDisabled={broken}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ fontSize: FS.sm, color: C.danger }}>{error}</div>}

      {/* Подтверждение выхода строкой, а не вторым окном поверх первого */}
      {confirmClose && (
        <div style={{
          display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap',
          padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md,
          background: C.dangerBg, color: C.dangerText, fontSize: FS.sm,
        }}>
          <span style={{ flex: 1, minWidth: 160 }}>Правки не сохранены. Закрыть и потерять их?</span>
          <Button size="sm" variant="ghost" onClick={() => setConfirmClose(false)}>Остаться</Button>
          <Button size="sm" variant="danger" onClick={onClose}>Закрыть</Button>
        </div>
      )}

      {/* Где будет храниться схема. Не украшение: от этого зависит, увидят ли её остальные */}
      {!type && (
        <div style={{
          padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md, background: C.bgInset,
          fontSize: FS.sm, color: C.textSecondary,
        }}>
          {info.scopeSource === 'file'
            ? <>Хранится в <code style={{ fontFamily: FONT.mono }}>{SCOPE_FILE}</code> репозитория — одинаково у всех, кто его открыл.</>
            : <>Файла <code style={{ fontFamily: FONT.mono }}>{SCOPE_FILE}</code> пока нет: он будет создан вместе с текущей областью документации.</>}
        </div>
      )}
      {broken && (
        <div style={{ fontSize: FS.sm, color: C.danger }}>
          Файл <code style={{ fontFamily: FONT.mono }}>{SCOPE_FILE}</code> не разобран, сохранение
          заблокировано: {info.scopeFileError}
        </div>
      )}
      {info.docTypesError && !broken && (
        <div style={{ fontSize: FS.sm, color: C.danger }}>
          Секция типов в {SCOPE_FILE} не разобрана: {info.docTypesError}
        </div>
      )}

      {/* Уровни 2 и 3 — с кнопкой возврата: без неё из вложенной страницы не выбраться */}
      {type && (
        <Button variant="ghost" size="sm" leftIcon={<ArrowLeft size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => setPage(p => (p.propIdx !== undefined ? { typeIdx: p.typeIdx } : {}))}>
          Назад
        </Button>
      )}

      {/* ── Страница 3: значения свойства-выбора ── */}
      {type && prop && page.typeIdx !== undefined && page.propIdx !== undefined && (
        <ChoicesPage
          choices={prop.choices ?? []}
          onChange={choices => patchProp(page.typeIdx!, page.propIdx!, { choices })}
        />
      )}

      {/* ── Страница 2: один тип ── */}
      {type && !prop && page.typeIdx !== undefined && (
        <TypePage
          type={type}
          info={info}
          onPatch={patch => patchType(page.typeIdx!, patch)}
          onOpenChoices={pi => setPage({ typeIdx: page.typeIdx, propIdx: pi })}
          moveIn={moveIn}
        />
      )}

      {/* ── Страница 1: список типов ── */}
      {!type && (
        rows.length === 0 ? (
          <EmptyState
            icon={<Tags size={20} strokeWidth={ICON_STROKE} />}
            title="Типов пока нет"
            subtitle="Тип задаёт, какие свойства есть у документов папки — например статус и дата у решений"
            action={
              <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', justifyContent: 'center' }}>
                <Button size="sm" variant="primary" onClick={() => { setRows([adrPreset()]); setPage({ typeIdx: 0 }); }}>
                  Завести ADR
                </Button>
                <Button size="sm" variant="ghost" onClick={() => {
                  setRows([{ id: newId('type'), title: 'Новый тип', folders: [], match: null, badgeProperty: null, properties: [] }]);
                  setPage({ typeIdx: 0 });
                }}>
                  Пустой тип
                </Button>
              </div>
            }
          />
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            {rows.map((t, i) => {
              const badge = t.properties
                .find(p => p.key.toLowerCase() === (t.badgeProperty ?? '').toLowerCase())
                ?.choices?.[0];
              return (
                <div key={t.id} style={rowStyle}>
                  <Order onUp={() => setRows(rs => moveIn(rs, i, -1))} onDown={() => setRows(rs => moveIn(rs, i, 1))} />
                  <button onClick={() => setPage({ typeIdx: i })} style={openStyle}>
                    <span style={{ fontWeight: 600, color: C.textHeading }}>{t.title}</span>
                    <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
                      {t.folders.join(', ') || 'папка не выбрана'}{t.match ? ` · ${t.match}` : ''}
                    </span>
                  </button>
                  {badge && <Badge size="xs" tone={PROP_TONE[badge.color]}>{badge.value}</Badge>}
                  <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>
                    {t.properties.length}
                  </span>
                  <IconButton title="Удалить тип" tone="danger" size="sm"
                    onClick={() => setRows(rs => rs.filter((_, idx) => idx !== i))}>
                    <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
                  </IconButton>
                </div>
              );
            })}
            <Button variant="ghost" size="sm" leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              onClick={() => {
                setRows(rs => [...rs, { id: newId('type'), title: 'Новый тип', folders: [], match: null, badgeProperty: null, properties: [] }]);
                setPage({ typeIdx: rows.length });
              }}>
              Добавить тип
            </Button>
            {/* Правда, которую люди боятся: удаление типа не трогает сами документы */}
            <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `0 ${SP.sm}px` }}>
              Удаление типа или свойства не правит документы: строки вида
              <code style={{ fontFamily: FONT.mono }}> **Статус:** …</code> останутся в файлах,
              просто перестанут показываться свойствами.
            </div>
          </div>
        )
      )}
    </Modal>
  );
}

// ─── Страница типа ─────────────────────────────────────────────────────────

function TypePage({ type, info, onPatch, onOpenChoices, moveIn }: {
  type: DocTypeSchema;
  info: DocsScopeInfo;
  onPatch: (patch: Partial<DocTypeSchema>) => void;
  onOpenChoices: (propIdx: number) => void;
  moveIn: <T>(list: T[], i: number, dir: -1 | 1) => T[];
}) {
  const [mask, setMask] = useState(type.match ?? '');
  // Индекс свойства + rect его кнопки: меню открывается по якорю, поэтому координаты
  // снимаются в момент клика (позже React обнулит currentTarget)
  const [kindOpen, setKindOpen] = useState<{ i: number; rect: DOMRect } | null>(null);
  const [badgeOpen, setBadgeOpen] = useState<DOMRect | null>(null);
  useCloseOnScroll(!!kindOpen, () => setKindOpen(null));
  useCloseOnScroll(!!badgeOpen, () => setBadgeOpen(null));

  const toggleFolder = (path: string) => onPatch({
    folders: type.folders.includes(path)
      ? type.folders.filter(f => f !== path)
      : [...type.folders, path],
  });

  const choiceProps = type.properties.filter(p => p.kind === 'choice');

  return (
    <>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <SectionTitle>Название</SectionTitle>
        <div style={{ padding: `0 ${SP.sm}px` }}>
          <TextField value={type.title} onChange={v => onPatch({ title: v })} placeholder="Например: Решение (ADR)" />
        </div>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <SectionTitle note="папка берётся со всем содержимым">Где применяется</SectionTitle>
        <div>
          {(info.folderCandidates ?? []).map(c => (
            <ScopeRow key={c.path} label={c.path} hint={c.exists ? undefined : 'нет на диске'}
              on={type.folders.includes(c.path)} onClick={() => toggleFolder(c.path)} />
          ))}
          {/* Папка, выбранная руками и отсутствующая среди кандидатов, не должна пропадать */}
          {type.folders.filter(f => !(info.folderCandidates ?? []).some(c => c.path === f)).map(f => (
            <ScopeRow key={f} label={f} on onClick={() => toggleFolder(f)} />
          ))}
        </div>
        <div style={{ padding: `0 ${SP.sm}px`, display: 'flex', flexDirection: 'column', gap: 2 }}>
          <TextField value={mask} onChange={setMask} placeholder="Маска имени: ADR-*.md"
            onBlur={() => onPatch({ match: mask.trim() || null })} />
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>
            Необязательно. Без маски тип получат все документы папки
          </span>
        </div>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <SectionTitle note="показывается плашкой в шапке и точкой в списке">Главное свойство</SectionTitle>
        <div style={{ padding: `0 ${SP.sm}px` }}>
          <Button size="sm" variant="ghost" disabled={choiceProps.length === 0}
            onClick={e => {
              const rect = e.currentTarget.getBoundingClientRect();
              setBadgeOpen(v => (v ? null : rect));
            }}>
            {type.badgeProperty
              || (choiceProps.length === 0 ? 'сначала добавьте свойство вида «Выбор»' : 'не выбрано')}
          </Button>
          {badgeOpen && (
            <Menu anchor={badgeOpen} minWidth={200} onClose={() => setBadgeOpen(null)}>
              <MenuItem label="Нет" onClick={() => { onPatch({ badgeProperty: null }); setBadgeOpen(null); }} />
              {choiceProps.map(p => (
                <MenuItem key={p.key} label={p.key}
                  onClick={() => { onPatch({ badgeProperty: p.key }); setBadgeOpen(null); }} />
              ))}
            </Menu>
          )}
        </div>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <SectionTitle note="ключ — как он написан в документе">Свойства</SectionTitle>
        {type.properties.map((p, i) => (
          <div key={i} style={rowStyle}>
            <Order
              onUp={() => onPatch({ properties: moveIn(type.properties, i, -1) })}
              onDown={() => onPatch({ properties: moveIn(type.properties, i, 1) })}
            />
            <span style={{ flex: '1 1 120px', minWidth: 0 }}>
              <TextField value={p.key} onChange={v => onPatch({
                properties: type.properties.map((x, j) => (j === i ? { ...x, key: v } : x)),
              })} placeholder="Статус" />
            </span>
            <span style={{ flexShrink: 0 }}>
              <Button size="xs" variant="ghost" onClick={e => {
                const rect = e.currentTarget.getBoundingClientRect();
                setKindOpen(v => (v?.i === i ? null : { i, rect }));
              }}>
                {KIND_LABEL[p.kind]}
              </Button>
              {kindOpen?.i === i && (
                <Menu anchor={kindOpen.rect} minWidth={180} onClose={() => setKindOpen(null)}>
                  {KINDS.map(k => (
                    <MenuItem key={k} label={KIND_LABEL[k]} onClick={() => {
                      onPatch({
                        properties: type.properties.map((x, j) => (j === i
                          ? { ...x, kind: k, choices: k === 'choice' ? (x.choices ?? []) : null, autoUpdate: k === 'date' ? x.autoUpdate : false }
                          : x)),
                      });
                      setKindOpen(null);
                    }} />
                  ))}
                </Menu>
              )}
            </span>
            {p.kind === 'choice' && (
              <Button size="xs" variant="ghost" onClick={() => onOpenChoices(i)}>
                {(p.choices ?? []).length} знач.
              </Button>
            )}
            <IconButton title="Удалить свойство" tone="danger" size="sm"
              onClick={() => onPatch({ properties: type.properties.filter((_, j) => j !== i) })}>
              <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
        ))}
        <Button variant="ghost" size="sm" leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => onPatch({ properties: [...type.properties, { key: '', kind: 'text' }] })}>
          Добавить свойство
        </Button>
        {/* Переименование ключа не блокируем — но и не врём, что оно правит документы */}
        <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `0 ${SP.sm}px` }}>
          Ключ — это текст перед двоеточием в самом документе. Переименуете здесь — строка
          в файлах останется прежней, и старое значение перестанет подхватываться.
        </div>
      </div>

      {/* «Дата смены» — тумблером у свойства-даты: отдельной строки он не стоит */}
      {type.properties.some(p => p.kind === 'date') && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <SectionTitle>Дата смены</SectionTitle>
          {type.properties.map((p, i) => p.kind !== 'date' ? null : (
            <ScopeRow
              key={i}
              label={`${p.key || 'дата'} — обновлять при смене других свойств`}
              on={!!p.autoUpdate}
              onClick={() => onPatch({
                properties: type.properties.map((x, j) => (j === i ? { ...x, autoUpdate: !x.autoUpdate } : x)),
              })}
            />
          ))}
        </div>
      )}
    </>
  );
}

// ─── Страница значений выбора ──────────────────────────────────────────────

// Цвет выбирается рядом пустых образцов — тех же плашек, но без подписи: значение
// набирается строкой выше, и повторять его шесть раз в палитре незачем. Роль образца
// говорит подсказка при наведении, выбранный отмечен галочкой. Ни одного сырого цвета —
// только роли дизайн-системы
function ChoicesPage({ choices, onChange }: {
  choices: DocPropertyChoice[];
  onChange: (choices: DocPropertyChoice[]) => void;
}) {
  const patch = (i: number, p: Partial<DocPropertyChoice>) =>
    onChange(choices.map((c, j) => (j === i ? { ...c, ...p } : c)));

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <SectionTitle note="значение пишется в документ как есть">Значения</SectionTitle>
      {choices.map((c, i) => (
        <div key={i} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, padding: `0 ${SP.sm}px` }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
            <span style={{ flex: 1, minWidth: 0 }}>
              <TextField value={c.value} onChange={v => patch(i, { value: v })} placeholder="Принято" />
            </span>
            <IconButton title="Удалить значение" tone="danger" size="sm"
              onClick={() => onChange(choices.filter((_, j) => j !== i))}>
              <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.xs }}>
            <span style={{ fontSize: FS.xs, color: C.textMuted, marginRight: SP.xs }}>Цвет</span>
            {COLOR_ORDER.map(color => (
              <Badge
                key={color}
                size="xs"
                tone={PROP_TONE[color]}
                active={c.color === color}
                title={COLOR_LABEL[color]}
                onClick={() => patch(i, { color: color as DocPropertyColor })}
                // Образец без текста: габарит держит minWidth, иначе плашка схлопнется
                // в полоску отступов и по ней будет не попасть
                style={{ minWidth: 34, justifyContent: 'center' }}
              >
                {c.color === color
                  ? <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ display: 'block' }} />
                  : ''}
              </Badge>
            ))}
          </div>
        </div>
      ))}
      <Button variant="ghost" size="sm" leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        onClick={() => onChange([...choices, { value: '', color: 'gray' }])}>
        Добавить значение
      </Button>
    </div>
  );
}

// ─── Мелочи ────────────────────────────────────────────────────────────────

function Order({ onUp, onDown }: { onUp: () => void; onDown: () => void }) {
  return (
    <span style={{ display: 'flex', flexDirection: 'column', flexShrink: 0 }}>
      <IconButton title="Выше" size="sm" onClick={onUp}>
        <ChevronUp size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
      <IconButton title="Ниже" size="sm" onClick={onDown}>
        <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
    </span>
  );
}

const rowStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap',
  padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md, background: C.bgInset,
};

const openStyle: React.CSSProperties = {
  flex: '1 1 140px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1,
  border: 'none', background: 'transparent', cursor: 'pointer', textAlign: 'left',
  fontFamily: FONT.sans, fontSize: FS.sm, padding: 0,
};
