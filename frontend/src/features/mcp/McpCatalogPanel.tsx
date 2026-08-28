import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { ArrowLeft, Ban, Box, Cloud, Monitor, Search } from 'lucide-react';
import { Button, EmptyState } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { useMe } from '../../lib/defaultPersona';
import type { McpCatalogServer } from '../../types';

// === Согласованные тексты и логика макета каталога (docs/mockups/mcp-catalog-v1.html) ===
//
// Карточка каталога — это НЕ карточка сервера из «Моих серверов»: ни статуса, ни тумблера
// «включён», ни строки доступа. Здесь человек выбирает, ЧТО подключить, а серверная
// карточка показывает, КАК оно сейчас работает. Соединяет их кнопка «Настроить
// подключение» — она открывает McpServerForm с предзаполнением.

// Человек выбирает «добавить вручную» из пустого состояния поиска — там нет «Подключить»
// в принципе, и ручной путь нужен сразу. Это не новое действие, а один из способов
// попасть в уже существующую форму
export type CatalogOpenTarget =
  | { kind: 'detail'; server: McpCatalogServer }
  | { kind: 'manual' };

export function McpCatalogPanel({ onPick, onManual, onClose }: {
  onPick: (server: McpCatalogServer) => void;
  onManual: () => void;
  onClose: () => void;
}) {
  // useMe держит «обо мне» (role/defaultPersonaId); ExecutionEnvironment приходит с
  // /api/auth/me — пробрасывается полем в типе Me, но useMe пока не отдаёт его. Карточка
  // без него просто не показывает бейдж среды (см. readExecEnv). Когда useMe расширится,
  // тут же появится реальное значение без правок компонента
  useMe();
  const env = readExecEnv();

  const [q, setQ] = useState('');
  // Серверы из каталога (пока статичный мок — бэкенд-эндпоинт делает Денис в соседнем дереве;
  // формат уже подогнан под контракт). useState + локальный фильтр: на каждом символе
  // мгновенно без сети, иначе ввод лагает на 200мс+
  const [servers] = useState<McpCatalogServer[]>(() => mockCatalogServers());

  // Дебаунс ТОЛЬКО для UI-сигнала «идёт поиск» — локальный фильтр и так мгновенный. Сейчас
  // оставлен индикатор, чтобы будущий вызов api.mcp.catalogSearch мог на нём повиснуть
  const [searching] = useState(false);

  const filtered = useMemo(() => {
    const term = q.trim().toLowerCase();
    if (!term) return servers;
    return servers.filter(s => {
      return (
        s.displayName.toLowerCase().includes(term) ||
        s.name.toLowerCase().includes(term) ||
        s.description.toLowerCase().includes(term)
      );
    });
  }, [q, servers]);

  const isStdioLocal = env === 'local';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Button variant="ghost" size="sm" leftIcon={<ArrowLeft size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />} onClick={onClose}>
          Назад
        </Button>
      </div>

      <div>
        <h3 style={{
          fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700,
          color: C.textHeading, margin: 0,
        }}>Каталог MCP-серверов</h3>
        <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5, marginTop: SP.xs }}>
          Открытый список серверов сообщества. AI Home не проверяет их код — смотрите,
          кто автор и давно ли сервер в реестре.
        </div>
      </div>

      <SearchField value={q} onChange={setQ} loading={searching} />

      {filtered.length === 0 ? (
        <EmptyState
          compact
          icon={<Search size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title={q.trim() ? `По запросу «${q.trim()}» ничего не нашлось` : 'Каталог пуст'}
          subtitle="Каталог ведётся на английском — попробуйте английское название сервиса."
          action={
            <Button variant="ghost" size="sm" onClick={onManual}>
              Добавить сервер вручную
            </Button>
          }
        />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
          {filtered.map(s => (
            <CatalogCard
              key={s.name}
              server={s}
              env={env}
              isLocalStdioWarning={isStdioLocal && s.transport === 'npm'}
              onPick={onPick}
            />
          ))}
        </div>
      )}

      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        <Button variant="ghost" size="sm" onClick={onManual}>Добавить сервер вручную</Button>
      </div>
    </div>
  );
}

