import { useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { ChevronDown, ChevronRight, Pencil, Plug, X } from 'lucide-react';
import { Button, Dot, EmptyState, IconButton, TextField, Toggle } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { groupHeaderStyle } from '../../lib/modelProvidersShared';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { accessSummaryOn, mcpAuthLine, mcpStatusTone, plural } from './useMcpData';
import type { McpData } from './useMcpData';
import type { McpBuiltinServer, McpCatalogRevision, McpServer } from '../../types';

// Вкладка «Серверы»: свои записи полноразмерными карточками, всё остальное — компактными
// плитками по группам. Ось группировки — кто подключил сервер и кто им управляет:
// сервисы AI Home, память персон (свёрнута строкой — её столько же, сколько персон,
// и делать с ней нечего) и наследство из конфигов CLI. Пустое состояние показано только
// там, где пусто: остальные группы остаются на месте.

// Человекочитаемые имена сервисов продукта: сырые ключи (wsp, codegraph) ничего не говорят.
// Ключа нет в словаре (новый продуктовый сервер приехал раньше словаря) — показываем сам ключ
const SERVICE_TITLES: Record<string, string> = {
  tasks: 'Задачи',
  notes: 'Заметки',
  memory: 'Долгая память',
  personas: 'Персоны',
  wsp: 'Проекты и файлы',
  notifications: 'Уведомления',
  widgets: 'Виджеты',
  codegraph: 'Граф кода',
  dify: 'База знаний (Dify)',
  'fal-ai': 'Генерация картинок (fal.ai)',
  glif: 'Медиа-агент (Glif)',
};

const PMEM_PREFIX = 'pmem_';

// Сетка плиток: minmax(140px) держит две колонки даже на 320px — при 150px там
// оставалась одна растянутая плитка ради двух коротких строк
const tileGridStyle: CSSProperties = {
  display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))', gap: SP.sm,
};

const hintStyle: CSSProperties = {
  fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, padding: '0 2px',
};

