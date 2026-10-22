import { useMemo, useRef, useState } from 'react';
import { AlertTriangle, Folder, MoreHorizontal, Plug, Sparkles } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { AccordionSection } from '../projects/dialogs/AccordionSection';
import {
  Button, Toggle, IconButton, EmptyState, Notice, ConfirmDialog,
  Modal, Menu, MenuItem, MenuSep, CountBadge, Badge,
} from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import { useProjectFeature } from '../../lib/projectCapabilities';
import { ProjectFeature, type Project } from '../../types';
import { api, type PluginInfo } from '../../lib/api';
import { useInstalledPlugins } from './useInstalledPlugins';
import { eraseOnUninstall } from './pluginStorageLifecycle';
import { ConsentDialog, type PluginConsentData } from './ConsentDialog';
import { DraftsSection } from './DraftsSection';
import { pluginDisplayName } from './panelNaming';

// === Секция «Плагины» в настройках проекта (экран 1 макетов, ADR-031, флаг plugins-mvp) ===
// Самодостаточная: сама грузит список (useInstalledPlugins), сама ведёт установку/удаление/
// выдачу прав. По образцу McpProjectSection: возвращает null, когда фича выключена.

// Данные диалога согласия из записи установки. Имя — общее для списка и диалога
// (pluginDisplayName: title первой панели, иначе humanizeId). Вклады (contributes) в
// DTO бэка нет — не показываем. Флаг «обязательный» (В2) DTO тоже не отдаёт (только
// {name, set, updatedAt}), поэтому считаем ВСЕ секреты обязательными — строгое чтение В2:
// пока любой не задан, «Установить» заблокирован. (Открытый вопрос: бэку стоит выводить
// required из манифеста.)
function toConsentData(info: PluginInfo, mode: 'install' | 'modify'): PluginConsentData {
  return {
    id: info.id,
    name: pluginDisplayName(info),
    version: info.version,
    path: info.source.startsWith('upload:') ? info.source.slice('upload:'.length) : undefined,
    sha256: info.sha256 ? info.sha256.slice(0, 8) : undefined,
    contributions: [],
    requested: info.requested,
    secrets: info.secrets.map(s => ({
      name: s.name,
      required: true,
      set: s.set,
      setAt: s.updatedAt ?? null,
    })),
    granted: mode === 'modify' ? info.granted : undefined,
  };
}

// Плитка 36×36 с инициалами (макет, экран 1). Выключенный — в нейтральной гамме.
function PluginTile({ name, muted }: { name: string; muted?: boolean }) {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const initials = parts.length === 0 ? '?'
    : parts.length === 1 ? parts[0].slice(0, 2).toUpperCase()
      : (parts[0][0] + parts[1][0]).toUpperCase();
  return (
    <div style={{
      width: 36, height: 36, borderRadius: R.lg, flexShrink: 0,
      background: muted ? C.bgInset : C.accentLight,
      color: muted ? C.textMuted : C.accent,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      fontWeight: 700, fontSize: 13,
    }}>
      {initials}
    </div>
  );
}

// Статус строки (ровно один, по приоритету макета):
//   «Не задан секрет» → «Нужны права» → «Права выданы · N» → «Выключен».
type RowStatus =
  | { kind: 'secret'; label: string; warning: boolean }
  | { kind: 'perms'; label: string; warning: boolean }
  | { kind: 'granted'; label: string; warning: boolean }
  | { kind: 'disabled'; label: string; warning: boolean };

function rowStatus(p: PluginInfo): RowStatus {
  if (p.secrets.some(s => !s.set)) return { kind: 'secret', label: 'Не задан секрет', warning: true };
  if (p.requested.some(r => !p.granted.includes(r))) return { kind: 'perms', label: 'Нужны права', warning: true };
  if (p.granted.length > 0) return { kind: 'granted', label: `Права выданы · ${p.granted.length}`, warning: false };
  return { kind: 'disabled', label: 'Выключен', warning: false };
}