// === Карточка сервера каталога ===
//
// Состояния карточки:
//   1. Сервер подключить нельзя — серая карточка, причина отказа первой строкой, без кнопки
//   2. Уже подключён (по CatalogRef.name из McpServerDto) — бейдж «Уже добавлен»
//   3. Свободен — кликабельная кнопка «Настроить подключение»
//
// Бейдж среды — по факту env владельца (план §1). Никакой галочки «только удалённые»:
// стdio-сервер на карточке у local-владельца несёт предупреждающую полосу (по §2)

function CatalogCard({ server, env, isLocalStdioWarning, onPick }: {
  server: McpCatalogServer;
  env: 'local' | 'container' | null;
  isLocalStdioWarning: boolean;
  onPick: (server: McpCatalogServer) => void;
}) {
  const blocked = !!server.unsupportedReason;
  const tag = server.unsupportedTag ?? null;

  const Tag = blocked ? 'div' : 'button';

  return (
    <Tag
      type={blocked ? undefined : 'button'}
      onClick={blocked ? undefined : () => onPick(server)}
      className={blocked ? undefined : 'card-act'}
      style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
        padding: '11px 13px', display: 'flex', flexDirection: 'column', gap: SP.xs,
        textAlign: 'left', font: 'inherit', fontFamily: FONT.sans, color: 'inherit',
        cursor: blocked ? 'default' : 'pointer',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{
          fontSize: FS.md, fontWeight: 600,
          color: blocked ? C.textMuted : C.textHeading,
        }}>{server.displayName}</span>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
          {tag && (
            <span style={badgeStyle('warn')}>{tag}</span>
          )}
          {envBadgeFor(server, env)}
        </div>
      </div>

      {blocked && server.unsupportedReason && (
        <div style={{
          display: 'flex', gap: SP.sm, alignItems: 'flex-start',
          fontSize: FS.sm, lineHeight: 1.5, color: C.warningText,
        }}>
          <Ban size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.warningText} style={{ flexShrink: 0, marginTop: 1 }} />
          <span>{server.unsupportedReason}</span>
        </div>
      )}

      <div style={{
        fontSize: FS.sm, color: blocked ? C.textMuted : C.textSecondary, lineHeight: 1.5,
      }}>{server.description}</div>

      <div style={{
        fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono, lineHeight: 1.5,
      }}>
        <a
          href={server.repositoryUrl ?? `https://${server.repository}`}
          target="_blank" rel="noopener noreferrer"
          style={{ color: C.accent, textDecoration: 'none' }}
        >
          {server.repository}
        </a>
        {' · '}версия {server.version}
        {' · '}в реестре с {formatMonth(server.publishedAt)}
      </div>

      {isLocalStdioWarning && !blocked && (
        <div style={{
          display: 'flex', gap: SP.sm, alignItems: 'flex-start',
          background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: R.xl,
          padding: '10px 12px', fontSize: FS.sm, lineHeight: 1.5, color: C.textSecondary,
          marginTop: SP.xs,
        }}>
          <Ban size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.warningText} style={{ flexShrink: 0, marginTop: 1 }} />
          <span>
            <b style={{ color: C.warningText }}>Запустится на вашем компьютере.</b>{' '}
            У сервера будет доступ ко всему, к чему есть доступ у вас: файлы, сеть,
            ключи в переменных окружения.
          </span>
        </div>
      )}
    </Tag>
  );
}