export function McpServerList({ data, onEdit, onAdd, onCatalog, onOpenAccess, onDelete }: {
  data: McpData;
  onEdit: (server: McpServer) => void;
  onAdd: () => void;
  // Кнопка «Найти сервер» в шапке раздела: ведёт в каталог (фича mcp-catalog).
  // Список сам не знает, включён ли флаг — это решает родитель и не передаёт
  // колбэк, если каталог выключен (тогда и кнопки нет)
  onCatalog?: () => void;
  onOpenAccess: () => void;
  onDelete: (server: McpServer) => void;
}) {
  const { servers, builtin } = data;
  const hasLegacy = servers?.some(s => s.source !== 'manual') ?? false;
  // Три известные группы разбираем явно, всё прочее — в «подключено вне AI Home»:
  // незнакомое значение group (новая группа с бэкенда) обязано остаться видимым,
  // иначе сервер подключён, а в списке его нет
  const serviceTiles = builtin.filter(t => t.group === 'product' || t.group === 'integration');
  const memoryTiles = builtin.filter(t => t.group === 'persona-memory');
  const externalTiles = builtin.filter(t => !serviceTiles.includes(t) && !memoryTiles.includes(t));

  if (servers === null) {
    return <div style={{ color: C.textMuted, fontSize: FS.md, padding: '8px 0' }}>Загрузка…</div>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={groupHeaderStyle}>Ваши серверы</div>

      {servers.length === 0 ? (
        <div style={{
          border: `1px dashed ${C.dashed}`, borderRadius: R.xxl, padding: `${SP.sm}px 0`,
        }}>
          <EmptyState
            icon={<Plug size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
            title="Своих серверов пока нет"
            // Что такое внешний MCP-сервер: программа, которую AI Home запускает
            // рядом с собой, чтобы дать чатам новые возможности — файлы, БД, поиск
            // по API. Это первое, что видит новый человек в разделе, и без строчки
            // «а зачем» кнопки «Найти» и «Добавить» выглядели бы как ритуал
            subtitle="Внешний MCP-сервер — это программа, которую AI Home запускает рядом с собой, чтобы дать чатам новые возможности: чтение файлов, доступ к базе, поиск по API. Найдите свой — Notion, файловый сервер, Postgres — или добавьте руками"
            action={
              <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
                {onCatalog && <Button variant="primary" size="sm" onClick={onCatalog}>Найти сервер</Button>}
                <Button variant="ghost" size="sm" onClick={onAdd}>Добавить вручную</Button>
              </div>
            }
          />
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          {servers.map(server => (
            <ServerCard
              key={server.id}
              server={server}
              data={data}
              onEdit={() => onEdit(server)}
              onOpenAccess={onOpenAccess}
              onDelete={() => onDelete(server)}
            />
          ))}
          {hasLegacy && (
            <div style={hintStyle}>
              «Наследство» — записи, пришедшие из готового конфига (вставка JSON или старый
              .mcp.json): они работают как раньше, здесь их можно проверить и доправить.
              Крестика удаления у них нет — сам сервер остался бы в исходном конфиге,
              который AI&nbsp;Home не редактирует.
            </div>
          )}
        </div>
      )}

      {onCatalog && (
        <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
          <Button variant="primary" size="sm" onClick={onCatalog}>Найти сервер</Button>
          <Button variant="ghost" size="sm" onClick={onAdd}>Добавить вручную</Button>
        </div>
      )}

      <div style={groupHeaderStyle}>Сервисы AI Home</div>
      {serviceTiles.length === 0 ? (
        <div style={hintStyle}>
          Пока ничего не наблюдалось: статус серверов приезжает из первого же хода в чате.
        </div>
      ) : (
        <>
          <div style={tileGridStyle}>
            {serviceTiles.map(tile => (
              <Tile
                key={tile.key}
                tile={tile}
                title={SERVICE_TITLES[tile.key] ?? tile.key}
                subtitle={tile.key}
                badge={tile.group === 'integration' ? 'через интернет' : undefined}
              />
            ))}
          </div>
          <div style={hintStyle}>
            Сервисы — часть AI Home: здесь виден только статус, выключить или удалить их нельзя.
            Метка «через интернет» — сервис, который ходит во внешнюю систему по ключу, настроенному в продукте.
          </div>
        </>
      )}

      {memoryTiles.length > 0 && <PersonaMemorySection tiles={memoryTiles} />}

      {externalTiles.length > 0 && (
        <>
          <div style={groupHeaderStyle}>Подключено вне AI Home</div>
          <div style={tileGridStyle}>
            {externalTiles.map(tile => <Tile key={tile.key} tile={tile} title={tile.key} />)}
          </div>
          <div style={hintStyle}>
            Эти серверы Claude Code принёс из своих конфигов и плагинов — AI Home их не подключал
            и ими не управляет, только показывает статус. Чтобы управлять таким сервером отсюда,
            добавьте его как свой на вкладке «Добавить».
          </div>
        </>
      )}
    </div>
  );
}

// Сводка по группе серверов памяти: человеку важно «всё ли в порядке», а не список.
// Статусы приезжают из ходов в чате, поэтому «не проверялись» — нормальное начальное
// состояние, а не проблема: тревожат только failed и needs-auth.
function memorySummary(tiles: McpBuiltinServer[]): { text: string; alarming: boolean } {
  const failed = tiles.filter(t => t.status?.status === 'failed').length;
  const needsAuth = tiles.filter(t => t.status?.status === 'needs-auth').length;
  if (failed) return { text: `${failed} не ${plural(failed, 'отвечает', 'отвечают', 'отвечают')}`, alarming: true };
  if (needsAuth) return { text: `${needsAuth} — нужен вход`, alarming: true };
  if (tiles.every(t => t.status?.status === 'connected')) return { text: 'все работают', alarming: false };
  if (tiles.every(t => !t.status)) return { text: 'пока не проверялись', alarming: false };
  return { text: 'часть ещё не проверялась', alarming: false };
}

// Память персон-консультантов: у каждой персоны с включённой памятью свой сервер, и
// плитками по числу персон раздел раздувался бы в простыню, с которой всё равно нечего
// делать. Свёрнута строкой; раскрывается сама, только если что-то сломалось.
function PersonaMemorySection({ tiles }: { tiles: McpBuiltinServer[] }) {
  const summary = memorySummary(tiles);
  const [open, setOpen] = useState(summary.alarming);
  const rowRef = useRef<HTMLButtonElement>(null);
  const Icon = open ? ChevronDown : ChevronRight;

  const toggle = () => {
    setOpen(!open);
    // На мобиле раскрытые плитки выпихивают строку из вида — человек не понимает,
    // что раскрылось; возвращаем её в поле зрения
    if (!open) requestAnimationFrame(() => rowRef.current?.scrollIntoView({ block: 'nearest' }));
  };

  return (
    <>
      <button
        ref={rowRef}
        type="button"
        onClick={toggle}
        aria-expanded={open}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: 40,
          padding: `${SP.sm}px 13px`, textAlign: 'left', cursor: 'pointer',
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          fontFamily: FONT.sans, fontSize: FS.base,
        }}
      >
        <Icon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={C.textMuted} style={{ flexShrink: 0 }} />
        <span style={{ fontWeight: 600, color: C.textHeading }}>Память персон</span>
        <span style={{ color: C.textMuted, fontSize: FS.sm }}>
          {tiles.length} {plural(tiles.length, 'сервер', 'сервера', 'серверов')}
        </span>
        <span style={{ marginLeft: 'auto', fontSize: FS.sm, color: summary.alarming ? C.warningText : C.textMuted }}>
          {summary.text}
        </span>
      </button>
      {open && (
        <>
          <div style={tileGridStyle}>
            {tiles.map(tile => (
              <Tile
                key={tile.key}
                tile={tile}
                title={tile.key.slice(PMEM_PREFIX.length)}
                subtitle={tile.key}
              />
            ))}
          </div>
          <div style={hintStyle}>
            Личная память заводится сама на каждую персону-консультанта с включённой памятью —
            подключать и настраивать эти серверы не нужно.
          </div>
        </>
      )}
    </>
  );
}

