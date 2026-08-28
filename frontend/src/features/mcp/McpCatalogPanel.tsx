import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { ArrowLeft, Ban, Box, Cloud, Monitor, RefreshCw, Search, X } from 'lucide-react';
import { api } from '../../lib/api';
import { Button, EmptyState, IconButton } from '../../components/ui';
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

export function McpCatalogPanel({ installedNames, onPick, onManual, onClose }: {
  // Имена уже подключённых каталожных серверов (по CatalogRef.name). Сверка по
  // name, а не по key — у каталожной записи ключ подбирает бэкенд из имени и
  // slug'а, а человек в карточке каталога видит именно реестровое имя. Если
  // такой сервер уже есть — карточка красится бейджем «Уже добавлен», а кнопка
  // «Настроить подключение» всё равно открывает форму, чтобы можно было дойти
  // до правки ключа/секрета (план §4)
  installedNames?: ReadonlySet<string>;
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
  // Серверы из каталога: грузим с бэка (волна 1, задача 9fa075ec). Три состояния
  // жёстко разделены: loading (скелетоны), error (плашка с «Повторить»), loaded.
  // Реестр в preview может лежать — это НЕ блокирует раздел: ручной путь «Добавить
  // вручную» всегда рядом
  const [servers, setServers] = useState<McpCatalogServer[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = () => {
    setServers(null);
    setError(null);
    api.mcp.catalogSearch('')
      .then(res => {
        setServers(res.items ?? []);
        if (res.error) setError(res.error);
      })
      .catch(e => setError(e instanceof Error && e.message ? e.message : 'Не удалось загрузить каталог'));
  };

  useEffect(() => { load(); }, []);

  const filtered = useMemo(() => {
    if (!servers) return [];
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

      <SearchField value={q} onChange={setQ} loading={servers === null} />

      {servers === null && !error ? (
        <CatalogSkeletons />
      ) : error ? (
        <EmptyState
          compact
          icon={<RefreshCw size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Каталог недоступен"
          subtitle={error}
          action={
            <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
              <Button variant="primary" size="sm" onClick={load}>Повторить</Button>
              <Button variant="ghost" size="sm" onClick={onManual}>Добавить вручную</Button>
            </div>
          }
        />
      ) : filtered.length === 0 ? (
        <EmptyState
          compact
          icon={<Search size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title={q.trim() ? `По запросу «${q.trim()}» ничего не нашлось` : 'В каталоге пока пусто'}
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
              installed={!!installedNames?.has(s.name)}
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

// Скелетоны каталожных карточек: пять «костей», имитирующих размер заполненной карточки.
// Пульсирующий фон через CSS-анимацию; короче простой @keyframes на шимминг не нужен —
// достаточно чуть приглушённой заливки, и взгляд понимает «грузятся»
function CatalogSkeletons() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }} aria-hidden>
      {Array.from({ length: 5 }).map((_, i) => (
        <div key={i} style={{
          background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
          padding: '11px 13px', display: 'flex', flexDirection: 'column', gap: SP.xs,
          opacity: 0.55,
        }}>
          <div style={{ display: 'flex', gap: 8, alignItems: 'baseline' }}>
            <div style={{ width: 130, height: 13, borderRadius: R.sm, background: C.bgInset }} />
            <div style={{ marginLeft: 'auto', width: 70, height: 13, borderRadius: R.sm, background: C.bgInset }} />
          </div>
          <div style={{ width: '90%', height: 10, borderRadius: R.sm, background: C.bgInset }} />
          <div style={{ width: '60%', height: 10, borderRadius: R.sm, background: C.bgInset }} />
          <div style={{ width: '40%', height: 9, borderRadius: R.sm, background: C.bgInset }} />
        </div>
      ))}
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

function CatalogCard({ server, env, installed, isLocalStdioWarning, onPick }: {
  server: McpCatalogServer;
  env: 'local' | 'container' | null;
  // Сервер с таким реестровым именем уже подключён (есть в McpServerDto.catalogRef.name).
  // Кнопка «Настроить подключение» остаётся — через неё открывается правка ключа/секрета
  installed: boolean;
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
        {/* Длинные имена (в реестре встречаются записи вида io.github.<id>) обрезаются
            многоточием: без minWidth:0 имя выдавило бы бейджи в новую строку и карточка
            «прыгала» по высоте. Полное имя в title — посмотреть можно, наведя курсор */}
        <span title={server.displayName} style={{
          fontSize: FS.md, fontWeight: 600,
          color: blocked ? C.textMuted : C.textHeading,
          minWidth: 0, flex: '1 1 auto',
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{server.displayName}</span>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
          {installed && (
            <span style={badgeStyle('ok')}>Уже добавлен</span>
          )}
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

// Поле поиска с иконкой и плейсхолдером из макета. На мобиле стереть запрос иначе
// стоит девяти нажатий — крестик справа всегда под рукой, появляется только когда
// поле не пустое (визуальный шум в покое не нужен)
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

  const clear = () => {
    onChange('');
    inputRef.current?.focus();
  };

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
      {value.length > 0 && !loading && (
        <IconButton size="sm" title="Очистить" onClick={clear}>
          <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      )}
    </div>
  );
}

function badgeStyle(tone: 'neutral' | 'warn' | 'ok'): CSSProperties {
  if (tone === 'warn') {
    return {
      display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap',
      fontSize: FS.xs, fontWeight: 600, lineHeight: 1.4, padding: '3px 8px',
      borderRadius: R.max, background: C.warningBg, color: C.warningText,
    };
  }
  if (tone === 'ok') {
    return {
      display: 'inline-flex', alignItems: 'center', gap: SP.xs, whiteSpace: 'nowrap',
      fontSize: FS.xs, fontWeight: 600, lineHeight: 1.4, padding: '3px 8px',
      borderRadius: R.max, background: C.successBg, color: C.successText,
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

// === Чтение ExecutionEnvironment из текущего пользователя. ===
// Сейчас стора для него нет (useMe отдаёт только role и defaultPersonaId), поэтому
// безопасно возвращаем null — карточка просто не покажет бейдж среды. Когда стор
// расширится, тут же появится реальное значение и бейдж оживёт без правок компонента
function readExecEnv(): 'local' | 'container' | null {
  return null;
}
