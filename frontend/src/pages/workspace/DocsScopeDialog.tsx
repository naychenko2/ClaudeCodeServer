// Настройка области панели «Документы»: какие папки проекта считать документацией.
//
// Дефолт — docs/, но соглашение о папке в проектах разное (wiki, documentation, doc,
// спеки рядом с кодом), поэтому область настраивается. README.md в корне входит всегда:
// это не папка, и отключать его нечем.
//
// Кандидатов считает бэкенд (папки с .md неглубоко от корня, без node_modules и скрытых).
// Ручной ввод оставлен для папки, которой в списке нет: пустой пока или лежащей глубже.

import { useEffect, useState } from 'react';
import { Check, FolderPlus } from 'lucide-react';
import type { DocsFoldersInfo } from '../../types';
import { api } from '../../lib/api';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, IconButton, Modal, ModalActions, TextField } from '../../components/ui';

interface Props {
  projectId: string;
  onClose: () => void;
  // Сохранённая область — панель перечитывает по ней индекс
  onSaved: (info: DocsFoldersInfo) => void;
}

export function DocsScopeDialog({ projectId, onClose, onSaved }: Props) {
  const [info, setInfo] = useState<DocsFoldersInfo | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  // Папки, добавленные вручную в этом заходе: их нет среди кандидатов, но показать надо
  const [extra, setExtra] = useState<string[]>([]);
  const [manual, setManual] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    api.docs.folders(projectId)
      .then(f => { if (alive) { setInfo(f); setSelected(f.selected); } })
      .catch(() => { if (alive) setError('Не удалось загрузить список папок'); });
    return () => { alive = false; };
  }, [projectId]);

  // Выбор папки включает ВСЁ её поддерево (бэкенд обходит рекурсивно), поэтому вложенные
  // строки не отдельные галки, а «уже внутри». Иначе диалог врал бы: docs отмечена,
  // docs/adr нет — а документы из неё в области.
  const parentOf = (path: string, list: string[]) =>
    list.find(s => path.toLowerCase().startsWith(`${s.toLowerCase()}/`));

  const toggle = (path: string) =>
    setSelected(prev => prev.includes(path)
      ? prev.filter(p => p !== path)
      // Вложенные снимаем: их и так покрывает родитель, а в сторе они были бы шумом
      : [...prev.filter(p => !p.toLowerCase().startsWith(`${path.toLowerCase()}/`)), path]);

  // Ручная папка сразу отмечается: её вводят, чтобы включить, а не чтобы посмотреть
  const addManual = () => {
    const path = manual.trim().replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    if (!path) return;
    const known = (info?.candidates ?? []).some(c => c.path.toLowerCase() === path.toLowerCase())
      || extra.some(e => e.toLowerCase() === path.toLowerCase());
    if (!known) setExtra(prev => [...prev, path]);
    if (!selected.some(s => s.toLowerCase() === path.toLowerCase())) setSelected(prev => [...prev, path]);
    setManual('');
  };

  const save = () => {
    setSaving(true);
    api.docs.setFolders(projectId, selected)
      .then(saved => { onSaved(saved); onClose(); })
      .catch(() => { setSaving(false); setError('Не удалось сохранить'); });
  };

  const rows = [
    ...(info?.candidates ?? []).map(c => ({ path: c.path, count: c.count, exists: c.exists })),
    ...extra.map(path => ({ path, count: 0, exists: false })),
  ];

  return (
    <Modal
      width={460}
      title="Папки документации"
      subtitle="Что панель «Документы» считает документацией проекта. Папка берётся со всем, что внутри"
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
          {/* README не папка и настройкой не отключается — говорим об этом прямо,
              иначе его присутствие в списке документов выглядит как игнор настройки */}
          <div style={{
            display: 'flex', alignItems: 'center', gap: SP.sm,
            padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg,
            background: C.bgInset, fontSize: FS.sm, color: C.textSecondary,
          }}>
            <Check size={ICON_SIZE.xs} strokeWidth={2.5} style={{ color: C.textMuted, flexShrink: 0 }} />
            <span><b style={{ fontWeight: 600 }}>README.md</b> в корне — всегда в документации</span>
          </div>

          <div style={{ maxHeight: 280, overflowY: 'auto', margin: `0 -${SP.xs}px` }}>
            {rows.length === 0 && (
              <div style={{ fontSize: FS.sm, color: C.textMuted, padding: `${SP.sm}px ${SP.xs}px` }}>
                В проекте не нашлось папок с markdown-файлами. Впишите путь вручную ниже.
              </div>
            )}
            {rows.map(row => {
              const parent = parentOf(row.path, selected);
              const on = selected.includes(row.path) || parent != null;
              return (
                <button
                  key={row.path}
                  onClick={() => { if (!parent) toggle(row.path); }}
                  title={parent ? `Уже входит в «${parent}» — папка берётся со всем содержимым` : undefined}
                  style={{
                    display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
                    padding: `${SP.xs}px ${SP.sm}px`, border: 'none', background: 'transparent',
                    borderRadius: R.md, cursor: parent ? 'default' : 'pointer', textAlign: 'left',
                    fontFamily: FONT.sans, fontSize: FS.sm,
                    // Наследованная строка глушится целиком: снять её нельзя, и активный вид
                    // обещал бы управление, которого нет
                    opacity: parent ? 0.55 : 1,
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
                    flex: 1, minWidth: 0, fontFamily: FONT.mono, fontSize: FS.xs,
                    color: row.exists ? C.textPrimary : C.textMuted,
                    overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                  }}>
                    {row.path}
                  </span>
                  <span style={{ flexShrink: 0, fontSize: FS.xs, color: C.textMuted }}>
                    {parent ? `внутри ${parent}` : row.exists ? `${row.count} md` : 'нет папки'}
                  </span>
                </button>
              );
            })}
          </div>

          {/* Ручной ввод: папки без .md в кандидаты не попадают, а завести область
              заранее (под будущие доки) — нормальное желание */}
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
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

          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
            <Button variant="ghost" size="sm" onClick={() => setSelected(info.defaults)}>
              По умолчанию
            </Button>
            <Button variant="ghost" size="sm" onClick={() => setSelected([])}>
              Снять всё
            </Button>
          </div>
        </>
      )}
    </Modal>
  );
}
