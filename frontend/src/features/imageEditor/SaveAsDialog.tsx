// Диалог «Сохранить как…» (ADR-018 §5, макет image-editor-v2): имя, папка проекта,
// расширение справа по формату результата. Имя проверяется на лету через save/check;
// перезаписи нет — занятое имя блокирует кнопку и предлагает свободное рядом.
// Тяжёлый файл (больше HeavyFileMb) — подсказка с кнопкой «Сжать в WebP» (ADR-018 §9).

import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { AlertTriangle, Folder } from 'lucide-react';
import { Button, Field, Modal, ModalActions, TextField, ICON_SIZE, ICON_STROKE, C, FS, R, SP, api as appApi } from 'aihome_shell/kit';
import { nameTakenSuggestion, type ImageEncodeFormat, type SaveCheckResponse } from './api';
import { plural } from './format';
import { checkKey, FORMAT_EXT, nameStem, projectFolders, saveBlocked, suggestionStem, type SaveCheckState, type SaveFolder } from './saveAs';
import { formatBytes } from './transforms';

export interface HeavyFile {
  bytes: number;
  // Оценка веса в WebP; null — ещё считается
  webpBytes: number | null;
  compress: boolean;
  onCompress: (on: boolean) => void;
}

const FORMAT_LABEL: Record<ImageEncodeFormat, string> = { png: 'PNG', jpeg: 'JPEG', webp: 'WebP' };

const folderLabel = (f: string) => (f ? `${f}/` : 'корень проекта');
const pathOf = (folder: string, name: string) => (folder ? `${folder}/${name}` : name);

