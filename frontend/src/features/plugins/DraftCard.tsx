// Карточка черновика плагина build-it-yourself (макет 4, ADR-031, шаг 4.4).
// Агент собрал плагин в чате; человек одним взглядом видит, что изменится после
// установки, смотрит файлы и решает — принять, отклонить или отдать агенту на доработку.
//
// «Принять…» открывает существующий ConsentDialog (не новое окно). Загрузку данных
// (preview/diff) ведёт обёртка useDraftCard; сам DraftCard «тупой» — данные и
// действия приходят пропсами, поэтому его рендерят в строку в тестах.

import { useEffect, useRef, useSyncExternalStore, useState } from 'react';
import {
  AlertTriangle, ChevronDown, ChevronRight, Copy, File as FileIcon, FileCode, FileImage,
  Folder as FolderIcon, Ghost, Globe, Key, List, Pencil, RotateCcw, Sparkles, TextWrap, X,
} from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { BackButton, Badge, Button, EmptyState, IconButton, InlineSegmented, Modal, Notice, Tabs } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { getEffectiveTheme, subscribeThemeMode } from '../../lib/themeMode';
import { onMessage } from '../../lib/signalr';
import { api } from '../../lib/api';
import { isNetPermission, permissionLabel, NET_PERMISSION_WARNING } from './permissionLabels';
import {
  acceptDisabledReason, buildFixReply, draftContributions, draftDisplayName, isRejectedExpired, problemCount,
  type DraftContribution,
} from './draftModel';
import { buildInitMessage, buildPanelSrcdoc, validateFrameMessage, panelWindowOf, MAX_PANEL_FILE_BYTES, clampContentHeight, PANEL_MAX_HEIGHT, PANEL_MAX_HEIGHT_MOBILE } from './bridge';
// Токены текущей темы хоста (S8): как в установленной панели (PluginFrame) —
// предпросмотр черновика тоже sandbox-iframe, и палитру доставляет хост.
import { readHostTokens } from './panelTokens';
import type {
  PluginDraftDiff, PluginDraftDiffLine, PluginDraftPreview, PluginDraftSummary,
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
            {s.set === true
              ? <Badge tone="success" size="xs" dot>задан</Badge>
              : s.required
                ? <Badge tone="warning" size="xs" dot>не задан</Badge>
                : <Badge tone="neutral" size="xs">не обязателен</Badge>}
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

// Предпросмотр панели черновика: iframe с шимом plugin-kit, без выданных прав
// (granted: [] — панель видит «нет права …» штатно). Перезагружается по событию
// plugin_draft_changed (агент правит черновик) и по кнопке «Перезагрузить».
function PanelPreview({ slug }: { slug: string }) {
  const theme = useSyncExternalStore(subscribeThemeMode, getEffectiveTheme, getEffectiveTheme);
  const isMobile = useIsMobile();
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
  // Токены текущей темы (S8) — как в установленной панели: без них --cc-fg не задан,
  // и в тёмной теме текст был дефолтным (чёрным) на тёмной подложке.
  const srcdoc = html != null && !tooBig ? buildPanelSrcdoc(html, theme, readHostTokens()) : null;
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
          // Токены текущей темы (S8): как в установленной панели, чтобы предпросмотр
          // перекрасился при смене темы без вспышки (first-paint уже в srcdoc).
          tokens: readHostTokens(),
        });
        panelWindow?.postMessage(init, '*');
      } else if (msg.type === 'resize') {
        // То же правило, что у установленной панели (PluginFrame): фрейм = высота
        // контента, прокручивает обёртка (внутренний документ не скроллится).
        setHeight(clampContentHeight(msg.height));
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
      {/* Окно предпросмотра (800 / 560 на мобиле): выше — прокрутка обёртки, как в слоте */}
      <div style={{ maxHeight: isMobile ? PANEL_MAX_HEIGHT_MOBILE : PANEL_MAX_HEIGHT, overflow: 'auto' }}>
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
    </div>
  );
}

