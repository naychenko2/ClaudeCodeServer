// Настройка области панели «Документы»: что считать документацией проекта.
//
// Три независимые оси. Папки — дефолт docs/, но соглашение в проектах разное (wiki,
// documentation, спеки рядом с кодом). Файлы корня — поимённо: в корне лежит и код, папкой
// его не выберешь, а README/CHANGELOG/ROADMAP читают наравне с docs/. Типы файлов — дефолт
// markdown, но документацию пишут и в .txt, и в .rst.
//
// Кандидатов считает бэкенд (папки с документами неглубоко от корня, без node_modules и
// скрытых; файлы корня — по всем поддерживаемым расширениям). Ручной ввод оставлен для
// папки, которой в списке нет: пустой пока или лежащей глубже.

import { useEffect, useState } from 'react';
import { Check, FolderPlus } from 'lucide-react';
import type { DocsScopeInfo } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, IconButton, Modal, ModalActions, TextField } from '../../components/ui';

// Имя файла области в репозитории — оно же в бэкенде (DocsIndexService.ScopeFileName).
// Показываем его буквально: настройка версионируется, и человек должен знать, что
// коммитить и что искать в git status
const SCOPE_FILE = '.docs';

interface Props {
  projectId: string;
  onClose: () => void;
  // Сохранённая область — панель перечитывает по ней индекс
  onSaved: (info: DocsScopeInfo) => void;
}

// Строка списка с галкой: один вид для папок, файлов корня и типов
function ScopeRow({ label, hint, on, muted, title, onClick }: {
  label: string;
  hint?: string;
  on: boolean;
  muted?: boolean;      // выбор задан не этой строкой (папка внутри выбранной родительской)
  title?: string;
  onClick?: () => void;
}) {
  return (
    <button
      onClick={onClick}
      title={title}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
        padding: `${SP.xs}px ${SP.sm}px`, border: 'none', background: 'transparent',
        borderRadius: R.md, cursor: muted ? 'default' : 'pointer', textAlign: 'left',
        fontFamily: FONT.sans, fontSize: FS.sm,
        // Наследованная строка глушится целиком: снять её нельзя, и активный вид
        // обещал бы управление, которого нет
        opacity: muted ? 0.55 : 1,
      }}
    >
      <span style={{
        flex: 'none', width: 17, height: 17, borderRadius: 5,
        border: `1.5px solid ${on ? C.accent : C.border}`,
        background: on ? C.accent : C.bgWhite,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>
        {on && <Check size={11} strokeWidth={ICON_STROKE} color={C.onAccent} />}
      </span>
      <span style={{
        flex: 1, minWidth: 0, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textPrimary,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {label}
      </span>
      {hint && <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted }}>{hint}</span>}
    </button>
  );
}

function SectionTitle({ children, note }: { children: string; note?: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, padding: `0 ${SP.sm}px` }}>
      <span style={{
        fontSize: FS.xs, fontWeight: 700, color: C.textSecondary,
        textTransform: 'uppercase', letterSpacing: '0.03em',
      }}>
        {children}
      </span>
      {note && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{note}</span>}
    </div>
  );
}

