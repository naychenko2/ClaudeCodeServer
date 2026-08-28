import { useCallback, useEffect, useRef, useState } from 'react';
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
  // useMe держит «обо мне» (role/defaultPersonaId/executionEnvironment). Среда из
  // /api/auth/me пробрасывается в стор в defaultPersona.ts: карточка без неё просто
  // не показала бы бейдж среды и предупреждающую полосу (отказ от честной пометки
  // был бы нарушением договорённости с владельцем — без неё человек не видит, что
  // stdio-сервер запустится на его машине)
  const env = useMe().executionEnvironment;

  const [q, setQ] = useState('');
  // Серверы из каталога: ищет БЭКЕНД по параметру q (волна 1, задача 9fa075ec).
  // Локальной фильтрации здесь нет и быть не может: реестр отдаёт результат
  // страницами, и подходящей записи на первой странице может не быть вовсе —
  // фильтр по загруженному массиву врал «ничего не нашлось» при живых данных
  // (QA: «filesystem» — 14 записей в реестре, ни одной на первой странице).
  // Три состояния жёстко разделены: loading (скелетоны, servers === null),
  // error (плашка с «Повторить»), loaded. Реестр в preview может лежать — это НЕ
  // блокирует раздел: ручной путь «Добавить вручную» всегда рядом
  const [servers, setServers] = useState<McpCatalogServer[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Следующая страница реестра: пока курсор есть — под списком живёт «Показать ещё».
  // Молча обрывать выдачу на первой странице нельзя: человек не отличит «это весь
  // каталог» от «дальше есть, но мы не показали»
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  // Итог догрузки для человека: отказ («не удалось») или пояснение, почему список
  // не вырос. Отдельно от error поиска — уже показанный список сносить плашкой
  // «Каталог недоступен» нельзя
  const [moreNote, setMoreNote] = useState<{ text: string; isError: boolean } | null>(null);
  // Порядковый номер поиска: человек печатает быстрее, чем отвечает реестр, и без
  // счётчика ответ по «not» перезаписал бы выдачу по «notion». Тот же приём, что
  // в useMcpData (saveSeq) и «Поставщиках моделей»
  const searchSeq = useRef(0);

  const runSearch = useCallback((term: string) => {
    const seq = ++searchSeq.current;
    setServers(null);
    setNextCursor(null);
    setError(null);
    setMoreNote(null);
    api.mcp.catalogSearch(term)
      .then(res => {
        if (searchSeq.current !== seq) return;
        setServers(res.items ?? []);
        setNextCursor(res.nextCursor ?? null);
        if (res.error) setError(res.error);
      })
      .catch(e => {
        if (searchSeq.current !== seq) return;
        setError(e instanceof Error && e.message ? e.message : 'Не удалось загрузить каталог');
      });
  }, []);

  // Первый заход грузит витрину сразу, каждый следующий ввод ждёт 350 мс: у реестра
  // на бэке стоит потолок запросов, и печать по букве молотила бы внешний сервис
  const firstRun = useRef(true);
  useEffect(() => {
    const term = q.trim();
    if (firstRun.current) {
      firstRun.current = false;
      runSearch(term);
      return;
    }
    const t = setTimeout(() => runSearch(term), 350);
    return () => clearTimeout(t);
  }, [q, runSearch]);

  // Догрузка страницы принадлежит ТЕКУЩЕМУ поиску: счётчик не двигаем, а сверяем —
  // если запрос успел смениться, доехавшая страница чужой выдачи выбрасывается.
  //
  // Листаем до первой НОВОЙ записи, а не ровно одну страницу: реестр пагинирует по
  // ВЕРСИЯМ (двадцать релизов одного сервера — двадцать записей и двадцать курсоров),
  // а бэкенд дедупит их по имени. Поэтому честная следующая страница нередко приносит
  // ноль новых имён, и «Показать ещё» без цикла выглядел бы как мёртвая кнопка.
  // Потолок в пять страниц — чтобы один клик не пролистал полреестра
  const MORE_PAGES = 5;
  const loadMore = async () => {
    if (!nextCursor || loadingMore) return;
    const seq = searchSeq.current;
    setLoadingMore(true);
    setMoreNote(null);
    // Известные имена собираем ДО цикла и ведём сами: считать добавленное внутри
    // updater'а нельзя — React зовёт его дважды в StrictMode
    const seen = new Set((servers ?? []).map(s => s.name));
    let cursor: string | null = nextCursor;
    let added = 0;
    try {
      for (let page = 0; page < MORE_PAGES && cursor && added === 0; page++) {
        const res = await api.mcp.catalogSearch(q.trim(), cursor);
        if (searchSeq.current !== seq) return;
        cursor = res.nextCursor ?? null;
        const fresh = (res.items ?? []).filter(s => !seen.has(s.name));
        for (const s of fresh) seen.add(s.name);
        if (fresh.length > 0) {
          added += fresh.length;
          setServers(prev => [...(prev ?? []), ...fresh]);
        }
      }
      setNextCursor(cursor);
      // Ничего не добавилось — говорим об этом вслух: молчащая кнопка читается как поломка
      if (added === 0) {
        setMoreNote(cursor
          ? { text: 'Дальше в реестре идут другие версии тех же серверов — уточните запрос', isError: false }
          : { text: 'Это всё, что нашлось в реестре', isError: false });
      }
    } catch (e) {
      if (searchSeq.current !== seq) return;
      setMoreNote({
        text: e instanceof Error && e.message ? e.message : 'Не удалось загрузить ещё',
        isError: true,
      });
    } finally {
      if (searchSeq.current === seq) setLoadingMore(false);
    }
  };

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

      {/* Порядок ветвей: сперва отказ (плашка с «Повторить»), потом ожидание
          (скелетоны), потом пустая выдача. «Ничего не нашлось» показывается ТОЛЬКО
          по фактическому пустому ответу сервера — на ошибке и на ожидании там
          соответственно плашка и кости */}
      {error ? (
        <EmptyState
          compact
          icon={<RefreshCw size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Каталог недоступен"
          subtitle={error}
          action={
            <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', justifyContent: 'center' }}>
              <Button variant="primary" size="sm" onClick={() => runSearch(q.trim())}>Повторить</Button>
              <Button variant="ghost" size="sm" onClick={onManual}>Добавить вручную</Button>
            </div>
          }
        />
      ) : servers === null ? (
        <CatalogSkeletons />
      ) : servers.length === 0 ? (
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
          {servers.map(s => (
            <CatalogCard
              key={s.name}
              server={s}
              env={env}
              installed={!!installedNames?.has(s.name)}
              isLocalStdioWarning={isStdioLocal && s.prefill?.transport === 'stdio'}
              onPick={onPick}
            />
          ))}
          {/* Подпись живёт и без кнопки: когда курсор иссяк, «Показать ещё» уходит,
              а «это всё, что нашлось» человеку по-прежнему нужно прочитать */}
          {(nextCursor || moreNote) && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, alignItems: 'center' }}>
              {nextCursor && (
                <Button variant="ghost" size="sm" loading={loadingMore} onClick={() => void loadMore()}>
                  Показать ещё
                </Button>
              )}
              {moreNote && (
                <div style={{
                  fontSize: FS.xs, textAlign: 'center',
                  color: moreNote.isError ? C.warningText : C.textMuted,
                }}>{moreNote.text}</div>
              )}
            </div>
          )}
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
  // Connectable=false у DTO — карточка без кнопки. Причина отказа — в notice.
  // Дополнительно рисуем бейдж «Устарел» если сервер в реестре deprecated: первая
  // буква причины не видна на превью, а тон «deprecated» нужно показать явно
  const blocked = !server.connectable;
  const reason = server.notice ?? null;
  const isDeprecated = server.status === 'deprecated';
  // Заголовок карточки: title из реестра (человеческое имя) или name как фолбэк.
  // По макету docs/mockups/mcp-catalog-v1.html имя сервера — первое, что человек читает
  const title = server.title ?? server.name;

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
            «прыгала» по высоте. Полное имя в title атрибуте — посмотреть можно, наведя курсор */}
        <span title={title} style={{
          fontSize: FS.md, fontWeight: 600,
          color: blocked ? C.textMuted : C.textHeading,
          minWidth: 0, flex: '1 1 auto',
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{title}</span>
        <div style={{ marginLeft: 'auto', display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
          {installed && (
            <span style={badgeStyle('ok')}>Уже добавлен</span>
          )}
          {isDeprecated && (
            <span style={badgeStyle('warn')}>Устарел</span>
          )}
          {envBadgeFor(server, env)}
        </div>
      </div>

      {blocked && reason && (
        <div style={{
          display: 'flex', gap: SP.sm, alignItems: 'flex-start',
          fontSize: FS.sm, lineHeight: 1.5, color: C.warningText,
        }}>
          <Ban size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.warningText} style={{ flexShrink: 0, marginTop: 1 }} />
          <span>{reason}</span>
        </div>
      )}

      {server.description && (
        <div style={{
          fontSize: FS.sm, color: blocked ? C.textMuted : C.textSecondary, lineHeight: 1.5,
        }}>{server.description}</div>
      )}

      {server.prefill?.transport === 'stdio' && (server.prefill.command || server.prefill.args.length > 0) && (() => {
        // Полная строка запуска из черновика каталога: то же, что уйдёт в запись
        // после подстановки плейсхолдеров {name} в форме. На карточке каталога
        // плейсхолдеры видны как есть — сигнал «форма спросит значения» до
        // нажатия «Настроить подключение»
        const parts = [server.prefill.command ?? '', ...server.prefill.args].filter(Boolean);
        const line = parts.join(' ');
        return (
          <div title={line} style={{
            fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.mono, lineHeight: 1.45,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{line}</div>
        );
      })()}

      <div style={{
        fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono, lineHeight: 1.5,
      }}>
        {server.repositoryUrl && (
          <>
            <a
              href={server.repositoryUrl}
              target="_blank" rel="noopener noreferrer"
              style={{ color: C.accent, textDecoration: 'none' }}
            >
              {repoDisplay(server.repositoryUrl)}
            </a>
            {' · '}
          </>
        )}
        версия {server.version ?? '—'}
        {server.publishedAt && ` · в реестре с ${formatMonth(server.publishedAt)}`}
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

// Из полного URL репозитория вырезаем хост + путь без схемы (для UI). Реестр
// присылает адреса вида https://github.com/... — нам нужен короткий «github.com/...»
// в моноширинной подписи, чтобы строка не разъезжалась по ширине карточки
function repoDisplay(url: string): string {
  return url.replace(/^https?:\/\//, '').replace(/\.git$/, '');
}

// Бейдж среды по факту User.ExecutionEnvironment. Для http-сервера — нейтральный
// «На сервере автора», для stdio-сервера: у container — «В песочнице» (нейтральный тон),
// у local — «На вашем компьютере» (warning). Неизвестная среда (env === null, например
// до того как /me ответил) — ничего не рисуем, карточка остаётся без этого бейджа.
// Transport живёт в prefill: у Connectable=false (prefill=null) бейдж не рисуем
function envBadgeFor(server: McpCatalogServer, env: 'local' | 'container' | null) {
  const transport = server.prefill?.transport;
  if (transport === 'http') {
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

// Старая заглушка readExecEnv удалена — executionEnvironment едет через useMe().
// Заглушка жила ровно один релиз и выпилена вместе с добавлением поля в стор
