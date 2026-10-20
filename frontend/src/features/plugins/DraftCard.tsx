// Карточка черновика плагина build-it-yourself (макет 4, ADR-031, шаг 4.4).
// Агент собрал плагин в чате; человек одним взглядом видит, что изменится после
// установки, смотрит файлы и решает — принять, отклонить или отдать агенту на доработку.
//
// «Принять…» открывает существующий ConsentDialog (не новое окно). Загрузку данных
// (preview/diff) ведёт обёртка useDraftCard; сам DraftCard «тупой» — данные и
// действия приходят пропсами, поэтому его рендерят в строку в тестах.

import { useEffect, useRef, useSyncExternalStore, useState } from 'react';
import {
  AlertTriangle, File as FileIcon, Folder as FolderIcon, Globe, Key, Pencil,
  RotateCcw, Sparkles, X,
} from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { Badge, Button, Modal, Notice } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { getEffectiveTheme, subscribeThemeMode } from '../../lib/themeMode';
import { onMessage } from '../../lib/signalr';
import { api } from '../../lib/api';
import { isNetPermission, permissionLabel, NET_PERMISSION_WARNING } from './permissionLabels';
import {
  acceptDisabledReason, buildFixReply, diffFileLabel, draftContributions, isRejectedExpired, problemCount,
  type DraftContribution,
} from './draftModel';
import { buildInitMessage, buildPanelSrcdoc, validateFrameMessage, panelWindowOf, MAX_PANEL_FILE_BYTES } from './bridge';
import type {
  PluginDraftDiff, PluginDraftDiffFile, PluginDraftDiffLine, PluginDraftPreview, PluginDraftSummary,
} from '../../lib/api';

// === Плитка черновика: sparkle на plan-гамме (макет 4, «черновик» — не accent) ===
function DraftTile() {
  return (
    <div style={{
      width: 36, height: 36, borderRadius: R.lg, flexShrink: 0,
      background: C.planLight, color: C.planText,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
    }}>
      <Sparkles size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
    </div>
  );
}

// Иконка вклада: панель/команда/навык
function ContributionIcon({ kind }: { kind: DraftContribution['kind'] }) {
  const I = kind === 'panel' ? Sparkles : kind === 'command' ? Pencil : FileIcon;
  return <I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0, marginTop: 1 }} />;
}

// Строка вклада «Что будет после установки»: иконка, название, тип (Badge neutral)
function ContributionRow({ c }: { c: DraftContribution }) {
  const typeLabel = c.kind === 'panel' ? 'panel' : c.kind === 'command' ? 'command' : 'skill';
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: '6px 0' }}>
      <ContributionIcon kind={c.kind} />
      <span style={{ flex: 1, minWidth: 0, fontWeight: 600, fontSize: FS.base, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {c.label}
      </span>
      <Badge tone="neutral" size="xs">{typeLabel}</Badge>
    </div>
  );
}

// Подпись секции: FS.xs, uppercase, C.textMuted (как в ConsentDialog)
function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{
      fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase',
      color: C.textMuted, marginBottom: SP.sm,
    }}>
      {children}
    </div>
  );
}