// === Строка плагина: плитка + имя/версия + статус + тумблер + «⋯» ===
function PluginRow({ plugin, isMobile, onToggle, onOpenConsent, onDelete, onRefresh }: {
  plugin: PluginInfo;
  isMobile: boolean;
  onToggle: (id: string, enabled: boolean) => void;
  onOpenConsent: (info: PluginInfo) => void;
  onDelete: (info: PluginInfo) => void;
  onRefresh: (info: PluginInfo) => void;
}) {
  const [hover, setHover] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const status = rowStatus(plugin);
  const name = pluginDisplayName(plugin);
  const isDev = plugin.version === 'dev';

  const openMenu = () => {
    if (menuRef.current) setAnchor(menuRef.current.getBoundingClientRect());
    setMenuOpen(true);
  };
  const closeMenu = () => { setMenuOpen(false); setAnchor(null); };

  return (
    <div
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.md,
        padding: '10px 8px',
        background: hover ? C.bgSelected : 'transparent',
        borderRadius: R.md,
      }}
    >
      <PluginTile name={name} muted={!plugin.enabled} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
          <span style={{
            fontWeight: 600, fontSize: FS.base,
            color: plugin.enabled ? C.textHeading : C.textSecondary,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{name}</span>
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{plugin.version}</span>
          {isDev && <Badge tone="info">из папки · следит за правками</Badge>}
        </div>
        {isDev && plugin.source.startsWith('upload:') && (
          <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
            {plugin.source.slice('upload:'.length)}
          </div>
        )}
      </div>

      {/* Статус: ровно один, по приоритету. Warning-статусы кликабельны → диалог согласия. */}
      {status.warning ? (
        <Button
          variant="ghost" size="xs"
          style={{ color: C.warningText, borderColor: C.warning, flexShrink: 0 }}
          leftIcon={<AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => onOpenConsent(plugin)}
        >
          {status.label}
        </Button>
      ) : status.kind === 'granted' ? (
        <Badge tone="success" dot style={{ flexShrink: 0 }}>{status.label}</Badge>
      ) : (
        <Badge tone="neutral" style={{ flexShrink: 0 }}>{status.label}</Badge>
      )}

      <Toggle
        checked={plugin.enabled}
        onChange={v => onToggle(plugin.id, v)}
        ariaLabel={`${name}: включён`}
      />

      {/* Меню «⋯» — только на десктопе; на мобиле тап по строке открывает шторку. */}
      {!isMobile && (
        <div ref={menuRef} style={{ position: 'relative', flexShrink: 0 }}>
          <IconButton size="xs" tone="muted" ariaLabel="Меню плагина" onClick={openMenu}>
            <MoreHorizontal size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </IconButton>
          {menuOpen && anchor && (
            <Menu onClose={closeMenu} anchor={anchor} align="right">
              <MenuItem label="Права и секреты…" onClick={() => { closeMenu(); onOpenConsent(plugin); }} />
              {isDev && <MenuItem label="Обновить из папки" onClick={() => { closeMenu(); onRefresh(plugin); }} />}
              <MenuItem label="Открыть файлы" onClick={closeMenu} />
              <MenuSep />
              <MenuItem label="Удалить плагин…" danger onClick={() => { closeMenu(); onDelete(plugin); }} />
            </Menu>
          )}
        </div>
      )}
    </div>
  );
}