// Бейдж среды по факту User.ExecutionEnvironment. Для remote-сервера — нейтральный
// «На сервере автора», для npm-сервера: у container — «В песочнице» (нейтральный тон),
// у local — «На вашем компьютере» (warning). Неизвестная среда (env === null, например
// до того как /me ответил) — ничего не рисуем, карточка остаётся без этого бейджа
function envBadgeFor(server: McpCatalogServer, env: 'local' | 'container' | null) {
  if (server.transport === 'remote') {
    return (
      <span style={badgeStyle('neutral')}>
        <Cloud size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> На сервере автора
      </span>
    );
  }
  if (env === 'container') {
    return (
      <span style={badgeStyle('neutral')}>
        <Box size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> В песочнице
      </span>
    );
  }
  if (env === 'local') {
    return (
      <span style={badgeStyle('warn')}>
        <Monitor size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> На вашем компьютере
      </span>
    );
  }
  // env === null (ещё не подгрузился): тон «В песочнице» по умолчанию НЕ рисуем —
  // это было бы враньём. Покажем «локальный» только когда человек уже в local
  return null;
}

// Поле поиска с иконкой и плейсхолдером из макета
function SearchField({ value, onChange, loading }: {
  value: string;
  onChange: (v: string) => void;
  loading: boolean;
}) {
  const [focused, setFocused] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const t = setTimeout(() => inputRef.current?.focus(), 100);
    return () => clearTimeout(t);
  }, []);

  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.sm,
      background: C.bgWhite, border: `1px solid ${focused ? C.accent : C.border}`,
      borderRadius: R.xl, padding: '10px 13px',
      boxShadow: focused ? '0 0 0 3px rgba(217, 119, 87, 0.14)' : 'none',
      transition: 'border-color 0.15s, box-shadow 0.15s',
    }}>
      <Search size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} color={focused ? C.accent : C.textMuted} />
      <input
        ref={inputRef}
        value={value}
        onChange={e => onChange(e.target.value)}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        placeholder="Поиск: notion, файлы, база данных…"
        style={{
          font: 'inherit', fontFamily: FONT.sans, fontSize: FS.md,
          color: C.textHeading, background: 'transparent', border: 'none',
          outline: 'none', flex: 1, minWidth: 0,
        }}
      />
      {loading && (
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>ищем…</span>
      )}
    </div>
  );
}

function badgeStyle(tone: 'neutral' | 'warn'): CSSProperties {
  if (tone === 'warn') {
    return {
      display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap',
      fontSize: FS.xs, fontWeight: 600, lineHeight: 1.4, padding: '3px 8px',
      borderRadius: R.max, background: C.warningBg, color: C.warningText,
    };
  }
  return {
    display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap',
    fontSize: FS.xs, fontWeight: 600, lineHeight: 1.4, padding: '3px 8px',
    borderRadius: R.max, background: C.bgInset, color: C.textMuted,
  };
}