// «Попросит права» (net:* предупреждающим цветом) и «Секреты» (обязательный — «не задан»)
function PermsAndSecrets({ preview, isMobile }: { preview: PluginDraftPreview; isMobile: boolean }) {
  const perms = preview.requestedPermissions;
  const secrets = preview.secrets;

  const permBlock = perms.length > 0 ? (
    <div>
      <SectionLabel>Попросит права</SectionLabel>
      <div style={{ display: 'flex', flexDirection: 'column' }}>
        {perms.map(key => {
          const label = permissionLabel(key);
          if (label === null) return null; // secret:* — в блоке секретов
          const net = isNetPermission(key);
          return (
            <div key={key} style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: '4px 0' }}>
              {net && <Globe size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.warning, flexShrink: 0 }} />}
              <span style={{ fontWeight: 600, fontSize: FS.sm, color: net ? C.warningText : C.textPrimary }}>{label}</span>
              <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{key}</span>
            </div>
          );
        })}
      </div>
      {perms.some(isNetPermission) && (
        <div style={{ fontSize: FS.xs, color: C.warningText, marginTop: SP.xs }}>{NET_PERMISSION_WARNING}</div>
      )}
    </div>
  ) : <div style={{ fontSize: FS.sm, color: C.textMuted }}>Права не запрашивает</div>;

  const secretBlock = secrets.length > 0 ? (
    <div>
      <SectionLabel>Секреты</SectionLabel>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        {secrets.map(s => (
          <div key={s.name} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: '4px 0' }}>
            <Key size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textSecondary, flexShrink: 0 }} />
            <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textPrimary, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {s.name}
            </span>
            {s.required && <Badge tone="warning" size="xs" dot>не задан</Badge>}
          </div>
        ))}
      </div>
    </div>
  ) : <div style={{ fontSize: FS.sm, color: C.textMuted }}>Секретов нет</div>;

  // Десктоп — две колонки, мобила — одна
  if (isMobile) return <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>{permBlock}{secretBlock}</div>;
  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: SP.lg }}>
      {permBlock}
      {secretBlock}
    </div>
  );
}

// Строка diff: номер + маркер + текст, цвет по kind (ctx/del/add)
function DiffLine({ line }: { line: PluginDraftDiffLine }) {
  const bg = line.kind === 'add' ? C.diffAddBg : line.kind === 'del' ? C.diffRemBg : 'transparent';
  const fg = line.kind === 'add' ? C.diffAddText : line.kind === 'del' ? C.diffRemText : C.textPrimary;
  const marker = line.kind === 'add' ? '+' : line.kind === 'del' ? '−' : ' ';
  const num = line.kind === 'del' ? line.oldLine : line.newLine;
  return (
    <div style={{
      display: 'flex', fontFamily: FONT.mono, fontSize: FS.xs, lineHeight: 1.5,
      background: bg, color: fg, whiteSpace: 'pre', overflowX: 'auto',
    }}>
      <span style={{ width: 34, flexShrink: 0, color: C.textMuted, textAlign: 'right', paddingRight: SP.xs, userSelect: 'none' }}>
        {num > 0 ? num : ''}
      </span>
      <span style={{ width: 12, flexShrink: 0, userSelect: 'none' }}>{marker}</span>
      <span style={{ paddingRight: SP.sm }}>{line.text || ' '}</span>
    </div>
  );
}

// Файл в diff: бинарный/без содержимого — «изменён, N байт», иначе построчный diff
function DiffFileBlock({ file }: { file: PluginDraftDiffFile }) {
  const label = diffFileLabel(file);
  return (
    <div style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.lg, overflow: 'hidden', marginBottom: SP.sm }}>
      <div style={{ padding: '6px 10px', background: C.bgInset, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary }}>
        {file.path}
      </div>
      {label
        ? <div style={{ padding: '6px 10px', fontSize: FS.xs, color: C.textMuted }}>{label}</div>
        : <div style={{ padding: SP.xs }}>{(file.lines ?? []).map((l, i) => <DiffLine key={i} line={l} />)}</div>}
    </div>
  );
}