export function DocsScopeDialog({ projectId, onClose, onSaved }: Props) {
  const [info, setInfo] = useState<DocsScopeInfo | null>(null);
  const [folders, setFolders] = useState<string[]>([]);
  const [rootFiles, setRootFiles] = useState<string[]>([]);
  const [types, setTypes] = useState<string[]>([]);
  // Папки, добавленные вручную в этом заходе: их нет среди кандидатов, но показать надо
  const [extra, setExtra] = useState<string[]>([]);
  const [manual, setManual] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    api.docs.scope(projectId)
      .then(s => {
        if (!alive) return;
        setInfo(s);
        setFolders(s.selected.folders);
        setRootFiles(s.selected.rootFiles);
        setTypes(s.selected.types);
      })
      .catch(() => { if (alive) setError('Не удалось загрузить настройку'); });
    return () => { alive = false; };
  }, [projectId]);

  // Выбор папки включает ВСЁ её поддерево (бэкенд обходит рекурсивно), поэтому вложенные
  // строки не отдельные галки, а «уже внутри». Иначе диалог врал бы: docs отмечена,
  // docs/adr нет — а документы из неё в области.
  const parentOf = (path: string, list: string[]) =>
    list.find(s => path.toLowerCase().startsWith(`${s.toLowerCase()}/`));

  const toggleFolder = (path: string) =>
    setFolders(prev => prev.includes(path)
      ? prev.filter(p => p !== path)
      // Вложенные снимаем: их и так покрывает родитель, а в сторе они были бы шумом
      : [...prev.filter(p => !p.toLowerCase().startsWith(`${path.toLowerCase()}/`)), path]);

  const toggleIn = (set: (fn: (prev: string[]) => string[]) => void, value: string) =>
    set(prev => prev.includes(value) ? prev.filter(v => v !== value) : [...prev, value]);

  // Ручная папка сразу отмечается: её вводят, чтобы включить, а не чтобы посмотреть
  const addManual = () => {
    const path = manual.trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    if (!path) return;
    const known = (info?.folderCandidates ?? []).some(c => c.path.toLowerCase() === path.toLowerCase())
      || extra.some(e => e.toLowerCase() === path.toLowerCase());
    if (!known) setExtra(prev => [...prev, path]);
    if (!folders.some(f => f.toLowerCase() === path.toLowerCase())) setFolders(prev => [...prev, path]);
    setManual('');
  };

  const save = () => {
    setSaving(true);
    // home не шлём: выбор начального документа временно снят из UI, а null в запросе
    // означает «не трогать» — сохранённое значение переживает правку области
    api.docs.setScope(projectId, { folders, rootFiles, types })
      .then(saved => { onSaved(saved); onClose(); })
      .catch(() => { setSaving(false); setError('Не удалось сохранить'); });
  };

  // Вынести область в репозиторий. Сначала сохраняем набранное в диалоге, потом переносим
  // в файл: иначе кнопка записала бы то, что было до правок, — а нажимают её как раз после
  const saveToRepo = () => {
    setSaving(true);
    api.docs.setScope(projectId, { folders, rootFiles, types })
      .then(() => api.docs.saveScopeFile(projectId))
      .then(saved => { onSaved(saved); onClose(); })
      .catch(() => { setSaving(false); setError(`Не удалось записать ${SCOPE_FILE} в репозиторий`); });
  };

  const toDefaults = () => {
    if (!info) return;
    setFolders(info.defaults.folders);
    setRootFiles(info.defaults.rootFiles);
    setTypes(info.defaults.types);
  };

  const folderRows = [
    ...(info?.folderCandidates ?? []).map(c => ({ path: c.path, count: c.count, exists: c.exists })),
    ...extra.map(path => ({ path, count: 0, exists: false })),
  ];

  return (
    <Modal
      width={460}
      title="Папки документации"
      subtitle="Область панели «Документы»: папка берётся со всем, что внутри"
      onClose={onClose}
      footer={
        <ModalActions
          confirmLabel="Сохранить"
          onConfirm={save}
          loading={saving}
          confirmDisabled={!info}
          onCancel={onClose}
        />
      }
    >
      {error && <div style={{ fontSize: FS.sm, color: C.danger }}>{error}</div>}
      {!info && !error && <div style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаем…</div>}

      {info && (
        <>
          {/* Где живёт настройка. Это не украшение: от источника зависит, увидят ли её
              остальные — файл версионируется вместе с документами, настройка продукта
              своя у каждого владельца папки */}
          <div style={{
            display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap',
            padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md, background: C.bgInset,
            fontSize: FS.sm, color: C.textSecondary,
          }}>
            {info.scopeSource === 'file' ? (
              <span>
                Хранится в <code style={{ fontFamily: FONT.mono }}>{SCOPE_FILE}</code> репозитория —
                одинаково у всех, кто его открыл.
              </span>
            ) : (
              <>
                <span style={{ flex: 1, minWidth: 180 }}>
                  Хранится в продукте: у каждого, кто подключил эту папку, своя.
                </span>
                <Button variant="ghost" size="sm" onClick={saveToRepo} disabled={saving}>
                  Вынести в репозиторий
                </Button>
              </>
            )}
          </div>
          {info.scopeFileError && (
            <div style={{ fontSize: FS.sm, color: C.danger }}>
              Файл <code style={{ fontFamily: FONT.mono }}>{SCOPE_FILE}</code> не разобран, действует
              настройка продукта: {info.scopeFileError}
            </div>
          )}

          {/* Типы файлов — первой секцией: они решают, что вообще попадёт в списки ниже.
              Группами, а не расширениями: расширений три десятка, и списком они не читаются */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            <SectionTitle note="что продукт умеет открыть">Типы файлов</SectionTitle>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, padding: `0 ${SP.sm}px` }}>
              {info.typeGroups.map(g => {
                const on = types.includes(g.key);
                return (
                  <button
                    key={g.key}
                    onClick={() => toggleIn(setTypes, g.key)}
                    // Расширения — в подсказке: в чипе они не помещаются, а знать их надо
                    title={`${g.extensions.join(' ')}${g.text ? '' : ' — откроется в центре, без превью и поиска по тексту'}`}
                    style={{
                      padding: `4px ${SP.sm}px`, borderRadius: R.max, cursor: 'pointer',
                      border: `1px solid ${on ? C.accent : C.border}`,
                      background: on ? C.accentMuted : 'transparent',
                      color: on ? C.accent : C.textSecondary,
                      fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: on ? 600 : 400,
                    }}
                  >
                    {g.title}
                  </button>
                );
              })}
            </div>
            {/* Честно про цену выбора: у файлов без текста корпуса не будет */}
            {types.some(k => info.typeGroups.find(g => g.key === k)?.text === false) && (
              <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `0 ${SP.sm}px` }}>
                Файлы без текста попадут в список, но откроются только в центре — заголовков,
                ссылок и поиска по содержимому у них нет.
              </div>
            )}
          </div>

          {/* Файлы корня — поимённо: папкой корень не выбирается, там же лежит код */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
            <SectionTitle note="выбираются по одному">Файлы в корне</SectionTitle>
            <div style={{ maxHeight: 140, overflowY: 'auto', margin: `0 -${SP.xs}px` }}>
              {info.rootFileCandidates.length === 0 && (
                <div style={{ fontSize: FS.sm, color: C.textMuted, padding: `${SP.xs}px ${SP.sm}px` }}>
                  В корне нет подходящих файлов
                </div>
              )}
              {info.rootFileCandidates.map(f => (
                <ScopeRow
                  key={f.name}
                  label={f.name}
                  hint={f.exists ? undefined : 'нет файла'}
                  on={rootFiles.includes(f.name)}
                  onClick={() => toggleIn(setRootFiles, f.name)}
                />
              ))}
            </div>
          </div>

          {/* Папки — берутся рекурсивно */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
            <SectionTitle note="со всеми вложенными">Папки</SectionTitle>
            <div style={{ maxHeight: 190, overflowY: 'auto', margin: `0 -${SP.xs}px` }}>
              {folderRows.length === 0 && (
                <div style={{ fontSize: FS.sm, color: C.textMuted, padding: `${SP.xs}px ${SP.sm}px` }}>
                  Папок с документами не нашлось. Впишите путь вручную ниже.
                </div>
              )}
              {folderRows.map(row => {
                const parent = parentOf(row.path, folders);
                return (
                  <ScopeRow
                    key={row.path}
                    label={row.path}
                    hint={parent ? `внутри ${parent}` : row.exists ? `${row.count}` : 'нет папки'}
                    on={folders.includes(row.path) || parent != null}
                    muted={parent != null}
                    title={parent ? `Уже входит в «${parent}» — папка берётся со всем содержимым` : undefined}
                    onClick={() => { if (!parent) toggleFolder(row.path); }}
                  />
                );
              })}
            </div>

            {/* Ручной ввод: папки без документов в кандидаты не попадают, а завести область
                заранее (под будущие доки) — нормальное желание */}
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, paddingTop: SP.xxs }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <TextField
                  value={manual}
                  onChange={setManual}
                  placeholder="Своя папка, например wiki/specs"
                  onEnter={addManual}
                />
              </div>
              <IconButton title="Добавить папку" onClick={addManual} disabled={!manual.trim()}>
                <FolderPlus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              </IconButton>
            </div>
          </div>

          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
            <Button variant="ghost" size="sm" onClick={toDefaults}>
              По умолчанию
            </Button>
            <Button variant="ghost" size="sm" onClick={() => { setFolders([]); setRootFiles([]); }}>
              Снять всё
            </Button>
          </div>
        </>
      )}
    </Modal>
  );
}
