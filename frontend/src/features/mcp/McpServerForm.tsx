import { useState } from 'react';
import type { CSSProperties } from 'react';
import { Check, Copy, Eye, EyeOff, X } from 'lucide-react';
import { Button, Field, IconButton, InlineSegmented, TextArea, TextField, Toggle } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { slugify } from '../../lib/slug';
import type { McpData } from './useMcpData';
import type { McpServer, McpServerCatalogDraft, McpValueInput } from '../../types';

// Вкладка «Добавить» (она же форма правки существующей записи): stdio / http /
// вставка готового JSON-фрагмента. Секретные значения после сохранения не приходят
// с бэка вовсе — в правке вместо них отметка «задано», а пустое поле секрета означает
// «оставить как было».

type Mode = 'stdio' | 'http' | 'json';

interface Pair {
  name: string;
  value: string;
  secret: boolean;
  // Значение уже лежит в защищённом хранилище: поле пустое и заблокировано
  stored: boolean;
  // Человеческая подпись поля, приехавшая из каталога. Только в сессии импорта
  // (план §7): в запись не кладётся, в правке не показывается. Нужна ТОЛЬКО на
  // предзаполнении — без неё обязательное поле без дефолта приезжает пустым и
  // безымянным, а проба падает без объяснения
  description?: string | null;
  // Обязательное для запуска (без значения по умолчанию). Аналогично description
  // живёт только в сессии импорта: на форме показывается звёздочкой, в запись не идёт
  isRequired?: boolean;
  // Плейсхолдер из реестра — виден на форме, пока поле пустое
  placeholder?: string | null;
}

const hintStyle: CSSProperties = { fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 };

// Поле с кнопкой «Скопировать» справа: у команды, аргументов и адреса это первое,
// что человек делает с введённой строкой (проверяет её в терминале). Обёртка
// повторяет controlStyle TextField — бордер/фон/фокус рисуются на самом инпуте,
// иконка-кнопка стоит поверх слоя (position: absolute), ширина инпута компенсируется
function TextFieldWithCopy({ value, onChange, placeholder, mono }: {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  mono?: boolean;
}) {
  const [copied, setCopied] = useState(false);
  // value может быть пустым — кнопка тогда задизейблена, чтобы не копировать мусор
  const canCopy = value.trim().length > 0;
  const copy = () => {
    if (!canCopy) return;
    // Запасной путь для небезопасного контекста (http вне localhost) — writeText может
    // бросить. Здесь это не критично: чаще всего работает, и даже если нет — ничего
    // не сломается, человек просто увидит, что галочка не появилась
    try {
      navigator.clipboard?.writeText(value);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard недоступен — без галочки */
    }
  };
  return (
    <div style={{ position: 'relative' }}>
      <TextField
        value={value}
        onChange={onChange}
        placeholder={placeholder}
        mono={mono}
        // Резервируем место под иконку: на мобиле с 320 CSS это всё ещё влезает,
        // поле остаётся шириной ~ 280, что достаточно для команды npm-пакета
        style={{ paddingRight: 40 }}
      />
      <span style={{
        position: 'absolute', right: 6, top: '50%', transform: 'translateY(-50%)',
        display: 'flex',
      }}>
        <IconButton
          size="sm"
          title={copied ? 'Скопировано' : 'Скопировать'}
          disabled={!canCopy}
          onClick={copy}
        >
          {copied
            ? <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.success} />
            : <Copy size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        </IconButton>
      </span>
    </div>
  );
}