// Предпросмотр панели черновика: iframe с шимом plugin-kit, без выданных прав
// (granted: [] — панель видит «нет права …» штатно). Перезагружается по событию
// plugin_draft_changed (агент правит черновик) и по кнопке «Перезагрузить».
function PanelPreview({ slug }: { slug: string }) {
  const theme = useSyncExternalStore(subscribeThemeMode, getEffectiveTheme, getEffectiveTheme);
  const [rev, setRev] = useState(0);
  const [html, setHtml] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [height, setHeight] = useState(240);
  const frameRef = useRef<HTMLIFrameElement>(null);

  // Забор panel/index.html черновика (REST, user JWT) + сброс состояния
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    api.plugins.draftPanel(slug)
      .then(h => { if (!cancelled) { setHtml(h); setLoading(false); } })
      .catch((e: unknown) => { if (!cancelled) { setError(e instanceof Error ? e.message : 'Не удалось загрузить панель'); setLoading(false); } });
    return () => { cancelled = true; };
  }, [slug, rev]);

  // srcdoc: оболочка (CSP + iframe) + внутренний документ (CSP + шим + html панели).
  // Панель больше потолка (256 КиБ) — не монтируем (показываем «слишком большая»).
  // Тема входит в ключ фрейма: смена темы перемонтирует iframe (простой предпросмотр —
  // без живого обновления темы сообщением, как в PluginFrame).
  // Размер — в БАЙТАХ (TextEncoder), не в символах: 100 000 русских символов
  // ≈ 200 КиБ байт, но .length = 100 000 (символы) — потолок 256 КиБ не срабатывал.
  const tooBig = html != null && new TextEncoder().encode(html).length > MAX_PANEL_FILE_BYTES;
  // theme — тема первого кадра (ADR-031 §3.3); здесь смена темы и так перемонтирует
  // (frameKey включает theme), поэтому тема идёт напрямую, без замыкания.
  const srcdoc = html != null && !tooBig ? buildPanelSrcdoc(html, theme) : null;
  const frameKey = `${slug}#${rev}#${theme}`;

  // Мост: ready → init (granted: [] — предпросмотр без прав), resize → высота.
  // Окно панели = frames[0] оболочки: e.source сверяется с ним, init уходит в него
  // (сообщения от самой оболочки и чужих окон отбрасываются, ADR-031 §3.6).
  useEffect(() => {
    const onMsg = (e: MessageEvent) => {
      const panelWindow = panelWindowOf(frameRef.current);
      const msg = validateFrameMessage(e, panelWindow);
      if (!msg) return;
      if (msg.type === 'ready') {
        const init = buildInitMessage({
          plugin: { id: slug, version: '' },
          slot: 'sidebar',
          context: { projectId: null },
          theme,
          locale: typeof navigator !== 'undefined' ? navigator.language : '',
          granted: [],
        });
        panelWindow?.postMessage(init, '*');
      } else if (msg.type === 'resize') {
        setHeight(Math.min(800, Math.max(48, Math.round(msg.height))));
      }
    };
    window.addEventListener('message', onMsg);
    return () => window.removeEventListener('message', onMsg);
  }, [frameKey, slug, theme]);

  // Агент правит черновик → перезагрузить панель
  useEffect(() => {
    const off = onMessage(msg => {
      if (msg.type === 'plugin_draft_changed' && msg.slug === slug) setRev(r => r + 1);
    });
    return off;
  }, [slug]);

  return (
    <div style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.lg, overflow: 'hidden', background: C.bgCard }}>
      <div style={{ padding: '6px 10px', background: C.bgInset, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, display: 'flex', alignItems: 'center', gap: SP.xs }}>
        <span style={{ flex: 1 }}>Предпросмотр панели · без выданных прав</span>
        <Button variant="ghost" size="xs" leftIcon={<RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => setRev(r => r + 1)}>
          Перезагрузить
        </Button>
      </div>
      {loading
        ? <div style={{ padding: SP.md, fontSize: FS.sm, color: C.textMuted }}>Загружаем панель…</div>
        : error
          ? <Notice tone="danger" icon={AlertTriangle} title="Не удалось загрузить панель">{error}</Notice>
          : tooBig
            ? <div style={{ padding: SP.md, fontSize: FS.sm, color: C.warningText }}>Панель слишком большая</div>
            : srcdoc
              ? <iframe key={frameKey} ref={frameRef} srcDoc={srcdoc} sandbox="allow-scripts" title="Предпросмотр панели черновика"
                  style={{ width: '100%', height, border: 'none', display: 'block', background: 'transparent' }} />
              : <div style={{ padding: SP.md, fontSize: FS.sm, color: C.textMuted }}>Панель не найдена</div>}
    </div>
  );
}