// === Окно «Файлы черновика» (макет docs/mockups/plugin-files-window-v1.html) ===
// Шапка + вкладки «Файлы» | «Изменения» | «Панель»; тело: дерево файлов (сайдбар) +
// область просмотра. На мобиле — два уровня: список → файл (BackButton). Футера нет:
// предпросмотр панели — отдельная вкладка.
//
// Что реально есть у окна: бэк отдаёт сырой текст только через diff изменённых файлов
// (lines). Вид «Файл» собран из ctx+add строк — это содержимое новой версии; для
// добавленных/неизменённых файлов содержимого нет — мета и прямая подсказка. «Изменения»
// — полный diff с двумя гуттерами.

type ChangeMark = 'add' | 'mod' | 'del';

const MARK_STYLE: Record<ChangeMark, { bg: string; fg: string; glyph: string; label: string }> = {
  add: { bg: C.successBg, fg: C.successText, glyph: '+', label: 'Добавлен' },
  mod: { bg: C.warningBg, fg: C.warningText, glyph: '~', label: 'Изменён' },
  del: { bg: C.dangerBg, fg: C.dangerText, glyph: '−', label: 'Удалён' },
};

// Человекочитаемый размер: «14,3 КБ» — запятая, как числа в интерфейсе.
function humanSize(n: number): string {
  return n < 1024 ? `${n} Б` : `${(n / 1024).toFixed(1).replace('.', ',')} КБ`;
}

// Метка изменения пути — по записям diff; статус 'new' — все файлы новые.
function changeMarkOf(path: string, diff: PluginDraftDiff | null): ChangeMark | null {
  if (!diff) return null;
  if (diff.status === 'new') return 'add';
  if (diff.added.some(f => f.path === path)) return 'add';
  if (diff.modified.some(f => f.path === path)) return 'mod';
  if (diff.removed.some(f => f.path === path)) return 'del';
  return null;
}

interface TreeNode {
  path: string;
  dirs: Map<string, TreeNode>;
  files: Array<{ path: string; name: string; size: number; mark: ChangeMark | null }>;
}

// Дерево из путей файлов: каталоги неявные (из путей), порядок — алфавитный.
function buildTree(entries: Array<{ path: string; size: number }>, diff: PluginDraftDiff | null): TreeNode {
  const root: TreeNode = { path: '', dirs: new Map(), files: [] };
  for (const e of entries) {
    const parts = e.path.split('/');
    let node = root;
    for (let i = 0; i < parts.length - 1; i++) {
      let child = node.dirs.get(parts[i]);
      if (!child) {
        child = { path: parts.slice(0, i + 1).join('/'), dirs: new Map(), files: [] };
        node.dirs.set(parts[i], child);
      }
      node = child;
    }
    node.files.push({ path: e.path, name: parts[parts.length - 1], size: e.size, mark: changeMarkOf(e.path, diff) });
  }
  return root;
}

function fileIconFor(path: string) {
  if (/\.(png|jpg|jpeg|gif|webp|ico|woff2?|ttf|bin)$/i.test(path)) return FileImage;
  if (/\.(js|ts|html|json|css)$/i.test(path)) return FileCode;
  return FileIcon;
}

// Метка изменения: 16×16, +/~/− в статусной гамме (та же, что у чипов сводки).
function MarkBadge({ mark }: { mark: ChangeMark }) {
  const m = MARK_STYLE[mark];
  return (
    <span aria-label={m.label} title={m.label} style={{
      width: 16, height: 16, borderRadius: R.sm, flexShrink: 0,
      background: m.bg, color: m.fg, display: 'inline-flex',
      alignItems: 'center', justifyContent: 'center',
      fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700,
    }}>{m.glyph}</span>
  );
}