function ServerCard({ server, data, onEdit, onOpenAccess, onDelete }: {
  server: McpServer;
  data: McpData;
  onEdit: () => void;
  onOpenAccess: () => void;
  onDelete: () => void;
}) {
  const checking = !!data.checking[server.id];
  const tone = mcpStatusTone(server.status?.status);
  const needsAuth = !checking && server.status?.status === 'needs-auth';
  // OAuth есть только у http/sse (McpOAuthService.StartAsync отказывает stdio) — у него
  // «Войти» ведёт к настоящему действию, у stdio остаётся только правка настроек записи
  const canLogin = needsAuth && server.transport !== 'stdio';
  const oauthBusy = !!data.oauthPending[server.id];
  const oauthNotice = data.oauthNotice[server.id];
  const legacy = server.source !== 'manual';
  const personasOn = data.personasOnCount(server.key);
  const projectsOn = data.projectsOnCount(server.key);
  const target = server.transport === 'stdio'
    ? [server.command, ...server.args].filter(Boolean).join(' ')
    : server.url ?? '';
  // Ревизия из реестра (волна 2). Карточка ничего не знает про запрос — он идёт
  // от родителя ОТДЕЛЬНО от списка, чтобы раздел открывался при лежащем реестре.
  // Здесь только показ: deprecated/deleted → warningBg, новее → нейтральный info,
  // общий отказ батча → нейтральная «проверить не удалось» (НЕ отзыв, иначе при
  // лежащем реестре люди выключат рабочие серверы)
  const revision = server.catalogRef?.name
    ? data.revisions[server.catalogRef.name]
    : undefined;

  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
      padding: '11px 13px', display: 'flex', flexDirection: 'column', gap: 6,
      opacity: server.enabled ? 1 : 0.62,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <span style={{
          width: 8, height: 8, borderRadius: R.full, flexShrink: 0,
          background: checking ? C.warning : tone.dot,
          animation: checking ? 'pulsedot 1s ease-in-out infinite' : undefined,
        }} />
        <div style={{
          display: 'flex', alignItems: 'center', gap: 7, minWidth: 0, flex: 1,
          fontSize: 13.5, fontWeight: 600, color: C.textHeading,
        }}>
          <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {server.label || server.key}
          </span>
          <Badge tone={legacy ? 'legacy' : 'own'}>{legacy ? 'наследство' : 'свой'}</Badge>
        </div>
        <span style={{
          fontFamily: FONT.mono, fontSize: 10.5, color: C.textMuted,
          border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '1px 6px', flexShrink: 0,
        }}>{server.transport}</span>
        <Toggle
          checked={server.enabled}
          onChange={v => data.setEnabled(server, v)}
          ariaLabel={`Включён: ${server.label || server.key}`}
        />
      </div>

      {target && (
        <div title={target} style={{
          fontFamily: FONT.mono, fontSize: 11, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{target}</div>
      )}

      {(revision || data.revisionsCheckFailed) && (
        <CatalogRevisionNote revision={revision} checkFailed={data.revisionsCheckFailed} />
      )}

      <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', fontSize: FS.sm }}>
        <span style={{
          color: (personasOn || projectsOn || server.allowOutsideProjects ? C.textSecondary : C.textMuted),
        }}>
          {accessSummaryOn(personasOn, projectsOn, server.allowOutsideProjects)}
        </span>
        <button
          type="button"
          onClick={onOpenAccess}
          style={{
            font: 'inherit', fontSize: FS.xs, color: C.accent, background: 'transparent',
            border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline',
          }}
        >Настроить</button>
      </div>

      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between',
        gap: SP.sm, flexWrap: 'wrap', minHeight: 40,
      }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
          <div style={{ fontSize: FS.sm, color: checking ? C.textMuted : tone.text }}>
            {checking ? 'Проверяем… обычно 2–3 секунды' : mcpAuthLine(server, data.probes[server.id])}
          </div>
          {oauthNotice && (
            <div style={{ fontSize: FS.xs, color: C.warningText }}>{oauthNotice}</div>
          )}
          {needsAuth && !canLogin && (
            <div style={{ fontSize: FS.xs, color: C.textMuted }}>
              Проверьте ключ в{' '}
              <button
                type="button"
                onClick={onEdit}
                style={{
                  font: 'inherit', fontSize: FS.xs, color: C.accent, background: 'transparent',
                  border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline',
                }}
              >настройках сервера</button>
            </div>
          )}
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
          {canLogin && (
            <Button
              variant="primary"
              size="md"
              loading={oauthBusy}
              disabled={checking}
              onClick={() => void data.startOAuth(server)}
            >Войти</Button>
          )}
          <Button variant="ghost" size="md" disabled={checking} onClick={() => void data.probe(server)}>
            Проверить
          </Button>
          <IconButton size="lg" title="Изменить" onClick={onEdit} disabled={checking}>
            <Pencil size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </IconButton>
          {/* У наследства крестика нет: запись пришла из общего конфига, и её удаление
              здесь не убрало бы сам сервер — кнопка обещала бы то, чего продукт не делает */}
          {!legacy && (
            <IconButton size="lg" tone="danger" title="Удалить" onClick={onDelete} disabled={checking}>
              <X size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </IconButton>
          )}
        </div>
      </div>

      {canLogin && oauthBusy && (
        <ManualOAuthCode server={server} data={data} />
      )}
    </div>
  );
}