// Шторка «Файлы»: дерево + содержимое (только чтение) / вкладка «Изменения» (diff)
// + предпросмотр панели. На мобиле Modal сам становится полноэкранной шторкой.
function FilesDrawer({ preview, diff, onClose }: {
  preview: PluginDraftPreview;
  diff: PluginDraftDiff | null;
  onClose: () => void;
}) {
  const isMobile = useIsMobile();
  const [tab, setTab] = useState<'files' | 'changes'>('files');
  const [selected, setSelected] = useState<string | null>(null);
  const [panelOpen, setPanelOpen] = useState(false);

  const files = preview.files.filter(f => !f.isDir);
  const dirs = preview.files.filter(f => f.isDir);
  const selectedFile = files.find(f => f.path === selected) ?? null;
  const selectedDiff = diff?.modified.find(m => m.path === selected) ?? null;

  return (
    <Modal
      size="fullscreen"
      title="Файлы черновика"
      subtitle={`${preview.name} · ${preview.version}`}
      onClose={onClose}
      footer={
        <div style={{ display: 'flex', gap: SP.sm, width: '100%', flexDirection: isMobile ? 'column' : 'row', justifyContent: 'flex-end' }}>
          <Button variant="ghost" size="md" fullWidth={isMobile} leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => setPanelOpen(true)}>
            Предпросмотр панели
          </Button>
          <Button variant="ghost" size="md" fullWidth={isMobile} onClick={onClose}>Закрыть</Button>
        </div>
      }
    >
      <div style={{ display: 'flex', gap: SP.xs }}>
        <Button variant={tab === 'files' ? 'secondary' : 'ghost'} size="xs" onClick={() => setTab('files')}>Файлы</Button>
        <Button variant={tab === 'changes' ? 'secondary' : 'ghost'} size="xs" onClick={() => setTab('changes')}>Изменения</Button>
      </div>

      {tab === 'files' ? (
        <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: SP.md, minHeight: 0 }}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
            {dirs.map(d => (
              <div key={d.path} style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
                <FolderIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> {d.path}/
              </div>
            ))}
            {files.map(f => (
              <button
                key={f.path}
                onClick={() => setSelected(f.path)}
                style={{
                  display: 'flex', alignItems: 'center', gap: SP.xs, textAlign: 'left',
                  padding: '4px 6px', borderRadius: R.md, border: 'none', cursor: 'pointer',
                  background: selected === f.path ? C.bgSelected : 'transparent',
                  color: selected === f.path ? C.textHeading : C.textSecondary,
                  fontFamily: FONT.mono, fontSize: FS.xs,
                }}
              >
                <FileIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{f.path}</span>
              </button>
            ))}
            {files.length === 0 && <div style={{ fontSize: FS.sm, color: C.textMuted }}>Файлов нет</div>}
          </div>
          <div style={{ minWidth: 0, overflow: 'auto', maxHeight: 420, border: `1px solid ${C.borderLight}`, borderRadius: R.lg }}>
            {selectedFile ? (
              selectedDiff && selectedDiff.lines
                ? <div style={{ padding: SP.xs }}>{selectedDiff.lines.map((l, i) => <DiffLine key={i} line={l} />)}</div>
                : <div style={{ padding: SP.md, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
                    {selectedDiff ? diffFileLabel(selectedDiff) : `файл · ${selectedFile.size} байт`}
                  </div>
            ) : (
              <div style={{ padding: SP.md, fontSize: FS.sm, color: C.textMuted }}>Выберите файл, чтобы посмотреть содержимое</div>
            )}
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minHeight: 0, overflow: 'auto', maxHeight: 420 }}>
          {diff == null ? (
            <div style={{ fontSize: FS.sm, color: C.textMuted }}>Загрузка сравнения…</div>
          ) : diff.status === 'new' ? (
            <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
              Новый плагин: файлов {diff.added.length}. Установленной версии ещё нет.
            </div>
          ) : (
            <>
              <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
                {diff.installedVersion ? `Черновик ${diff.version} против установленной ${diff.installedVersion}` : `Черновик ${diff.version}`}
                {diff.truncated && ' · diff усечён'}
              </div>
              {diff.added.length > 0 && (
                <div>
                  <SectionLabel>Добавлено · {diff.added.length}</SectionLabel>
                  {diff.added.map(f => (
                    <div key={f.path} style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.successText, padding: '2px 0' }}>+ {f.path}</div>
                  ))}
                </div>
              )}
              {diff.removed.length > 0 && (
                <div>
                  <SectionLabel>Удалено · {diff.removed.length}</SectionLabel>
                  {diff.removed.map(f => (
                    <div key={f.path} style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.dangerText, padding: '2px 0' }}>− {f.path}</div>
                  ))}
                </div>
              )}
              {diff.modified.length > 0 && (
                <div>
                  <SectionLabel>Изменено · {diff.modified.length}</SectionLabel>
                  {diff.modified.map(f => <DiffFileBlock key={f.path} file={f} />)}
                </div>
              )}
              {diff.added.length === 0 && diff.removed.length === 0 && diff.modified.length === 0 && (
                <div style={{ fontSize: FS.sm, color: C.textMuted }}>Файлы не изменились</div>
              )}
              {diff.manifest && (diff.manifest.permissionsAdded.length > 0 || diff.manifest.permissionsRemoved.length > 0) && (
                <div>
                  <SectionLabel>Права</SectionLabel>
                  {diff.manifest.permissionsAdded.map(p => (
                    <div key={p} style={{ fontSize: FS.xs, color: C.successText, padding: '2px 0' }}>+ {p}</div>
                  ))}
                  {diff.manifest.permissionsRemoved.map(p => (
                    <div key={p} style={{ fontSize: FS.xs, color: C.dangerText, padding: '2px 0' }}>− {p}</div>
                  ))}
                </div>
              )}
            </>
          )}
        </div>
      )}

      {panelOpen && <PanelPreview slug={preview.slug} />}
    </Modal>
  );
}

