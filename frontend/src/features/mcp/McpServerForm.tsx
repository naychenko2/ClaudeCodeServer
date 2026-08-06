import { useState } from 'react';
import type { CSSProperties } from 'react';
import { X } from 'lucide-react';
import { Button, Field, IconButton, InlineSegmented, TextArea, TextField } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { slugify } from '../../lib/slug';
import type { McpData } from './useMcpData';
import type { McpServer, McpValueInput } from '../../types';

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
}

const hintStyle: CSSProperties = { fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 };

export function McpServerForm({ data, server, onDone, onCancel }: {
  data: McpData;
  server: McpServer | null;      // null — создание
  onDone: () => void;
  onCancel: () => void;
}) {
  const editing = server !== null;
  const [mode, setMode] = useState<Mode>(
    server ? (server.transport === 'stdio' ? 'stdio' : 'http') : 'stdio');
  const [label, setLabel] = useState(server?.label ?? '');
  const [key, setKey] = useState(server?.key ?? '');
  const [keyTouched, setKeyTouched] = useState(editing);
  const [command, setCommand] = useState(server?.command ?? '');
  const [args, setArgs] = useState((server?.args ?? []).join(' '));
  const [url, setUrl] = useState(server?.url ?? '');
  const [env, setEnv] = useState<Pair[]>(() => toPairs(server?.env));
  const [headers, setHeaders] = useState<Pair[]>(() => toPairs(server?.headers));
  const [json, setJson] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [imported, setImported] = useState<string | null>(null);

  const changeLabel = (v: string) => {
    setLabel(v);
    if (!keyTouched) setKey(slugify(v, true));
  };

  const submit = async () => {
    setBusy(true);
    setError('');
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
        ...(mode === 'stdio'
          ? { command: command.trim(), args: splitArgs(args), env: toInputs(env) }
          : { url: url.trim(), headers: toInputs(headers) }),
      });
      onDone();
    } catch (e) {
      setError(e instanceof SyntaxError
        ? 'Это не похоже на JSON — проверьте фрагмент'
        : (e instanceof Error && e.message ? e.message : 'Не удалось сохранить'));
    } finally {
      setBusy(false);
    }
  };

  const canSubmit = mode === 'json'
    ? json.trim().length > 0
    : key.trim().length > 0 && (mode === 'stdio' ? command.trim().length > 0 : url.trim().length > 0);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      {!editing && (
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
          <Field label="Ключ" hint="Латиница в нижнем регистре, цифры, дефис. Под этим именем сервер виден персонам и в конфиге хода.">
            <TextField
              value={key}
              onChange={v => { setKey(slugify(v, true)); setKeyTouched(true); }}
              mono
              placeholder="file-server"
            />
          </Field>

          {mode === 'stdio' ? (
            <>
              <Field label="Команда">
                <TextField value={command} onChange={setCommand} mono placeholder="npx" />
              </Field>
              <Field label="Аргументы" hint="Через пробел; значение с пробелами возьмите в кавычки.">
                <TextField value={args} onChange={setArgs} mono placeholder="-y @modelcontextprotocol/server-filesystem" />
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
                <TextField value={url} onChange={setUrl} mono placeholder="https://mcp.example.com/mcp" />
              </Field>
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

      <div style={{
        display: 'flex', justifyContent: 'flex-end', gap: SP.sm,
        paddingTop: SP.sm, borderTop: `1px solid ${C.borderLight}`,
      }}>
        <Button variant="ghost" size="sm" onClick={onCancel}>Отмена</Button>
        <Button variant="primary" size="sm" loading={busy} disabled={!canSubmit || busy} onClick={() => void submit()}>
          {mode === 'json' ? 'Добавить из JSON' : editing ? 'Сохранить' : 'Добавить сервер'}
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
          <div key={i} style={{
            display: 'grid', gridTemplateColumns: 'minmax(0,1fr) minmax(0,1.4fr) auto auto',
            gap: 6, alignItems: 'center',
          }}>
            <TextField value={pair.name} onChange={v => patch(i, { name: v })} mono placeholder="Ключ" />
            <TextField
              value={pair.stored ? '' : pair.value}
              onChange={v => patch(i, { value: v, stored: false })}
              mono
              disabled={pair.stored}
              placeholder={pair.stored ? 'задано' : 'Значение'}
            />
            <label style={{
              display: 'flex', alignItems: 'center', gap: 5, fontSize: FS.xs,
              color: C.textSecondary, cursor: 'pointer', whiteSpace: 'nowrap', userSelect: 'none',
              fontFamily: FONT.sans,
            }}>
              <input
                type="checkbox"
                checked={pair.secret}
                onChange={e => patch(i, { secret: e.target.checked })}
                style={{ accentColor: C.accent, width: 14, height: 14, cursor: 'pointer' }}
              />
              секрет
            </label>
            <IconButton
              size="sm"
              tone="danger"
              title="Убрать"
              onClick={() => onChange(pairs.filter((_, idx) => idx !== i))}
            >
              <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            </IconButton>
          </div>
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