// Плашка ревизии каталожной записи (волна 2): три варианта по серьёзности.
// deprecated/deleted — точно отзыв, тон warning (как у unsupportedReason в каталоге);
// hasNewer — информационная, нейтральный тон; общий отказ батча (checkFailed) — НЕ
// отзыв, нейтрально-приглушённый, чтобы при лежащем реестре не выглядело как «беда»
// и человек не выключил рабочий сервер.
function CatalogRevisionNote({ revision, checkFailed }: {
  revision: McpCatalogRevision | undefined;
  checkFailed: boolean;
}) {
  let tone: 'warn' | 'info' | 'muted' = 'muted';
  let text: string | null = null;
  if (revision?.status === 'deprecated') {
    tone = 'warn';
    text = 'Автор пометил сервер устаревшим в реестре. Работает как раньше, но новые подключения лучше не делать.';
  } else if (revision?.status === 'deleted') {
    tone = 'warn';
    text = 'Сервер удалён из реестра. Лучше выключить и завести замену вручную.';
  } else if (revision?.hasNewer) {
    tone = 'info';
    text = revision.latestVersion
      ? `В реестре вышла новее: ${revision.latestVersion}. Можно обновить вручную.`
      : 'В реестре вышла новее. Можно обновить вручную.';
  } else if (checkFailed) {
    tone = 'muted';
    text = 'Не удалось проверить состояние в реестре. Это не значит, что сервер сломан.';
  }
  if (!text) return null;

  const skin = tone === 'warn'
    ? { background: C.warningBg, color: C.warningText }
    : tone === 'info'
      ? { background: C.bgInset, color: C.textSecondary }
      : { background: 'transparent', color: C.textMuted, border: `1px dashed ${C.dashed}` };

  return (
    <div style={{
      fontSize: FS.xs, lineHeight: 1.45, padding: '6px 10px',
      borderRadius: R.md, ...skin,
    }}>{text}</div>
  );
}