// === Карточка черновика ===
export interface DraftCardProps {
  summary: PluginDraftSummary;
  // Предпросмотр (вклады/права/секреты/файлы/проверка). null — ещё грузится
  preview: PluginDraftPreview | null;
  // Сравнение с установленной версией (загружается, когда открыта шторка «Файлы»)
  diff: PluginDraftDiff | null;
  // «Черновик обновлён агентом»: подсветка после события plugin_draft_changed
  justUpdated?: boolean;
  // Загрузка предпросмотра
  loading?: boolean;
  // Ошибка сервера (403/404/409) — понятным текстом
  error?: string | null;
  // Ошибка действия («Принять…», «Вернуть»): сервер ответил 409/5xx или сбой сети —
  // понятным текстом; null — нет. Показывается, пока действие не повторят.
  actionError?: string | null;
  // Есть ли у этого id более новая версия (карточка «устарела»)
  staleNewerSlug?: string | null;
  // Действия
  onAccept?: () => void;
  onReject?: () => void;
  onRestore?: () => void;
  // «Попросить агента исправить» / «Править»: кладёт готовую реплику в поле ввода чата
  onEdit?: (reply: string) => void;
  // Загрузить diff (открытие вкладки «Изменения»)
  onLoadDiff?: () => void;
  // «Открыть в «Плагинах»» (свёрнутая установленная/отклонённая/устаревшая)
  onOpenInPlugins?: () => void;
}

