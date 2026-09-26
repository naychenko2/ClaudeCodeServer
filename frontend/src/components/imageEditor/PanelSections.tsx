// Секции левой панели редактора v2 (макет image-editor-v2): «Образцы» с ролями,
// «Быстрые действия» и «История шагов». Логика входа задачи — в editorInputs.ts.

import { useEffect, useMemo, useRef, useState, type DragEvent } from 'react';
import { ChevronDown, ChevronUp, Expand, FolderOpen, Image as ImageIcon, Layers, Plus, Scissors, Search, Sparkles, Upload, X } from 'lucide-react';
import { Button, EmptyState, IconButton, IconField, Menu, MenuItem, Modal, ModalActions, SegmentedControl } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, FS, R, SP } from '../../lib/design';
import { api as appApi } from '../../lib/api';
import type { ReferenceRole } from '../../api/imageEditor';
import { SectionHint } from './EditorSections';
import {
  isImagePath, OUTPAINT_RATIOS, QUICK_LABEL, roleShort, SAMPLE_ROLES, stepLabel,
  type History, type OutpaintRatio, type QuickAction, type Sample,
} from './editorInputs';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

const MAX_FILE_MB = 20;

// ── Образцы ──

export function SamplesSection({ samples, max, disabled, onAddFiles, onAddPaths, onRole, onRemove, onPickProject }: {
  samples: Sample[];
  max: number;
  disabled: boolean;
  onAddFiles: (files: File[]) => void;
  onAddPaths: (paths: string[]) => void;
  onRole: (id: string, role: ReferenceRole) => void;
  onRemove: (id: string) => void;
  onPickProject: () => void;
}) {
  const [menu, setMenu] = useState(false);
  const [roleOf, setRoleOf] = useState<string | null>(null);
  const [over, setOver] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const editing = samples.find(s => s.id === roleOf) ?? null;

  // Перетаскивают картинки с компьютера или строку из дерева файлов (путь в text/plain)
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setOver(false);
    if (disabled) return;
    const files = [...e.dataTransfer.files].filter(f => f.type.startsWith('image/'));
    if (files.length) { onAddFiles(files); return; }
    const path = e.dataTransfer.getData('text/plain').trim();
    if (path && isImagePath(path)) onAddPaths([path]);
  };

  return (
    <div data-samples="true" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}
      onDragOver={e => { if (!disabled) { e.preventDefault(); setOver(true); } }}
      onDragLeave={() => setOver(false)} onDrop={onDrop}>
      <div style={{
        display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: `${SP.md}px ${SP.sm}px`, position: 'relative',
        borderRadius: R.lg, outline: over ? `2px dashed ${C.accent}` : 'none', outlineOffset: 2,
      }}>
        {samples.map(s => (
          <div key={s.id} data-sample={s.name} style={{ position: 'relative', minWidth: 0 }}>
            <div style={{ aspectRatio: '1', borderRadius: R.lg, overflow: 'hidden', border: `1px solid ${C.border}`, background: C.bgInset }}>
              {s.url && <img src={s.url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />}
            </div>
            <Button size="xs" fullWidth variant={s.role === 'character' ? 'ghostAccent' : 'secondary'} disabled={disabled}
              title="Роль образца" onClick={() => setRoleOf(roleOf === s.id ? null : s.id)}
              style={{ marginTop: SP.xxs, justifyContent: 'space-between', paddingLeft: SP.xs, paddingRight: SP.xxs }}>
              {roleShort(s.role)}{ic(roleOf === s.id ? ChevronUp : ChevronDown, 12)}
            </Button>
            <div title={s.name} style={{ fontSize: FS.xs, color: C.textSecondary, marginTop: 2, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
              {s.name}
            </div>
            <span style={{
              position: 'absolute', top: -SP.xs, right: -SP.xs, display: 'inline-flex', borderRadius: R.full,
              background: C.bgWhite, border: `1px solid ${C.border}`,
            }}>
              <IconButton size="xs" title="Убрать образец" ariaLabel="Убрать образец" disabled={disabled}
                onClick={() => { if (roleOf === s.id) setRoleOf(null); onRemove(s.id); }}>
                {ic(X, 12)}
              </IconButton>
            </span>
          </div>
        ))}
        {samples.length < max && (
          <Button variant="dashed" size="sm" disabled={disabled} onClick={() => setMenu(v => !v)}
            style={{ aspectRatio: '1', height: 'auto', width: '100%', flexDirection: 'column', gap: SP.xxs, padding: SP.xs }}>
            {ic(Plus, ICON_SIZE.sm)}Образец
          </Button>
        )}
        {menu && (
          <Menu onClose={() => setMenu(false)} align="left" top={0} minWidth={230}>
            <MenuItem icon={ic(Upload, ICON_SIZE.sm)} onClick={() => { setMenu(false); input.current?.click(); }}
              label={<SampleMenuLabel title="С компьютера" hint={`PNG, JPG или WebP до ${MAX_FILE_MB} МБ`} />} />
            <MenuItem icon={ic(FolderOpen, ICON_SIZE.sm)} onClick={() => { setMenu(false); onPickProject(); }}
              label={<SampleMenuLabel title="Из файлов проекта…" hint="Любая картинка этого проекта" />} />
          </Menu>
        )}
      </div>
      {editing && (
        <div data-role-picker="true" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs, padding: SP.xs, borderRadius: R.lg, border: `1px solid ${C.border}`, background: C.bgWhite }}>
          <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `0 ${SP.xs}px`, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            Как модели использовать «{editing.name}»
          </div>
          {SAMPLE_ROLES.map(([role, full]) => (
            <Button key={role} size="sm" fullWidth variant={editing.role === role ? 'ghostAccent' : 'ghost'}
              onClick={() => { onRole(editing.id, role); setRoleOf(null); }} style={{ justifyContent: 'flex-start' }}>
              {full}
            </Button>
          ))}
        </div>
      )}
      <SectionHint>Модель возьмёт образец за пример. Роль — под миниатюрой: лицо, стиль или предмет. Картинку можно перетащить сюда из файлов.</SectionHint>
      <input ref={input} type="file" accept="image/png,image/jpeg,image/webp" multiple hidden
        onChange={e => {
          const files = [...(e.target.files ?? [])];
          e.target.value = '';
          if (files.length) onAddFiles(files);
        }} />
    </div>
  );
}