// === Секция «Плагины» ===
export function PluginsProjectSection({ project, onUpdated, onEdit }: {
  project: Project;
  onUpdated?: (updated: Project) => void;
  // «Править»/«Попросить агента исправить» в карточке черновика: готовая реплика → чат
  onEdit?: (reply: string) => void;
}) {
  const isMobile = useIsMobile();
  const pluginsMvp = useFeature(FLAGS.pluginsMvp);
  const pluginsAvail = useProjectFeature(project, ProjectFeature.Plugins);
  const buildYourself = useFeature(FLAGS.pluginsBuildYourself);
  const { plugins, loading, error, refresh, missingCount } = useInstalledPlugins(project.id, pluginsMvp && pluginsAvail);

  // Диалог согласия: режим «установить» (после install) или «изменить» (со строки).
  const [consent, setConsent] = useState<{ data: PluginConsentData; mode: 'install' | 'modify' } | null>(null);
  // Диалог «Поставить из папки»: путь к каталогу пакета.
  const [folderDialog, setFolderDialog] = useState(false);
  const [folderPath, setFolderPath] = useState('');
  const [folderBusy, setFolderBusy] = useState(false);
  const [folderErr, setFolderErr] = useState<string | null>(null);
  // Подтверждение удаления
  const [deleting, setDeleting] = useState<PluginInfo | null>(null);
  // Оптимистичные тумблеры: id → enabled, до прихода ответа сервера.
  const [localOverride, setLocalOverride] = useState<Record<string, boolean>>({});

  // Установить пакет по пути: slug = basename папки, метка upload:{slug}.
  const submitInstall = async () => {
    const slug = (folderPath.trim().split(/[\\/]/).filter(Boolean).pop() ?? '').toLowerCase();
    if (!slug) { setFolderErr('Укажите путь к папке пакета'); return; }
    setFolderBusy(true); setFolderErr(null);
    try {
      const info = await api.plugins.install(`upload:${slug}`);
      setFolderDialog(false); setFolderPath('');
      // Пакет установлен, но права ещё не выданы — диалог согласия в режиме «установить».
      setConsent({ data: toConsentData(info, 'install'), mode: 'install' });
    } catch (e: unknown) {
      setFolderErr(e instanceof Error ? e.message : 'Не удалось установить');
    } finally {
      setFolderBusy(false);
    }
  };

  // Выдать права/секреты (общий для «установить» и «изменить»).
  const submitConsent = async (permissions: string[], secrets: { name: string; value: string }[]) => {
    if (!consent) return;
    await api.plugins.grant(consent.data.id, permissions, secrets);
    setConsent(null);
    void refresh();
    onUpdated?.(project);
  };

  // Тумблер: оптимистично, откат при ошибке.
  const toggle = (id: string, enabled: boolean) => {
    const prev = localOverride[id] ?? plugins.find(p => p.id === id)?.enabled ?? true;
    setLocalOverride(o => ({ ...o, [id]: enabled }));
    api.plugins.setEnabled(id, enabled)
      .then(() => { onUpdated?.(project); void refresh(); })
      .catch(() => setLocalOverride(o => ({ ...o, [id]: prev })));
  };

  const shownPlugins = useMemo(
    () => plugins.map(p => localOverride[p.id] != null ? { ...p, enabled: localOverride[p.id] } : p),
    [plugins, localOverride],
  );

  // «Создать по промпту» — кнопка видна только под флагом; карточка черновика — шаг 4.4.
  const createFromPrompt = () => { /* 4.4: откроет чат с карточкой черновика */ };

  if (!pluginsMvp) return null;

  // Локальный проект без поддержки плагинов: вместо экрана — info-плашка (макет, экран 1).
  if (!pluginsAvail) {
    return (
      <AccordionSection icon={Plug} title="Плагины" summary="недоступно" summaryTone="neutral">
        <Notice tone="info" icon={Plug}>
          Плагины для локальных проектов пока недоступны
        </Notice>
      </AccordionSection>
    );
  }

  const subtitle = 'Панели, команды и навыки, которые расширяют чаты этого проекта. Без выданных прав агент плагин не вызывает.';

  return (
    <AccordionSection
      icon={Plug}
      title="Плагины"
      summary={plugins.length === 0 ? 'нет' : `${plugins.length}`}
      summaryTone={missingCount > 0 ? 'warning' : 'neutral'}
      badge={missingCount > 0 ? <CountBadge value={missingCount} tone="warning" inline /> : undefined}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        {/* Заголовок + подзаголовок */}
        <div>
          <div style={{ fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 500, color: C.textHeading, letterSpacing: '-0.01em' }}>
            Плагины
          </div>
          <div style={{ fontSize: FS.base, color: C.textSecondary, marginTop: 2 }}>{subtitle}</div>
        </div>

        {/* Кнопки */}
        <div style={{ display: 'flex', gap: SP.sm, flexDirection: isMobile ? 'column' : 'row' }}>
          <Button
            variant="primary" size="sm" fullWidth={isMobile}
            leftIcon={<Folder size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            onClick={() => { setFolderErr(null); setFolderDialog(true); }}
          >
            {isMobile ? 'Из папки' : 'Поставить из папки'}
          </Button>
          {buildYourself && (
            <Button
              variant="ghost" size="sm" fullWidth={isMobile}
              leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              onClick={createFromPrompt}
            >
              {isMobile ? 'По промпту' : 'Создать по промпту'}
            </Button>
          )}
        </div>

        {/* Состояния: загрузка / ошибка / пусто / список */}
        {loading ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            {[0, 1, 2].map(i => (
              <div key={i} style={{ height: 52, borderRadius: R.lg, background: C.bgInset }} />
            ))}
          </div>
        ) : error ? (
          <Notice tone="danger" icon={AlertTriangle} title="Не удалось получить список"
            action={<Button variant="ghost" size="xs" onClick={() => void refresh()}>Повторить</Button>}>
            {error}
          </Notice>
        ) : shownPlugins.length === 0 ? (
          <div style={{ border: `1px dashed ${C.dashed}`, borderRadius: R.xl, padding: SP.lg }}>
            <EmptyState
              icon={<Plug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
              title="Плагинов пока нет"
              subtitle="Поставьте готовый из папки или попросите агента собрать свой."
              action={
                <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
                  <Button variant="primary" size="sm" fullWidth onClick={() => { setFolderErr(null); setFolderDialog(true); }}>
                    Поставить из папки
                  </Button>
                  {buildYourself && (
                    <Button variant="ghost" size="sm" fullWidth onClick={createFromPrompt}>Создать по промпту</Button>
                  )}
                </div>
              }
            />
          </div>
        ) : (
          <div style={{
            background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
            display: 'flex', flexDirection: 'column',
          }}>
            {shownPlugins.map((p, i) => (
              <div key={p.id} style={{ borderTop: i === 0 ? 'none' : `1px solid ${C.borderLight}` }}>
                <PluginRow
                  plugin={p}
                  isMobile={isMobile}
                  onToggle={toggle}
                  onOpenConsent={info => setConsent({ data: toConsentData(info, 'modify'), mode: 'modify' })}
                  onDelete={setDeleting}
                  onRefresh={() => void refresh()}
                />
              </div>
            ))}
          </div>
        )}

        {/* Черновики от агента (флаг plugins-build-yourself): секция сама решает, показывать ли */}
        <DraftsSection project={project} onUpdated={onUpdated} onEdit={onEdit} />
      </div>

      {/* Диалог «Поставить из папки» */}
      {folderDialog && (
        <Modal
          width={440}
          title="Поставить из папки"
          onClose={() => !folderBusy && setFolderDialog(false)}
          closeOnBackdrop={!folderBusy}
          footer={
            <div style={{ display: 'flex', gap: SP.sm, width: '100%', flexDirection: isMobile ? 'column' : 'row' }}>
              <Button variant="ghost" size="md" fullWidth={isMobile} disabled={folderBusy} onClick={() => setFolderDialog(false)}>
                Отмена
              </Button>
              <Button variant="primary" size="md" fullWidth={isMobile} loading={folderBusy} onClick={() => void submitInstall()}>
                Установить
              </Button>
            </div>
          }
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>
              Путь к папке пакета на этом компьютере. Пакет должен содержать нативный манифест
              <span style={{ fontFamily: FONT.mono }}> .claude-plugin/plugin.json</span>
              (обязателен, CLI берёт из него имя пакета) и, при необходимости, наш
              <span style={{ fontFamily: FONT.mono }}> ccs-plugin.json</span> (источник id/прав).
            </div>
            <input
              value={folderPath}
              onChange={e => setFolderPath(e.target.value)}
              placeholder="/home/you/plugins/my-plugin"
              style={{
                height: 38, padding: '0 12px', borderRadius: R.xl,
                border: `1px solid ${C.border}`, background: C.bgWhite,
                color: C.textPrimary, fontFamily: FONT.mono, fontSize: FS.sm, outline: 'none',
              }}
            />
            {folderErr && (
              <div style={{ fontSize: FS.sm, color: C.dangerText, background: C.dangerBg, padding: '6px 10px', borderRadius: R.md }}>
                {folderErr}
              </div>
            )}
          </div>
        </Modal>
      )}

      {/* Диалог согласия (установить / изменить) */}
      {consent && (
        <ConsentDialog
          data={consent.data}
          mode={consent.mode}
          onClose={() => setConsent(null)}
          onSubmit={submitConsent}
          // «Сохранить» в строке секрета — PUT /secrets/{name}; возвращает updatedAt для бейджа
          onSaveSecret={async (name, value) => (await api.plugins.setSecret(consent.data.id, name, value)).updatedAt}
        />
      )}

      {/* Подтверждение удаления */}
      {deleting && (
        <ConfirmDialog
          title={`Удалить «${pluginDisplayName(deleting)}»?`}
          subtitle="Пакет и выданные права будут удалены. Секреты останутся в архиве владельца."
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => {
            const d = deleting;
            setDeleting(null);
            api.plugins.uninstall(d.id).then(() => { eraseOnUninstall(d.id); void refresh(); onUpdated?.(project); }).catch(() => {});
          }}
          onCancel={() => setDeleting(null)}
        />
      )}
    </AccordionSection>
  );
}