export function SaveAsDialog({ projectId, sourcePath, defaultName, folder, format, onCheck, onSave, onClose, heavy }: {
  projectId: string;
  // Исходный файл в проекте; null — картинка нарисована с нуля
  sourcePath: string | null;
  // Имя без расширения: hero.v2
  defaultName: string;
  // Папка, где картинка сейчас («здесь сейчас»)
  folder: string;
  format: ImageEncodeFormat;
  onCheck: (folder: string, name: string) => Promise<SaveCheckResponse>;
  onSave: (v: { folder: string; fileName: string }) => Promise<void>;
  onClose: () => void;
  heavy?: HeavyFile | null;
}) {
  const [name, setName] = useState(defaultName);
  const [dir, setDir] = useState(folder);
  const [folders, setFolders] = useState<SaveFolder[] | null>(null);
  const [check, setCheck] = useState<SaveCheckState | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    appApi.files.tree(projectId, '')
      .then(entries => { if (alive) setFolders(projectFolders(entries)); })
      .catch(() => { if (alive) setFolders([]); });
    return () => { alive = false; };
  }, [projectId]);

  // Текущая папка есть в списке, даже если дерево ещё не пришло или не отдало её
  const shown = useMemo(() => {
    const list = folders ?? [];
    return list.some(f => f.path === folder) ? list : [{ path: folder, files: 0 }, ...list];
  }, [folders, folder]);

  const stem = nameStem(name);
  const key = checkKey(dir, stem, format);
  const ext = FORMAT_EXT[format];

  // Проверка на лету с дебаунсом 300 мс; окончательное решение всё равно за сервером (CreateNew)
  useEffect(() => {
    if (!stem) return;
    let alive = true;
    const t = setTimeout(() => {
      onCheck(dir, stem)
        .then(result => { if (alive) setCheck({ key, result, error: null }); })
        .catch((e: Error) => { if (alive) setCheck({ key, result: null, error: e.message }); });
    }, 300);
    return () => { alive = false; clearTimeout(t); };
  }, [onCheck, dir, stem, key]);

  const blocked = saveBlocked(stem, key, check);
  const current = check?.key === key ? check : null;

  const submit = async () => {
    if (blocked || busy) return;
    setBusy(true);
    setError(null);
    try {
      await onSave({ folder: dir, fileName: stem });
    } catch (e) {
      // Имя заняли между проверкой и записью: показываем то же предупреждение
      const suggestion = nameTakenSuggestion(e);
      if (suggestion) setCheck({ key, result: { path: pathOf(dir, `${stem}.${ext}`), taken: true, suggestion }, error: null });
      else setError((e as Error).message);
      setBusy(false);
    }
  };

  let status;
  if (!stem) status = <Hint>Введите имя файла.</Hint>;
  else if (!current) status = <Hint>Проверяем имя…</Hint>;
  else if (current.error) status = <div style={{ fontSize: FS.sm, color: C.dangerText }}>{current.error}</div>;
  else if (current.result?.taken) {
    const free = current.result.suggestion;
    status = (
      <div data-save-warn="true" style={{
        display: 'flex', flexDirection: 'column', gap: SP.xs, fontSize: FS.sm, lineHeight: 1.45,
        color: C.warningText, background: C.warningBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`,
      }}>
        <span style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', fontWeight: 600 }}>
          <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 2 }} />
          Такой файл уже есть: {current.result.path}
        </span>
        {free ? (
          <>
            <span>Перезаписать нельзя — возьмите другое имя. Свободное рядом: <b>{free}</b></span>
            <span>
              <Button size="sm" variant="secondary" onClick={() => setName(suggestionStem(free))}>
                Взять «{free.split('/').pop()}»
              </Button>
            </span>
          </>
        ) : <span>Перезаписать нельзя — возьмите другое имя.</span>}
      </div>
    );
  } else if (current.result) status = <Hint>Файл ляжет сюда: <b>{current.result.path}</b></Hint>;

  return (
    <Modal title="Сохранить как…" width={480} onClose={onClose}
      footer={<ModalActions confirmLabel="Сохранить" onConfirm={() => { void submit(); }} loading={busy}
        confirmDisabled={blocked} onCancel={onClose} />}>
      <div data-save-as="true" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <Field label="Имя файла" hint={`Расширение подставляется по формату результата — сейчас ${FORMAT_LABEL[format]}.`}>
          <div style={{ display: 'flex', alignItems: 'stretch' }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <TextField value={name} onChange={setName} autoFocus onEnter={() => { void submit(); }}
                style={{ borderTopRightRadius: 0, borderBottomRightRadius: 0, width: '100%' }} />
            </div>
            <span title="Расширение — по формату результата" style={{
              display: 'inline-flex', alignItems: 'center', padding: `0 ${SP.sm}px`, fontSize: FS.sm, color: C.textMuted,
              background: C.bgInset, border: `1px solid ${C.border}`, borderLeft: 'none', borderRadius: `0 ${R.lg}px ${R.lg}px 0`,
            }}>.{ext}</span>
          </div>
        </Field>
        <Field label="Папка проекта">
          <div style={{
            display: 'flex', flexDirection: 'column', gap: 2, padding: SP.xxs, maxHeight: 220, overflow: 'auto',
            border: `1px solid ${C.border}`, borderRadius: R.lg, background: C.bgWhite,
          }}>
            {shown.map(f => (
              <Button key={f.path} size="sm" fullWidth variant={f.path === dir ? 'ghostAccent' : 'ghost'}
                title={folderLabel(f.path)} onClick={() => setDir(f.path)}
                leftIcon={<Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
                style={{ justifyContent: 'flex-start', fontWeight: f.path === dir ? 600 : 400, flexShrink: 0 }}>
                <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{folderLabel(f.path)}</span>
                <span style={{ marginLeft: 'auto', paddingLeft: SP.sm, fontSize: FS.xs, fontWeight: 400, color: C.textMuted, whiteSpace: 'nowrap' }}>
                  {f.path === folder ? 'здесь сейчас · ' : ''}{f.files} {plural(f.files, 'файл', 'файла', 'файлов')}
                </span>
              </Button>
            ))}
            {!folders && <Hint>Загружаем папки проекта…</Hint>}
          </div>
        </Field>
        {status}
        {sourcePath && (
          <div style={{ fontSize: FS.sm, color: C.successText, background: C.successBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px` }}>
            Исходный {sourcePath} не изменится. Редактор перейдёт на новый файл.
          </div>
        )}
        {heavy && (
          <div data-heavy="true" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', fontSize: FS.sm, color: C.warningText, background: C.warningBg, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px` }}>
            <span style={{ flex: 1, minWidth: 180 }}>
              {heavy.compress
                ? `Сохраним в WebP${heavy.webpBytes != null ? ` ≈ ${formatBytes(heavy.webpBytes)}` : ''} вместо ${formatBytes(heavy.bytes)}.`
                : `Файл ${formatBytes(heavy.bytes)} — тяжёлый. Сжать в WebP${heavy.webpBytes != null ? ` ≈ ${formatBytes(heavy.webpBytes)}` : ''}?`}
            </span>
            <Button size="sm" variant="secondary" onClick={() => heavy.onCompress(!heavy.compress)}>
              {heavy.compress ? 'Оставить как есть' : 'Сжать в WebP'}
            </Button>
          </div>
        )}
        {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
      </div>
    </Modal>
  );
}

function Hint({ children }: { children: ReactNode }) {
  return <div style={{ fontSize: FS.sm, color: C.textMuted }}>{children}</div>;
}