// Строки дерева: 28px десктоп, 44px мобила (тач-цель), отступ уровня — 12px.
function TreeRows({ node, depth, isMobile, selected, onSelect, collapsed, toggle }: {
  node: TreeNode; depth: number; isMobile: boolean;
  selected: string | null; onSelect: (path: string) => void;
  collapsed: Set<string>; toggle: (path: string) => void;
}) {
  const rows: React.ReactNode[] = [];
  for (const name of [...node.dirs.keys()].sort()) {
    const child = node.dirs.get(name)!;
    const isCollapsed = collapsed.has(child.path);
    rows.push(
      <button
        key={child.path}
        data-dir={child.path}
        onClick={() => toggle(child.path)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.xs, height: isMobile ? 44 : 28,
          padding: `0 ${SP.sm}px 0 ${SP.sm + depth * 12}px`, border: 'none', background: 'transparent',
          borderRadius: R.md, cursor: 'pointer', color: C.textSecondary, fontSize: FS.base,
        }}>
        {isCollapsed
          ? <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          : <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />}
        <FolderIcon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
        <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{name}</span>
      </button>
    );
    if (!isCollapsed) {
      rows.push(
        <TreeRows key={child.path + '/'} node={child} depth={depth + 1} isMobile={isMobile}
          selected={selected} onSelect={onSelect} collapsed={collapsed} toggle={toggle} />
      );
    }
  }
  for (const f of node.files.slice().sort((a, b) => a.name.localeCompare(b.name))) {
    const sel = selected === f.path;
    const Icon = fileIconFor(f.path);
    rows.push(
      <button
        key={f.path}
        data-file={f.path}
        aria-current={sel ? 'true' : undefined}
        onClick={() => onSelect(f.path)}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.xs, height: isMobile ? 44 : 28,
          padding: `0 ${SP.sm}px 0 ${SP.sm + depth * 12}px`, border: 'none',
          borderRadius: R.md, cursor: 'pointer',
          background: sel ? C.bgSelected : 'transparent',
          color: f.mark === 'del' ? C.textMuted : sel ? C.textHeading : C.textPrimary,
          fontSize: isMobile ? FS.md : FS.base, fontWeight: sel ? 600 : 400,
          textDecoration: f.mark === 'del' ? 'line-through' : undefined,
        }}>
        <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: sel ? C.textHeading : C.textMuted, flexShrink: 0 }} />
        <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{f.name}</span>
        {f.mark && <MarkBadge mark={f.mark} />}
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{humanSize(f.size)}</span>
      </button>
    );
  }
  return <>{rows}</>;
}

// Код: гуттер номеров строк + текст. view 'file' — содержимое новой версии (ctx+add),
// view 'diff' — полный diff: два гуттера (старый/новый номер) и знак.
function CodePane({ lines, view, wrap, isMobile }: {
  lines: PluginDraftDiffLine[]; view: 'file' | 'diff'; wrap: boolean; isMobile: boolean;
}) {
  const rows = view === 'file' ? lines.filter(l => l.kind !== 'del') : lines;
  const gut = isMobile ? 24 : 34;
  return (
    <div>
      {rows.map((l, i) => {
        const bg = l.kind === 'add' ? C.diffAddBg : l.kind === 'del' ? C.diffRemBg : 'transparent';
        const fg = l.kind === 'add' ? C.diffAddText : l.kind === 'del' ? C.diffRemText : C.textPrimary;
        return (
          <div key={i} style={{ display: 'flex', alignItems: 'flex-start', background: bg }}>
            {view === 'diff' && (
              <span style={{ width: gut, flexShrink: 0, textAlign: 'right', color: C.textMuted, fontFamily: FONT.mono, fontSize: FS.xs, userSelect: 'none' }}>
                {l.oldLine || ''}
              </span>
            )}
            <span style={{
              width: gut, flexShrink: 0, textAlign: 'right', padding: `0 ${SP.xs}px`,
              color: C.textMuted, background: C.bgInset, borderRight: `1px solid ${C.borderLight}`,
              fontFamily: FONT.mono, fontSize: FS.xs, userSelect: 'none',
            }}>
              {view === 'diff' ? (l.newLine || '') : l.newLine}
            </span>
            {view === 'diff' && (
              <span style={{
                width: 14, flexShrink: 0, textAlign: 'center', userSelect: 'none',
                color: l.kind === 'add' ? C.diffAddText : l.kind === 'del' ? C.diffRemText : C.textMuted,
                fontFamily: FONT.mono, fontWeight: 700,
              }}>
                {l.kind === 'add' ? '+' : l.kind === 'del' ? '−' : ''}
              </span>
            )}
            <span style={{
              padding: `0 ${SP.sm}px 0 ${SP.xs}px`, fontFamily: FONT.mono,
              fontSize: isMobile ? FS.xs : FS.sm, lineHeight: isMobile ? 1.4 : 1.5,
              color: fg, whiteSpace: wrap ? 'pre-wrap' : 'pre',
              ...(wrap ? { wordBreak: 'break-all' } : {}),
            }}>
              {l.text || ' '}
            </span>
          </div>
        );
      })}
    </div>
  );
}