export function DraftCard(props: DraftCardProps) {
  const {
    summary, preview, diff, justUpdated, loading, error, actionError, staleNewerSlug,
    onAccept, onReject, onRestore, onEdit, onLoadDiff, onOpenInPlugins,
  } = props;

  const [filesOpen, setFilesOpen] = useState(false);
  // Хук — до ранних return: иначе число хуков менялось бы между рендерами
  const isMobile = useIsMobile();

  // Свёрнутые состояния — строка, не полная карточка
  const rejected = summary.rejected;
  const installed = summary.installedSameVersion;
  const stale = staleNewerSlug != null;

  if (rejected || installed || stale) {
    return (
      <CollapsedRow
        rejected={rejected}
        installed={installed}
        stale={stale}
        name={preview?.name ?? summary.id}
        version={summary.version}
        expiresAt={summary.expiresAt}
        actionError={actionError}
        onRestore={onRestore}
        onOpenInPlugins={onOpenInPlugins}
      />
    );
  }

  // Загрузка предпросмотра
  if (loading && !preview) {
    return (
      <div style={cardShell(justUpdated)}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.md, padding: SP.md }}>
          <DraftTile />
          <div style={{ flex: 1 }}>
            <div style={{ fontWeight: 600, fontSize: FS.base, color: C.textHeading }}>{summary.id}</div>
            <div style={{ fontSize: FS.xs, color: C.textMuted }}>Загружаем предпросмотр…</div>
          </div>
        </div>
      </div>
    );
  }

  // Ошибка сервера (403/404/409)
  if (error && !preview) {
    return (
      <div style={cardShell(justUpdated)}>
        <Notice tone="danger" icon={AlertTriangle} title="Не удалось загрузить черновик">{error}</Notice>
      </div>
    );
  }

  if (!preview) return null;

  const problems = preview.validation.problems;
  const acceptBlocked = acceptDisabledReason(preview);
  const contributions = draftContributions(preview);

  return (
    <div style={cardShell(justUpdated)}>
      {/* Шапка: плитка + имя + бейдж «Черновик» + id/версия + статус проверки */}
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: SP.md }}>
        <DraftTile />
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
            <span style={{ fontWeight: 600, fontSize: FS.base, color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {preview.name}
            </span>
            <Badge tone="plan" size="xs">Черновик</Badge>
          </div>
          <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
            {summary.id} · {summary.version}
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
            Собран агентом · ещё не установлен · виден только вам
          </div>
        </div>
        {preview.validation.isValid
          ? <Badge tone="success" size="sm" dot style={{ flexShrink: 0 }}>Проверка пройдена</Badge>
          : <Badge tone="danger" size="sm" dot style={{ flexShrink: 0 }}>{problemCount(preview)} {problemCount(preview) === 1 ? 'ошибка' : 'ошибки'}</Badge>}
      </div>

      {/* «Что будет после установки» */}
      {contributions.length > 0 && (
        <div style={{ marginTop: SP.md }}>
          <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 500, color: C.textHeading, marginBottom: SP.xs }}>
            Что будет после установки
          </div>
          {contributions.map((c, i) => <ContributionRow key={i} c={c} />)}
        </div>
      )}

      {/* Права и секреты */}
      <div style={{ marginTop: SP.md }}>
        <PermsAndSecrets preview={preview} isMobile={isMobile} />
      </div>

      {/* Проверка не пройдена: список ошибок + «Принять пока нельзя» */}
      {!preview.validation.isValid && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title="Принять пока нельзя">
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              {problems.map((p, i) => (
                <div key={i} style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>{p.message} <span style={{ color: C.textMuted }}>({p.code})</span></div>
              ))}
            </div>
          </Notice>
        </div>
      )}

      {/* Ошибка действия («Принять…»): сервер ответил 409/5xx или сбой сети — текст сервера */}
      {actionError && (
        <div style={{ marginTop: SP.md }}>
          <Notice tone="danger" icon={AlertTriangle} title="Принять не вышло">{actionError}</Notice>
        </div>
      )}

      {/* «Черновик обновлён агентом» */}
      {justUpdated && (
        <div style={{ marginTop: SP.md }}>
          <Badge tone="plan" size="sm" dot>Черновик обновлён агентом</Badge>
        </div>
      )}

      {/* Футер: Посмотреть файлы / Править(Попросить агента) / Отклонить / Принять… */}
      <div style={{
        marginTop: SP.md, padding: SP.md, background: C.bgInset, borderRadius: R.lg,
        display: 'flex', gap: SP.sm, flexWrap: 'wrap', alignItems: 'center',
      }}>
        <Button variant="ghost" size="sm" leftIcon={<FileIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => { onLoadDiff?.(); setFilesOpen(true); }}>
          Посмотреть файлы
        </Button>
        {preview.validation.isValid
          ? <Button variant="ghost" size="sm" leftIcon={<Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => onEdit?.(`Поправь плагин «${preview.name}»: `)}>Править</Button>
          : <Button variant="secondary" size="sm" leftIcon={<Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => onEdit?.(buildFixReply(preview.name, problems))}>Попросить агента исправить</Button>}
        <div style={{ flex: 1 }} />
        <Button
          variant="ghost" size="sm"
          style={{ color: C.dangerText, borderColor: C.dangerBorder }}
          leftIcon={<X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => onReject?.()}
        >
          Отклонить
        </Button>
        <Button
          variant="primary" size="sm"
          disabled={!!acceptBlocked}
          title={acceptBlocked ?? undefined}
          onClick={() => onAccept?.()}
        >
          Принять…
        </Button>
      </div>

      {/* Шторка «Файлы» (дерево + diff + предпросмотр панели) */}
      {filesOpen && <FilesDrawer preview={preview} diff={diff} onClose={() => setFilesOpen(false)} />}
    </div>
  );
}