// Запасной путь входа: часть серверов принимает только http://127.0.0.1:PORT/… и до
// нашего callback код не доезжает — окно провайдера открыто, но повисает на чужом адресе.
// Свёрнуто по умолчанию: 95% входов закрываются сами через postMessage, поле — не для них.
function ManualOAuthCode({ server, data }: { server: McpServer; data: McpData }) {
  const [open, setOpen] = useState(false);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);

  if (!open) {
    return (
      <button
        type="button"
        onClick={() => setOpen(true)}
        style={{
          alignSelf: 'flex-start', font: 'inherit', fontSize: FS.xs, color: C.textMuted,
          background: 'transparent', border: 'none', padding: 0, cursor: 'pointer', textDecoration: 'underline',
        }}
      >Окно не вернулось само? Вставить код вручную</button>
    );
  }

  const submit = async () => {
    if (!code.trim() || busy) return;
    setBusy(true);
    try {
      // На отказе оставляем поле открытым с введённым кодом — ошибка уже видна строкой
      // выше (oauthNotice), а закрытие формы читалось бы как «получилось»
      if (await data.completeOAuth(server, code)) { setCode(''); setOpen(false); }
    } finally { setBusy(false); }
  };

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <TextField value={code} onChange={setCode} mono placeholder="Код из адресной строки окна входа" onEnter={submit} />
      </div>
      <Button variant="ghost" size="md" disabled={!code.trim() || busy} loading={busy} onClick={() => void submit()}>
        Завершить
      </Button>
    </div>
  );
}

// Компактная плитка наблюдаемого сервера: только статус, управления нет.
// title — человекочитаемое имя, subtitle — технический ключ (когда они различаются).
function Tile({ tile, title, subtitle, badge }: {
  tile: McpBuiltinServer;
  title: string;
  subtitle?: string;
  badge?: string;
}) {
  const tone = mcpStatusTone(tile.status?.status);
  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
      padding: `9px ${SP.md - 1}px`, display: 'flex', flexDirection: 'column',
      gap: SP.xxs, minWidth: 0,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
        {/* Заголовок обрезается многоточием — в тултипе полное имя и технический ключ */}
        <span title={subtitle && subtitle !== title ? `${title} · ${subtitle}` : title} style={{
          fontSize: FS.sm, fontWeight: 600, color: C.textHeading, flex: 1,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{title}</span>
        {badge && <Badge tone="outline">{badge}</Badge>}
      </div>
      {subtitle && subtitle !== title && (
        <div style={{
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{subtitle}</div>
      )}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.xs, color: C.textSecondary }}>
        <Dot color={tone.dot} size={6} />
        {tone.label}
      </div>
    </div>
  );
}

// Метка на карточке и плитке. «Свой» и «через интернет» стоят почти рядом и не должны
// сливаться: разводим их формой, а не цветом (заливка против контура) — так разница
// читается и в тёмной теме, и новых токенов не нужно
function Badge({ tone, children }: { tone: 'own' | 'legacy' | 'outline'; children: string }) {
  const skin = tone === 'legacy'
    ? { background: C.warningBg, color: C.warningText }
    : tone === 'outline'
      ? { background: 'transparent', border: `1px solid ${C.border}`, color: C.textMuted }
      : { background: C.bgSelected, color: C.textSecondary };
  return (
    <span style={{
      fontSize: FS.xs, fontWeight: 700, padding: '1px 6px', borderRadius: R.pill,
      whiteSpace: 'nowrap', flexShrink: 0, ...skin,
    }}>{children}</span>
  );
}