// Скелет загрузки вкладки «Изменения» (diff ещё не пришёл).
function SkeletonLines(count: number) {
  return (
    <div style={{ padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} style={{ height: 12, borderRadius: R.sm, background: C.bgSelected, opacity: 0.55 }} />
      ))}
    </div>
  );
}

// Строка сводки изменений: метка + путь + размер.
function SummaryRow({ path, size, mark }: { path: string; size: number; mark: ChangeMark | null }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: '4px 0' }}>
      {mark ? <MarkBadge mark={mark} /> : null}
      <span style={{ flex: 1, minWidth: 0, fontFamily: FONT.mono, fontSize: FS.sm, color: C.textPrimary, overflow: 'hidden', textOverflow: 'ellipsis' }}>{path}</span>
      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{humanSize(size)}</span>
    </div>
  );
}

export function FilesDrawer({ preview, diff, diffError, onClose, onLoadDiff }: {
  preview: PluginDraftPreview;
  diff: PluginDraftDiff | null;
  // Ошибка запроса diff (текст сервера): состояние «diff не загружен» во вкладке «Изменения».
  diffError?: string | null;
  onClose: () => void;
  // Повторить запрос diff (кнопка «Повторить»).
  onLoadDiff?: () => void;
}) {
  const isMobile = useIsMobile();
  const [tab, setTab] = useState<'files' | 'changes' | 'panel'>('files');
  const [selected, setSelected] = useState<string | null>(null);
  const [view, setView] = useState<'file' | 'diff'>('file');
  // На мобиле «Сводка» — отдельный уровень после нажатия; на десктопе она дефолт вкладки.
  const [summaryOpen, setSummaryOpen] = useState(false);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  // Перенос строк: на мобиле по умолчанию — горизонтального скролла в шторке нет.
  const [wrap, setWrap] = useState(isMobile);

  const files = preview.files.filter(f => !f.isDir);
  const selectedFile = files.find(f => f.path === selected) ?? null;
  // Запись diff о выбранном файле (любая: added/modified/removed) — источник содержимого.
  const selectedEntry = diff
    ? [...diff.modified, ...diff.added, ...diff.removed].find(m => m.path === selected) ?? null
    : null;
  // Файл удалён из пакета относительно установленной версии: его больше нет.
  const goneSelected = selected != null && !selectedFile && (diff?.removed.some(m => m.path === selected) ?? false);
  const changedCount = diff ? diff.added.length + diff.modified.length + diff.removed.length : 0;

  const toggleCollapse = (path: string) =>
    setCollapsed(prev => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path); else next.add(path);
      return next;
    });
  // Каждая вкладка помнит свой вид: в «Файлах» — содержимое, в «Изменениях» — diff.
  const selectFile = (path: string, asView: 'file' | 'diff') => {
    setSelected(path);
    setSummaryOpen(false);
    setView(asView);
  };

  const subtitle = (
    <span>
      {preview.name} · <span style={{ fontFamily: FONT.mono }}>{preview.version}</span>
      {preview.installedVersion
        ? ` · обновляет установленную <span style={{ fontFamily: FONT.mono }}>{preview.installedVersion}</span>`
        : ' · новый плагин'}
    </span>
  );

  const tabs = (
    <Tabs
      value={tab}
      onChange={setTab}
      size="md"
      isMobile={isMobile}
      ariaLabel="Разделы черновика"
      items={[
        { value: 'files', label: 'Файлы', count: files.length },
        { value: 'changes', label: 'Изменения', count: changedCount },
        ...(preview.panels.length > 0 ? [{ value: 'panel' as const, label: 'Панель' }] : []),
      ]}
    />
  );

  // Сайдбар: «Файлы» — весь пакет; «Изменения» — сводка + только тронутые файлы.
  let side: React.ReactNode = null;
  if (tab === 'files') {
    side = (
      <div style={{ background: C.bgPanel, borderRight: isMobile ? 'none' : `1px solid ${C.divider}`, padding: isMobile ? '4px 8px 16px' : SP.sm, overflow: 'auto', minWidth: 0 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.sm, padding: '4px 8px 8px', fontSize: FS.xs, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted, fontWeight: 700 }}>
          <span>Пакет</span>
          <span style={{ fontFamily: FONT.mono, textTransform: 'none', letterSpacing: 0, fontWeight: 400 }}>
            {files.length} файлов · {humanSize(files.reduce((s, f) => s + f.size, 0))}
          </span>
        </div>
        {files.length === 0
          ? <EmptyState compact inline icon={<FileIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />} title="Пакет пуст" subtitle="Агент ещё не записал ни одного файла." />
          : <TreeRows node={buildTree(files, diff)} depth={0} isMobile={isMobile} selected={selected}
              onSelect={p => selectFile(p, 'file')} collapsed={collapsed} toggle={toggleCollapse} />}
      </div>
    );
  } else if (tab === 'changes') {
    side = (
      <div style={{ background: C.bgPanel, borderRight: isMobile ? 'none' : `1px solid ${C.divider}`, padding: isMobile ? '4px 8px 16px' : SP.sm, overflow: 'auto', minWidth: 0 }}>
        <button
          data-summary
          aria-current={selected == null && (summaryOpen || !isMobile) ? 'true' : undefined}
          onClick={() => { setSelected(null); setSummaryOpen(true); }}
          style={{
            display: 'flex', alignItems: 'center', gap: SP.xs, height: isMobile ? 44 : 28,
            padding: '0 8px', border: 'none', borderRadius: R.md, cursor: 'pointer',
            background: selected == null && (summaryOpen || !isMobile) ? C.bgSelected : 'transparent',
            color: selected == null && (summaryOpen || !isMobile) ? C.textHeading : C.textPrimary,
            fontSize: isMobile ? FS.md : FS.base, fontWeight: selected == null && (summaryOpen || !isMobile) ? 600 : 400,
          }}>
          <List size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
          <span>Сводка</span>
        </button>
        <div style={{ height: 1, background: C.borderLight, margin: '4px 8px 8px' }} />
        {diffError ? (
          <div style={{ padding: '4px 8px', fontSize: FS.xs, color: C.textMuted }}>Файлы не сравнены</div>
        ) : diff == null ? (
          SkeletonLines(6)
        ) : (
          <>
            <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.sm, padding: '4px 8px 8px', fontSize: FS.xs, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted, fontWeight: 700 }}>
              <span>{diff.status === 'new' ? 'Все файлы новые' : 'Изменённые файлы'}</span>
              <span style={{ fontFamily: FONT.mono, textTransform: 'none', letterSpacing: 0, fontWeight: 400 }}>
                {diff.added.length + diff.modified.length + diff.removed.length}
              </span>
            </div>
            <TreeRows
              node={buildTree([...diff.added, ...diff.modified, ...diff.removed], diff)}
              depth={0} isMobile={isMobile} selected={selected}
              onSelect={p => selectFile(p, 'diff')} collapsed={collapsed} toggle={toggleCollapse} />
          </>
        )}
      </div>
    );
  }

  // Область просмотра.
  let pane: React.ReactNode;
  if (tab === 'panel') {
    // «Панель» — отдельный вид окна, не кнопка в футере (макет).
    pane = (
      <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: 40, padding: `0 ${SP.sm}px 0 ${SP.lg}px`, background: C.bgInset, borderBottom: `1px solid ${C.borderLight}`, flexShrink: 0 }}>
          <span style={{ flex: 1, fontSize: FS.sm, color: C.textSecondary }}>Так панель увидит человек — с правами черновика</span>
        </div>
        <div style={{ flex: 1, minHeight: 0, overflow: 'auto', background: C.bgMain, padding: isMobile ? SP.sm : SP.xl, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm }}>
          <div style={{ width: '100%', maxWidth: 640 }}>
            <PanelPreview slug={preview.slug} />
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, maxWidth: 420, lineHeight: 1.5, textAlign: 'center' }}>
            Песочница: права не выданы — панель честно показывает «нет права …», как без установки.
          </div>
        </div>
      </div>
    );
  } else if (tab === 'changes' && selected == null && (summaryOpen || !isMobile)) {
    // Сводка изменений.
    if (diffError) {
      pane = (
        <div style={{ padding: SP.md }}>
          <Notice
            tone="danger"
            icon={AlertTriangle}
            title="Не удалось сравнить с установленной версией"
            action={onLoadDiff ? <Button variant="secondary" size="sm" onClick={onLoadDiff}>Повторить</Button> : undefined}
          >
            Файлы пакета по-прежнему можно посмотреть во вкладке «Файлы».
          </Notice>
        </div>
      );
    } else if (diff == null) {
      pane = (
        <div style={{ padding: SP.md }}>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginBottom: SP.sm }}>Сравниваем с установленной версией…</div>
          {SkeletonLines(12)}
        </div>
      );
    } else if (diff.status === 'new') {
      pane = (
        <div style={{ padding: isMobile ? SP.md : '24px 32px', background: C.bgCard }}>
          <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 500, color: C.textHeading }}>Новый плагин</div>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginBottom: SP.md }}>
            Установленной версии нет — сравнивать не с чем, всё в пакете новое.
          </div>
          {preview.requestedPermissions.length > 0 && (
            <Notice tone="info" icon={Globe} title="Пакет попросит права">
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: 4 }}>
                {preview.requestedPermissions.map(p => <span key={p} style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>{p}</span>)}
              </div>
            </Notice>
          )}
          <SectionLabel>Файлы · {files.length}</SectionLabel>
          {files.map(f => <SummaryRow key={f.path} path={f.path} size={f.size} mark="add" />)}
        </div>
      );
    } else {
      pane = (
        <div style={{ padding: isMobile ? SP.md : '24px 32px', background: C.bgCard }}>
          <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 500, color: C.textHeading }}>Что поменялось</div>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginBottom: SP.md }}>
            Черновик <b>{diff.version}</b> против установленной <b>{diff.installedVersion ?? '—'}</b>
          </div>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginBottom: SP.md }}>
            {([
              { mark: 'add' as ChangeMark, n: diff.added.length, label: 'добавлено' },
              { mark: 'mod' as ChangeMark, n: diff.modified.length, label: 'изменено' },
              { mark: 'del' as ChangeMark, n: diff.removed.length, label: 'удалено' },
            ]).filter(c => c.n > 0).map(c => (
              <span key={c.mark} style={{
                display: 'inline-flex', alignItems: 'center', gap: SP.xs, height: 24, padding: '0 8px',
                borderRadius: R.pill, fontSize: FS.sm, border: `1px solid ${C.borderLight}`, background: C.bgMain,
              }}>
                <MarkBadge mark={c.mark} />{c.label} {c.n}
              </span>
            ))}
          </div>
          {(diff.manifest?.permissionsAdded.length ?? 0) > 0 && (
            <Notice tone="warning" icon={AlertTriangle} title="Новые права">
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: 4 }}>
                {diff.manifest!.permissionsAdded.map(p => <span key={p} style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>+ {p}</span>)}
              </div>
            </Notice>
          )}
          {(diff.manifest?.secretsAdded.length ?? 0) > 0 && (
            <Notice tone="info" icon={Key} title="Новый секрет" style={{ marginTop: SP.sm }}>
              {diff.manifest!.secretsAdded.map(s => (
                <span style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>{s.name}{s.required ? '' : ' · необязательный'}</span>
              ))}
            </Notice>
          )}
          {diff.truncated && (
            <Notice tone="warning" icon={AlertTriangle} title="Сравнение длинное" style={{ marginTop: SP.sm }}>
              Diff обрезан потолком 1000 строк — чего сервер не отправил, здесь нет.
            </Notice>
          )}
          {diff.modified.length > 0 && (
            <>
              <SectionLabel>Изменено · {diff.modified.length}</SectionLabel>
              {diff.modified.map(f => <SummaryRow key={f.path} path={f.path} size={f.size} mark="mod" />)}
            </>
          )}
          {diff.added.length > 0 && (
            <>
              <SectionLabel>Добавлено · {diff.added.length}</SectionLabel>
              {diff.added.map(f => <SummaryRow key={f.path} path={f.path} size={f.size} mark="add" />)}
            </>
          )}
          {diff.removed.length > 0 && (
            <>
              <SectionLabel>Удалено · {diff.removed.length}</SectionLabel>
              {diff.removed.map(f => <SummaryRow key={f.path} path={f.path} size={f.size} mark="del" />)}
            </>
          )}
          {diff.modified.length === 0 && diff.added.length === 0 && diff.removed.length === 0 && (
            <div style={{ fontSize: FS.sm, color: C.textMuted }}>Файлы не изменились</div>
          )}
        </div>
      );
    }
  } else if (goneSelected) {
    pane = (
      <div style={{ flex: 1, minHeight: 0 }}>
        <EmptyState
          compact
          icon={<Ghost size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          title="Файла больше нет"
          subtitle={<span>Агент обновил черновик, пока окно было открыто, и <span style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>{selected}</span> из пакета убран.</span>}
          action={<Button variant="secondary" size="sm" onClick={() => { setSelected(null); setSummaryOpen(true); }}>К сводке</Button>}
        />
      </div>
    );
  } else if (selectedFile) {
    const parts = selectedFile.path.split('/');
    const name = parts[parts.length - 1];
    const dir = parts.length > 1 ? parts.slice(0, -1).join(' / ') + ' / ' : '';
    const mark = changeMarkOf(selectedFile.path, diff);
    const meta = `${humanSize(selectedFile.size)}${selectedEntry?.lines ? ` · ${selectedEntry.lines.length} строк` : ''}`;
    pane = (
      <div style={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>
        <div style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: 40,
          padding: `0 ${SP.sm}px 0 ${SP.lg}px`, background: C.bgInset,
          borderBottom: `1px solid ${C.borderLight}`, flexShrink: 0, flexWrap: 'wrap',
        }}>
          <span style={{ flex: 1, minWidth: 160, fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {dir}<b style={{ color: C.textHeading, fontWeight: 600 }}>{name}</b>
          </span>
          {mark && <MarkBadge mark={mark} />}
          <span style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{meta}</span>
          {(selectedEntry?.lines ?? []).length > 0 && (
            <InlineSegmented
              value={view}
              onChange={setView}
              isMobile={isMobile}
              size="sm"
              options={[
                { value: 'file', label: 'Файл' },
                { value: 'diff', label: 'Изменения' },
              ]}
            />
          )}
          {(selectedEntry?.lines ?? []).length > 0 && (
            <>
              <IconButton size="sm" ariaLabel="Перенос строк" active={wrap} onClick={() => setWrap(w => !w)}>
                <TextWrap size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
              <IconButton size="sm" ariaLabel="Копировать содержимое" onClick={() => {
                const text = selectedEntry!.lines!.filter(l => l.kind !== 'del').map(l => l.text).join('\n');
                if (typeof navigator !== 'undefined' && navigator.clipboard) void navigator.clipboard.writeText(text);
              }}>
                <Copy size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              </IconButton>
            </>
          )}
        </div>
        {(selectedEntry?.truncated || diff?.truncated) && (
          <Notice tone="warning" icon={AlertTriangle} title="Сравнение длинное">
            Показаны строки, которые сервер успел отправить; полное содержимое файла в окно не приходит.
          </Notice>
        )}
        <div style={{ flex: 1, minHeight: 0, overflow: 'auto', background: C.outputBg }}>
          {selectedEntry?.binary ? (
            <EmptyState
              compact
              icon={<FileImage size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              title="Двоичный файл"
              subtitle={<span>Картинки и другие двоичные файлы здесь не показываем. <span style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>{selectedFile.path} · {humanSize(selectedFile.size)}</span></span>}
            />
          ) : (selectedEntry?.lines ?? []).length > 0 ? (
            <CodePane lines={selectedEntry!.lines!} view={view} wrap={wrap} isMobile={isMobile} />
          ) : (
            <EmptyState
              compact
              icon={<FileIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              title="Содержимое не показано"
              subtitle={<span>Текст приходит только для файлов, изменённых против установленной версии. <span style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>{selectedFile.path}</span></span>}
            />
          )}
        </div>
      </div>
    );
  } else {
    pane = (
      <EmptyState
        compact
        icon={<FileIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        title="Выберите файл"
        subtitle="Файл из дерева откроется справа — содержимое или сравнение с установленной версией."
      />
    );
  }

  // Раскладка: десктоп — сайдбар 280 + область; мобила — список → файл (BackButton).
  const body = isMobile
    ? tab === 'panel'
      ? pane
      : selected != null || (tab === 'changes' && summaryOpen)
        ? (
          <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: SP.xs, height: 44, padding: '0 4px',
              background: C.bgCard, borderBottom: `1px solid ${C.borderLight}`, flexShrink: 0,
            }}>
              <BackButton onClick={() => setSelected(null)} ariaLabel={tab === 'files' ? 'Назад к списку файлов' : 'Назад к сводке'}>
                <span style={{ fontSize: FS.base, color: C.textSecondary }}>{tab === 'files' ? 'Файлы' : 'Изменения'}</span>
              </BackButton>
              <div style={{ flex: 1 }} />
              {(selectedEntry?.lines ?? []).length > 0 && (
                <>
                  <IconButton size="lg" ariaLabel="Перенос строк" active={wrap} onClick={() => setWrap(w => !w)}>
                    <TextWrap size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                  </IconButton>
                  <IconButton size="lg" ariaLabel="Копировать содержимое" onClick={() => {
                    const text = selectedEntry!.lines!.filter(l => l.kind !== 'del').map(l => l.text).join('\n');
                    if (typeof navigator !== 'undefined' && navigator.clipboard) void navigator.clipboard.writeText(text);
                  }}>
                    <Copy size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                  </IconButton>
                </>
              )}
            </div>
            <div style={{ flex: 1, minHeight: 0, overflow: 'auto', background: C.outputBg }}>
              {pane}
            </div>
          </div>
        )
        : side
    : (
      <div style={{ flex: 1, minHeight: 0, display: 'grid', gridTemplateColumns: tab === 'panel' ? '1fr' : '280px 1fr' }}>
        {side}
        <div style={{ minWidth: 0, minHeight: 0, overflow: 'auto', display: 'flex', flexDirection: 'column' }}>
          {pane}
        </div>
      </div>
    );

  return (
    <Modal size="fullscreen" title="Файлы черновика" subtitle={subtitle} onClose={onClose}>
      {tabs}
      {body}
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
  // Ошибка запроса diff (понятным текстом): состояние «diff не загружен» в окне файлов.
  diffError?: string | null;
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
    summary, preview, diff, diffError, justUpdated, loading, error, actionError, staleNewerSlug,
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
        name={draftDisplayName(summary.displayName, preview?.name ?? summary.id)}
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

  // Имя в шапке и в репликах агенту — человекочитаемое («Погода»), а не slug манифеста
  const cardName = draftDisplayName(summary.displayName, preview.name);
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
              {cardName}
            </span>
            <Badge tone="plan" size="xs">Черновик</Badge>
          </div>
          <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
            {summary.id} · {summary.version}
          </div>
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
            {preview.installedVersion
              ? `Обновление установленной версии ${preview.installedVersion} · виден только вам`
              : 'Собран агентом · ещё не установлен · виден только вам'}
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
          ? <Button variant="ghost" size="sm" leftIcon={<Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => onEdit?.(`Поправь плагин «${cardName}»: `)}>Править</Button>
          : <Button variant="secondary" size="sm" leftIcon={<Pencil size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={() => onEdit?.(buildFixReply(cardName, problems))}>Попросить агента исправить</Button>}
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

      {/* Окно «Файлы черновика» (дерево + содержимое/diff + предпросмотр панели) */}
      {filesOpen && <FilesDrawer preview={preview} diff={diff} diffError={diffError} onClose={() => setFilesOpen(false)} onLoadDiff={onLoadDiff} />}
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