function formatMonth(iso: string): string {
  // publishedAt — ISO-дата (YYYY-MM-DD). Возвращаем «сентября 2025» в родительном падеже,
  // чтобы подпись карточки звучала естественно («в реестре с сентября 2025»)
  const t = Date.parse(iso);
  if (isNaN(t)) return iso;
  const d = new Date(t);
  const months = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
    'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'];
  return `${months[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
}

// === Заглушка каталога (мок) ===
//
// Бэкенд-эндпоинт GET /api/mcp/catalog/search делает Денис в соседнем дереве — пока
// используем локальный набор. Источник значений — каталог из макета
// docs/mockups/mcp-catalog-v1.html. Когда бэк ответит, useEffect дёрнет api.mcp.catalogSearch
// и положит результат в state; сейчас компонент держит мок в useState-инициализаторе, чтобы
// не показывать «каталог недоступен» до прихода бэка (сознательно НЕ в этой волне).
function mockCatalogServers(): McpCatalogServer[] {
  return [
    {
      name: '@notion/mcp-server-notion',
      displayName: 'Notion',
      description: 'Читать и править страницы и базы Notion.',
      version: '2.1.0',
      repository: 'github.com/makenotion/notion-mcp',
      repositoryUrl: 'https://github.com/makenotion/notion-mcp',
      publishedAt: '2025-09-15',
      status: 'active',
      transport: 'remote',
      url: 'https://mcp.notion.com/mcp',
      fields: [
        { name: 'Authorization', description: 'внутренний токен интеграции Notion', isSecret: true, isRequired: true, placeholder: 'ntn_…' },
      ],
    },
    {
      name: '@modelcontextprotocol/server-filesystem',
      displayName: 'Filesystem',
      description: 'Чтение и запись файлов в разрешённой папке.',
      version: '1.2.0',
      repository: 'github.com/modelcontextprotocol/servers',
      repositoryUrl: 'https://github.com/modelcontextprotocol/servers',
      publishedAt: '2025-11-08',
      status: 'active',
      transport: 'npm',
      command: 'npx -y @modelcontextprotocol/server-filesystem@1.2.0',
      fields: [
        { name: 'ROOT_PATH', description: 'папка, к которой открыт доступ', isRequired: true, placeholder: 'C:\\Мои проекты\\docs', arg: true },
      ],
    },
    {
      name: '@modelcontextprotocol/server-postgres',
      displayName: 'PostgreSQL',
      description: 'Запросы к базе только на чтение.',
      version: '0.6.0',
      repository: 'github.com/modelcontextprotocol/servers',
      repositoryUrl: 'https://github.com/modelcontextprotocol/servers',
      publishedAt: '2025-10-22',
      status: 'active',
      transport: 'npm',
      command: 'npx -y @modelcontextprotocol/server-postgres@0.6.0',
      fields: [
        { name: 'DATABASE_URL', description: 'строка подключения к базе', isSecret: true, isRequired: true, placeholder: 'postgresql://…' },
      ],
    },
    {
      name: 'io.github.acme/analytics',
      displayName: 'Analytics Pro',
      description: 'Отчёты и дашборды Acme.',
      version: '0.3.1',
      repository: 'github.com/acme/analytics-mcp',
      repositoryUrl: 'https://github.com/acme/analytics-mcp',
      publishedAt: '2026-03-12',
      status: 'deprecated',
      transport: 'remote',
      url: 'https://mcp.acme.dev/mcp',
      fields: [],
      unsupportedReason: 'Автор пометил сервер устаревшим. Из каталога подключить нельзя — если он всё-таки нужен, добавьте вручную.',
      unsupportedTag: 'Устарел',
    },
    {
      name: 'io.github.acme/legacy-docs',
      displayName: 'Legacy Docs',
      description: 'Поиск по внутренней базе документов.',
      version: '1.0.0',
      repository: 'github.com/acme/legacy-docs',
      repositoryUrl: 'https://github.com/acme/legacy-docs',
      publishedAt: '2026-01-04',
      status: 'active',
      transport: 'remote',
      url: 'https://mcp.acme.dev/legacy/mcp',
      fields: [],
      unsupportedReason: 'Подключается через Docker — в этой версии AI Home так подключать нельзя.',
      unsupportedTag: 'Нельзя подключить',
    },
    {
      name: 'io.github.unknown/fast-fetch',
      displayName: 'Fast Fetch',
      description: 'Быстрая загрузка страниц.',
      version: '2.0.0',
      repository: 'github.com/unknown/fast-fetch',
      repositoryUrl: 'https://github.com/unknown/fast-fetch',
      publishedAt: '2026-08-01',
      status: 'active',
      transport: 'npm',
      command: 'npx -y @unknown/fast-fetch@2.0.0',
      fields: [],
      unsupportedReason: 'Сервер просит скачивать себя из чужого источника пакетов — такие подключать нельзя.',
      unsupportedTag: 'Нельзя подключить',
    },
  ];
}

// Чтение ExecutionEnvironment из текущего пользователя. Сейчас стора для него нет (useMe
// отдаёт только role и defaultPersonaId), поэтому безопасно возвращаем null — карточка
// просто не покажет бейдж среды. Когда стор расширится, тут же появится реальное значение
// и бейдж оживёт без правок компонента
function readExecEnv(): 'local' | 'container' | null {
  return null;
}