function SampleMenuLabel({ title, hint }: { title: string; hint: string }) {
  return (
    <span style={{ display: 'flex', flexDirection: 'column' }}>
      <span>{title}</span>
      <span style={{ fontSize: FS.xs, color: C.textMuted }}>{hint}</span>
    </span>
  );
}

// «Из файлов проекта…»: все картинки проекта сеткой, поиск по имени
export function ProjectImagePicker({ projectId, taken, onPick, onClose }: {
  projectId: string;
  // Уже добавленные пути — повторно не предлагаем
  taken: string[];
  onPick: (path: string) => void;
  onClose: () => void;
}) {
  const [files, setFiles] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [q, setQ] = useState('');
  const [sel, setSel] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    appApi.files.tree(projectId, '')
      .then(entries => { if (alive) setFiles(entries.filter(e => !e.isDirectory && isImagePath(e.path)).map(e => e.path)); })
      .catch((e: Error) => { if (alive) setError(e.message); });
    return () => { alive = false; };
  }, [projectId]);

  const shown = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (files ?? []).filter(p => !taken.includes(p) && (!needle || p.toLowerCase().includes(needle)));
  }, [files, q, taken]);

  return (
    <Modal title="Образец из файлов проекта" width={560} onClose={onClose}
      footer={<ModalActions confirmLabel="Добавить образец" confirmDisabled={!sel} onCancel={onClose} onConfirm={() => { if (sel) onPick(sel); }} />}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        <IconField icon={ic(Search, ICON_SIZE.sm)} value={q} onChange={setQ} placeholder="Поиск по имени" height={38} radius={R.lg} fontSize={14} />
        {error && <div style={{ fontSize: FS.sm, color: C.dangerText }}>{error}</div>}
        {!files && !error && <div style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаем картинки проекта…</div>}
        {files && !shown.length && (
          <EmptyState compact inline icon={ic(ImageIcon, ICON_SIZE.lg)} title={files.length ? 'Ничего не нашлось' : 'В проекте нет картинок'}
            subtitle={files.length ? 'Попробуйте другое имя' : 'Добавьте образец с компьютера'} />
        )}
        {shown.length > 0 && (
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(112px, 1fr))', gap: SP.sm, maxHeight: 360, overflow: 'auto' }}>
            {shown.map(p => (
              <Button key={p} variant={sel === p ? 'ghostAccent' : 'ghost'} size="sm" title={p} onClick={() => setSel(p)}
                style={{ flexDirection: 'column', alignItems: 'stretch', height: 'auto', padding: SP.xs, gap: SP.xxs, minWidth: 0 }}>
                <span style={{ display: 'block', aspectRatio: '1', borderRadius: R.md, overflow: 'hidden', background: C.bgInset }}>
                  <img src={appApi.files.fileUrl(projectId, p)} alt="" loading="lazy" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
                </span>
                <span style={{ fontSize: FS.xs, fontWeight: 400, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{p.split('/').pop()}</span>
              </Button>
            ))}
          </div>
        )}
      </div>
    </Modal>
  );
}