// Рамка карточки: план-рамка (черновик), при обновлении — акцентная подсветка
function cardShell(justUpdated?: boolean): React.CSSProperties {
  return {
    display: 'flex', flexDirection: 'column',
    border: `1px solid ${justUpdated ? C.plan : C.planBorder}`,
    borderRadius: R.xl, background: C.bgCard,
    boxShadow: justUpdated ? SHADOW.selected : 'none',
    padding: SP.lg,
  };
}

// Свёрнутая строка: отклонён / установлен / устарел
function CollapsedRow({ rejected, installed, stale, name, version, expiresAt, actionError, onRestore, onOpenInPlugins }: {
  rejected: boolean;
  installed: boolean;
  stale: boolean;
  name: string;
  version: string;
  // Когда отклонённый исчезнет (rejectedAt + 30 дней); null — активный (не истёк)
  expiresAt: string | null;
  // Ошибка действия (здесь — «Вернуть»): текст сервера, пока действие не повторят
  actionError?: string | null;
  onRestore?: () => void;
  onOpenInPlugins?: () => void;
}) {
  const label = stale ? 'Черновик устарел' : installed ? `Установлен · ${version}` : 'Отклонён';
  const tone: 'neutral' | 'success' | 'plan' = stale ? 'plan' : installed ? 'success' : 'neutral';
  // «Вернуть» — пока срок не истёк (rejectedAt + 30 дней, isRejectedExpired);
  // после — черновик исчезает, кнопки нет (макет V6).
  const restoreVisible = rejected && onRestore && !isRejectedExpired({ rejected, expiresAt } as PluginDraftSummary, Date.now());
  return (
    <div style={{
      display: 'flex', flexDirection: 'column', gap: SP.xs,
      border: `1px solid ${C.borderLight}`, borderRadius: R.lg, background: C.bgCard, padding: SP.md,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <Badge tone={tone} size="sm">{label}</Badge>
        <span style={{ fontSize: FS.sm, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
        <div style={{ flex: 1 }} />
        {installed && onOpenInPlugins && (
          <Button variant="ghost" size="xs" onClick={onOpenInPlugins}>Открыть в «Плагинах»</Button>
        )}
        {restoreVisible && (
          <Button variant="ghost" size="xs" leftIcon={<RotateCcw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={onRestore}>
            Вернуть
          </Button>
        )}
        {stale && onOpenInPlugins && (
          <Button variant="ghost" size="xs" onClick={onOpenInPlugins}>К новой версии</Button>
        )}
      </div>
      {actionError && (
        <Notice tone="danger" icon={AlertTriangle} title="Вернуть не вышло">{actionError}</Notice>
      )}
    </div>
  );
}