export function McpServerForm({ data, server, catalogDraft, onDone, onCancel }: {
  data: McpData;
  server: McpServer | null;      // null — создание
  // Предзаполнение из каталога. Когда задано — форма понимает, что её открыли как
  // продолжение карточки каталога: режим фиксируется (stdio/http по draft.source.prefill.transport),
  // transport заблокирован на переключение, описание и обязательность едут с полями,
  // кнопка сохранения говорит «Сохранить выключенным» с плашкой-объяснением
  catalogDraft?: McpServerCatalogDraft | null;
  onDone: () => void;
  onCancel: () => void;
}) {
  const editing = server !== null;
  // При импорте из каталога — режим зафиксирован по типу сервера. Переключатель stdio/http
  // прячем, чтобы человек не выкинул команду из черновика. JSON-фрагмент из каталога
  // подкидывать незачем — это ручной путь, ему каталог не нужен
  const fromCatalog = !!catalogDraft;
  const [mode, setMode] = useState<Mode>(() => {
    if (catalogDraft) return catalogDraft.source.prefill?.transport === 'http' ? 'http' : 'stdio';
    if (server) return server.transport === 'stdio' ? 'stdio' : 'http';
    return 'stdio';
  });
  const [label, setLabel] = useState(() => {
    if (catalogDraft) return catalogDraft.source.title ?? catalogDraft.source.name;
    return server?.label ?? '';
  });
  const [key, setKey] = useState(() => {
    if (catalogDraft) return slugify(catalogDraft.source.title ?? catalogDraft.source.name, true);
    return server?.key ?? '';
  });
  const [keyTouched, setKeyTouched] = useState(editing || fromCatalog);
  const [command, setCommand] = useState(() => {
    if (catalogDraft) return catalogDraft.source.prefill?.command ?? '';
    return server?.command ?? '';
  });
  const [args, setArgs] = useState(() => {
    if (catalogDraft) {
      // В argv идут только поля с target='args' и непустым default. Бэкенд в DTO
      // кладёт target строго из 'env' | 'header' | 'url' | 'args' — старого флага
      // arg нет, его роль играет target
      const argPairs = catalogDraft.fieldsDraft.filter(f => f.target === 'args' && f.default);
      return argPairs.map(f => quoteIfNeeded(f.default ?? '')).join(' ');
    }
    return (server?.args ?? []).join(' ');
  });
  const [url, setUrl] = useState(() => {
    if (catalogDraft) return catalogDraft.source.prefill?.url ?? '';
    return server?.url ?? '';
  });
  const [env, setEnv] = useState<Pair[]>(() => {
    if (catalogDraft) {
      // env-поля — target='env' или target='url' (URL-плейсхолдер пока в env для совместимости
      // с текущим UI: отдельной формы для шаблонов URL в первой волне нет)
      const envFields = catalogDraft.fieldsDraft.filter(f => f.target === 'env' || f.target === 'url');
      if (envFields.length === 0) return [];
      return envFields.map(f => ({
        name: f.name,
        value: f.default ?? '',
        secret: !!f.secret,
        stored: false,
        description: f.description ?? null,
        isRequired: !!f.required,
        placeholder: null,
      }));
    }
    return toPairs(server?.env);
  });
  const [headers, setHeaders] = useState<Pair[]>(() => {
    if (catalogDraft) {
      // http-поля — target='header'. Имя DTO-поля единственное ('header'), а не 'headers'
      const hdrFields = catalogDraft.fieldsDraft.filter(f => f.target === 'header');
      if (hdrFields.length === 0) return toPairs(server?.headers);
      return hdrFields.map(f => ({
        name: f.name,
        value: f.default ?? '',
        secret: !!f.secret,
        stored: false,
        description: f.description ?? null,
        isRequired: !!f.required,
        placeholder: null,
      }));
    }
    return toPairs(server?.headers);
  });
  // Способ входа — только у http/sse (OAuth ограничен транспортом на бэке). 'headers' —
  // всё как раньше (заголовки вручную, включая apikey/bearer из наследства); переключение
  // на него у записи с oauth2 явно сбрасывает kind — иначе тумблер соврал бы о состоянии
  const initialAuthKind = server?.auth.kind ?? 'none';
  const [authMode, setAuthMode] = useState<'headers' | 'oauth'>(
    initialAuthKind === 'oauth2' ? 'oauth' : 'headers');
  const [oauthClientId, setOauthClientId] = useState(server?.auth.clientId ?? '');
  const [allowReadOnlyPersonas, setAllowReadOnlyPersonas] = useState(server?.allowReadOnlyPersonas ?? false);
  const [json, setJson] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [imported, setImported] = useState<string | null>(null);
  // Ошибка привязана к конкретному полю (волна 2): сейчас ловим «занятый ключ».
  // Сразу две проверки — клиентская по data.servers (мгновенная подсветка без бэка)
  // и серверная (бэк умеет 400 с понятным текстом, мы его парсим по подстроке «ключ»,
  // потому что контракт ошибки бэка — только {error: string}, без поля field)
  const [keyError, setKeyError] = useState<string | null>(null);

  const changeLabel = (v: string) => {
    setLabel(v);
    if (!keyTouched) setKey(slugify(v, true));
  };

  // Клиентская проверка занятости ключа (только для новой записи — у правки свой ключ,
  // и трогать смысла нет). Реагируем на каждое изменение key: пользователь правит,
  // конфликт появился — показываем; конфликт исчез — убираем. Дополнительно чистим
  // при изменении label (slug из label тоже может дать коллизию)
  const trimmedKey = key.trim().toLowerCase();
  const keyConflict = !editing && trimmedKey.length > 0
    && data.servers?.some(s => s.key.toLowerCase() === trimmedKey);
  const liveKeyError = keyConflict ? 'Сервер с таким ключом уже есть' : keyError;
  // Сбрасываем серверную ошибку, как только пользователь начал править ключ:
  // иначе после правки пометка «занято» висела бы, даже если он уже ушёл от коллизии
  const changeKey = (v: string) => {
    setKey(slugify(v, true));
    setKeyTouched(true);
    if (keyError) setKeyError(null);
  };

  // undefined — не трогать авторизацию (наследство apikey/bearer из импорта остаётся как
  // было). Явный kind шлём только когда включаем OAuth либо гасим его — тумблер не должен
  // молчать о смене режима, которую сам же показывает
  const authField = () => {
    if (authMode === 'oauth') return { kind: 'oauth2', clientId: oauthClientId.trim() || null };
    return initialAuthKind === 'oauth2' ? { kind: 'none' } : undefined;
  };

  const submit = async () => {
    setBusy(true);
    setError('');
    setKeyError(null);
    try {
      if (mode === 'json') {
        const parsed = JSON.parse(json) as unknown;
        const result = await data.importJson(parsed);
        const skipped = result.skipped.map(s => `${s.key} — ${s.reason}`).join('; ');
        setImported(`Добавлено серверов: ${result.created.length}${skipped ? `. Пропущено: ${skipped}` : ''}`);
        setJson('');
        return;
      }
      await data.save(server?.id ?? null, {
        key: key.trim(),
        label: label.trim() || key.trim(),
        transport: mode,
        // Каталожная запись идёт с enabled=false принудительно (план §4: импорт
        // безопасной зоной не делает). Для правки enabled НЕ передаём — бэкенд
        // сохраняет прежнее значение. Включает запись человек отдельным действием
        ...(fromCatalog ? { enabled: false, catalogRef: catalogDraft!.catalogRef } : {}),
        allowReadOnlyPersonas,
        ...(mode === 'stdio'
          ? { command: command.trim(), args: splitArgs(args), env: toInputs(env) }
          : { url: url.trim(), headers: toInputs(headers), auth: authField() }),
      });
      onDone();
    } catch (e) {
      // Текст ошибки бэка приходит одной строкой. Контракт не выделяет поле отдельно,
      // поэтому эвристика по подстроке «ключ» — единственный способ отличить коллизию
      // ключа от прочих 400. Совпадения с формулировками McpRegistry.ValidateKey
      // (Сервер с ключом … уже есть / Ключ … занят / Ключ … зарезервирован)
      const msg = e instanceof SyntaxError
        ? 'Это не похоже на JSON — проверьте фрагмент'
        : (e instanceof Error && e.message ? e.message : 'Не удалось сохранить');
      if (!editing && /ключ/i.test(msg)) {
        setKeyError(msg);
      } else {
        setError(msg);
      }
    } finally {
      setBusy(false);
    }
  };

  // Поля, отмеченные в черновике как обязательные, должны быть заполнены перед
  // сохранением. Без этого импорт «проходит» на пустых env — и проба падает без
  // объяснения, в каком поле. Дефолт из реестра кладётся, но секрет-поля приезжают
  // пустыми по построению (значения секретов из реестра НЕ передаются, план §3)
  const requiredMissing = (() => {
    if (mode === 'stdio') {
      return env.some(p => p.isRequired && !p.stored && p.value.trim().length === 0);
    }
    return headers.some(p => p.isRequired && !p.stored && p.value.trim().length === 0);
  })();

  const canSubmit = mode === 'json'
    ? json.trim().length > 0
    : key.trim().length > 0
      && (mode === 'stdio' ? command.trim().length > 0 : url.trim().length > 0)
      && !requiredMissing;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {fromCatalog && (
        <div style={{
          padding: '8px 12px', borderRadius: R.lg, fontSize: FS.sm, lineHeight: 1.5,
          color: C.textSecondary, background: C.bgInset,
        }}>
          Настройки взяты из каталога. Проверьте их и заполните недостающее.
        </div>
      )}
      {!editing && !fromCatalog && (
        <InlineSegmented
          value={mode}
          options={[
            { value: 'stdio' as Mode, label: 'stdio' },
            { value: 'http' as Mode, label: 'http' },
            { value: 'json' as Mode, label: 'JSON-фрагмент' },
          ]}
          onChange={setMode}
        />
      )}

      {mode === 'json' ? (
        <Field
          label="JSON-фрагмент"
          hint="Вставьте объект mcpServers из конфига Claude Code или Claude Desktop — каждый сервер из фрагмента добавится как свой, выключенным. Значения env и headers приезжают открытыми: пометить их секретами можно потом, правкой записи."
        >
          <TextArea
            value={json}
            onChange={v => { setJson(v); setImported(null); }}
            minHeight={140}
            placeholder={'{\n  "mcpServers": {\n    "github-mcp": { "command": "npx", "args": ["-y", "@github/mcp-server"] }\n  }\n}'}
          />
        </Field>
      ) : (
        <>
          <Field label="Название">
            <TextField value={label} onChange={changeLabel} placeholder="Файловый сервер" />
          </Field>
          <Field
            label="Ключ"
            hint="Латиница в нижнем регистре, цифры, дефис. Под этим именем сервер виден персонам и в конфиге хода."
            error={liveKeyError}
          >
            <TextField
              value={key}
              onChange={changeKey}
              mono
              placeholder="file-server"
              invalid={!!liveKeyError}
            />
          </Field>

          {mode === 'stdio' ? (
            <>
              <Field label="Команда">
                <TextFieldWithCopy value={command} onChange={setCommand} mono placeholder="npx" />
              </Field>
              <Field label="Аргументы" hint="Через пробел; значение с пробелами возьмите в кавычки.">
                <TextFieldWithCopy value={args} onChange={setArgs} mono placeholder="-y @modelcontextprotocol/server-filesystem" />
              </Field>
              <PairList
                title="Переменные окружения"
                addLabel="+ Добавить переменную"
                pairs={env}
                onChange={setEnv}
              />
            </>
          ) : (
            <>
              <Field label="Адрес">
                <TextFieldWithCopy value={url} onChange={setUrl} mono placeholder="https://mcp.example.com/mcp" />
              </Field>
              <Field
                label="Способ входа"
                hint={authMode === 'oauth'
                  ? 'Кнопка «Войти» появится на карточке сервера — вход по OAuth 2.1 с PKCE.'
                  : 'Заголовки ниже (Authorization, X-Api-Key…) — как раньше.'}
              >
                <InlineSegmented
                  value={authMode}
                  options={[
                    { value: 'headers' as const, label: 'Заголовки' },
                    { value: 'oauth' as const, label: 'OAuth' },
                  ]}
                  onChange={setAuthMode}
                />
              </Field>
              {authMode === 'oauth' && (
                <Field
                  label="Client ID"
                  hint="Нужен только серверам без автоматической регистрации клиента (DCR). Если сервер её поддерживает — оставьте пустым, AI Home зарегистрируется сам при первом входе."
                >
                  <TextField value={oauthClientId} onChange={setOauthClientId} mono placeholder="необязательно" />
                </Field>
              )}
              <PairList
                title="Заголовки"
                addLabel="+ Добавить заголовок"
                pairs={headers}
                onChange={setHeaders}
              />
            </>
          )}
          <div style={hintStyle}>
            Секретные значения после сохранения не показываются — в форме правки вместо них
            остаётся только отметка «задано».
          </div>

          <div style={{ display: 'flex', alignItems: 'flex-start', gap: 14, paddingTop: 4 }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading, marginBottom: 3 }}>
                Доступ персонам «Только чтение»
              </div>
              <div style={hintStyle}>
                Имена инструментов этого сервера системе не известны, среди них могут быть пишущие —
                поэтому персоны с профилем «Только чтение» не получают сервер, пока это не разрешено явно.
              </div>
            </div>
            <div style={{ flexShrink: 0, paddingTop: 2 }}>
              <Toggle
                checked={allowReadOnlyPersonas}
                onChange={setAllowReadOnlyPersonas}
                ariaLabel="Доступ персонам «Только чтение»"
              />
            </div>
          </div>
        </>
      )}

      {error && (
        <div style={{
          padding: '7px 10px', borderRadius: R.md, fontSize: FS.sm,
          color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}`,
        }}>{error}</div>
      )}
      {imported && (
        <div style={{
          padding: '7px 10px', borderRadius: R.md, fontSize: FS.sm,
          color: C.successText, background: C.successBg,
        }}>{imported}</div>
      )}

      {fromCatalog && (
        <div style={{
          padding: '8px 12px', borderRadius: R.lg, fontSize: FS.sm, lineHeight: 1.5,
          color: C.textSecondary, background: C.bgInset,
        }}>
          Сервер сохранится <b style={{ color: C.textHeading }}>выключенным</b>. Включить
          его вы решите сами — в нужном проекте или для конкретной персоны.
        </div>
      )}

      <div style={{
        display: 'flex', justifyContent: 'flex-end', gap: SP.sm,
        paddingTop: SP.sm, borderTop: `1px solid ${C.borderLight}`,
      }}>
        <Button variant="ghost" size="sm" onClick={onCancel}>Отмена</Button>
        <Button variant="primary" size="sm" loading={busy} disabled={!canSubmit || busy} onClick={() => void submit()}>
          {mode === 'json' ? 'Добавить из JSON'
            : fromCatalog ? 'Сохранить выключенным'
              : editing ? 'Сохранить' : 'Добавить сервер'}
        </Button>
      </div>
    </div>
  );
}

function PairList({ title, addLabel, pairs, onChange }: {
  title: string;
  addLabel: string;
  pairs: Pair[];
  onChange: (pairs: Pair[]) => void;
}) {
  const patch = (i: number, next: Partial<Pair>) =>
    onChange(pairs.map((p, idx) => (idx === i ? { ...p, ...next } : p)));

  return (
    <Field label={title}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {pairs.map((pair, i) => (
          <PairRow key={i} pair={pair} onPatch={next => patch(i, next)} onRemove={() => onChange(pairs.filter((_, idx) => idx !== i))} />
        ))}
        <Button
          variant="dashed"
          size="sm"
          onClick={() => onChange([...pairs, { name: '', value: '', secret: false, stored: false }])}
        >{addLabel}</Button>
      </div>
    </Field>
  );
}

// Одна строка пары «ключ/значение». Живёт отдельно от PairList — у неё своё
// локальное состояние показа секрета (глазик), которое не должно раздувать список
// и перерисовывать соседей. Шапка-подпись из каталога рисуется только при наличии
// описания; без него поле «Ключ» остаётся как у обычной формы
function PairRow({ pair, onPatch, onRemove }: {
  pair: Pair;
  onPatch: (next: Partial<Pair>) => void;
  onRemove: () => void;
}) {
  const [revealed, setRevealed] = useState(false);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      {pair.description && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
          <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
            {pair.description}
          </span>
          <span style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
            border: `1px solid ${C.border}`, borderRadius: R.sm, padding: '1px 6px',
          }}>{pair.name}</span>
          {pair.isRequired && (
            <span style={{ fontSize: FS.sm, fontWeight: 700, color: C.danger }} aria-label="обязательное">*</span>
          )}
        </div>
      )}
      <div style={{
        display: 'grid', gridTemplateColumns: 'minmax(0,1fr) minmax(0,1.4fr) auto auto auto',
        gap: 6, alignItems: 'center',
      }}>
        <TextField
          value={pair.name}
          onChange={v => onPatch({ name: v })}
          mono
          placeholder={pair.description ? undefined : 'Ключ'}
        />
        {/* Значение секрета в виде точек: опечатку в токене видно только по провалу
            проверки. Глазик раскрывает на 1 показ — текст не сохраняется в стейте
            дольше жизни компонента, и при размонтировании значения нет */}
        <TextField
          value={pair.stored ? '' : pair.value}
          onChange={v => onPatch({ value: v, stored: false })}
          mono
          disabled={pair.stored}
          placeholder={pair.stored ? 'задано' : (pair.placeholder ?? 'Значение')}
          type={pair.secret && !revealed ? 'password' : 'text'}
        />
        {/* Глазик только у секретов — у обычных значений он лишний и съедает место.
            С stored=true кнопка не нужна: значение всё равно пустое */}
        {pair.secret && !pair.stored && (
          <IconButton
            size="sm"
            title={revealed ? 'Скрыть значение' : 'Показать значение'}
            onClick={() => setRevealed(r => !r)}
          >
            {revealed
              ? <EyeOff size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
              : <Eye size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          </IconButton>
        )}
        <label style={{
          display: 'flex', alignItems: 'center', gap: 5, fontSize: FS.xs,
          color: C.textSecondary, cursor: 'pointer', whiteSpace: 'nowrap', userSelect: 'none',
          fontFamily: FONT.sans,
        }}>
          <input
            type="checkbox"
            checked={pair.secret}
            onChange={e => onPatch({ secret: e.target.checked })}
            style={{ accentColor: C.accent, width: 14, height: 14, cursor: 'pointer' }}
          />
          секрет
        </label>
        <IconButton
          size="sm"
          tone="danger"
          title="Убрать"
          onClick={onRemove}
        >
          <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </div>
    </div>
  );
}

// Поле с кнопкой «Скопировать» справа: у команды, аргументов и адреса это первое,
// что человек делает с введённой строкой (проверяет её в терминале). Обёртка
// повторяет controlStyle TextField — бордер/фон/фокус рисуются на самом инпуте,
// иконка-кнопка стоит поверх слоя (position: absolute), ширина инпута компенсируется
// Поле с кнопкой «Скопировать» справа: у команды, аргументов и адреса это первое,
// что человек делает с введённой строкой (проверяет её в терминале). Объявление
// перенесено в начало файла — там, где его видит компилятор до JSX
// (function declaration срабатывает через hoisting, но переезд снимает сомнения
// у редактора и у людей, читающих файл сверху вниз)

function toPairs(values: McpServer['env'] | undefined): Pair[] {
  return (values ?? []).map(v => ({
    name: v.name,
    value: v.value ?? '',
    secret: v.secret,
    stored: v.secret && v.hasValue,
  }));
}

// Пустое значение секрета = «оставить как было»: бэк наследует прежний плейсхолдер
function toInputs(pairs: Pair[]): McpValueInput[] {
  return pairs
    .filter(p => p.name.trim().length > 0)
    .map(p => ({ name: p.name.trim(), value: p.stored ? '' : p.value, secret: p.secret }));
}

// Аргументы одной строкой → массив. Кавычки уважаем: в путях Windows пробелы —
// обычное дело, а разбиение по пробелам ломало бы такую команду молча.
function splitArgs(line: string): string[] {
  const out: string[] = [];
  const re = /"([^"]*)"|'([^']*)'|(\S+)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(line)) !== null) out.push(m[1] ?? m[2] ?? m[3]);
  return out;
}

// Кавычки в значении argv-аргумента: пробел или иной «особый» символ — оборачиваем
// в двойные кавычки. Зеркально splitArgs: что splitArgs разобрал, quoteIfNeeded
// собирает обратно. Используется при предзаполнении аргументов из черновика каталога
function quoteIfNeeded(value: string): string {
  if (!value) return value;
  // Кавычки нужны, если в строке есть символ, который оболочка трактовала бы
  // иначе. Самый частый случай — пробел в путях Windows. Кавычки в самом значении
  // оставляем на совести каталога (план §7: аргумент с кавычкой → отказ импорта
  // либо экранирование, не наше дело)
  if (/[\s"'\\$`<>|&;]/.test(value)) return `"${value}"`;
  return value;
}