// ── Быстрые действия ──

const QUICK_ICON: Record<QuickAction, typeof X> = {
  removeBackground: Layers, upscale: Sparkles, removeMarked: Scissors, outpaint: Expand,
};

export function QuickActions({ blockReason, ratio, onRatio, onRun }: {
  // Пусто — действие доступно
  blockReason: (a: QuickAction) => string;
  ratio: OutpaintRatio;
  onRatio: (r: OutpaintRatio) => void;
  onRun: (a: QuickAction) => void;
}) {
  const [outpaint, setOutpaint] = useState(false);
  const actions: QuickAction[] = ['removeBackground', 'upscale', 'removeMarked', 'outpaint'];
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
        {actions.map(a => {
          const why = blockReason(a);
          const on = a === 'outpaint' && outpaint && !why;
          return (
            <Button key={a} size="sm" pill variant={on ? 'ghostAccent' : 'ghostFilled'} disabled={!!why} title={why || undefined}
              leftIcon={ic(QUICK_ICON[a])} onClick={() => { if (a === 'outpaint') setOutpaint(v => !v); else { setOutpaint(false); onRun(a); } }}>
              {QUICK_LABEL[a]}
            </Button>
          );
        })}
      </div>
      {outpaint && !blockReason('outpaint') && (
        <div data-outpaint="true" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', padding: SP.sm, borderRadius: R.lg, border: `1px solid ${C.border}`, background: C.bgWhite }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>Пропорции</span>
          <div style={{ flex: 1, minWidth: 150 }}>
            <SegmentedControl value={ratio} onChange={v => onRatio(v)} options={OUTPAINT_RATIOS.map(r => ({ value: r, label: r }))} />
          </div>
          <Button size="sm" variant="primary" onClick={() => { setOutpaint(false); onRun('outpaint'); }}>Дорисовать</Button>
        </div>
      )}
      <SectionHint>Запускаются сразу, без промпта. Число вариантов и цена — как в поле промпта.</SectionHint>
    </div>
  );
}

// ── История шагов ──

export function HistorySteps({ history, disabled, onStep }: {
  history: History;
  disabled: boolean;
  onStep: (i: number) => void;
}) {
  const { steps, cur } = history;
  const hint = steps.length <= 1
    ? 'Здесь появятся шаги правки. На любой можно вернуться.'
    : cur < steps.length - 1
      ? `Вы на шаге «${stepLabel(history, cur)}». Следующая правка заменит шаги после него.`
      : 'Нажмите на шаг, чтобы вернуться к нему.';
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {steps.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          {steps.map((s, i) => (
            <Button key={s.id} size="sm" fullWidth variant={i === cur ? 'ghostAccent' : 'ghost'} disabled={disabled}
              onClick={() => onStep(i)} title={s.title}
              style={{
                justifyContent: 'flex-start', height: 'auto', padding: SP.xxs, gap: SP.sm, fontWeight: 400,
                border: `1px solid ${i === cur ? C.accent : 'transparent'}`, opacity: i > cur ? 0.55 : 1,
              }}>
              <span style={{ width: 64, flex: '0 0 64px', aspectRatio: '4 / 3', borderRadius: R.sm, overflow: 'hidden', border: `1px solid ${C.border}`, background: C.bgInset }}>
                <img src={s.src} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }} />
              </span>
              <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0, textAlign: 'left' }}>
                <span style={{ fontSize: FS.xs, color: C.textMuted }}>{stepLabel(history, i)}</span>
                {!s.original && (
                  <span style={{ fontSize: FS.sm, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{s.title}</span>
                )}
              </span>
            </Button>
          ))}
        </div>
      )}
      <SectionHint>{hint}</SectionHint>
    </div>
  );
}
