import { useEffect, useMemo, useState } from 'react';
import { Plug, Search, type LucideIcon } from 'lucide-react';
import type { BindingTarget, PersonaBinding, PersonaBindingMode } from '../../types';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button, IconField, InlineSegmented, WaitingIndicator } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { fetchBindingTargets } from './bindingMeta';
import { isMcpUserKey, TOOL_GROUPS, TOOL_GROUP_ORDER, toolDefaultCaption, toolGroupOf, toolIcon } from './toolMeta';

// Пикер инструментов — шаг «Цель» для привязки типа tool (гибрид макетов v1+v3,
// docs/mockups/persona-tool-picker-*.html): групповой список с иконками, подписью
// дефолтного состояния у персоны и inline-переключателем режима «Авто/Всегда/Выкл».
// Переключатель сохраняет мгновенно: без привязки — создаёт, с привязкой — меняет
// режим, повторный клик по активному режиму — снимает привязку (возврат к дефолту).
// Клик по телу строки — шаг ③ «Правило» с условием применения (как раньше).

// Сегменты режима с тонами активного состояния (как бейджи MODE_BADGE у карточек)
const MODE_SEGMENTS: { value: PersonaBindingMode; label: string; tone: { bg: string; fg: string } }[] = [
  { value: 'auto',   label: 'Авто',   tone: { bg: C.accentLight, fg: C.accent } },
  { value: 'always', label: 'Всегда', tone: { bg: C.infoBg,      fg: C.info } },
  { value: 'off',    label: 'Выкл',   tone: { bg: C.bgSelected,  fg: C.textHeading } },
];

// Единый паддинг строк-состояний (загрузка/ошибка/пусто)
const STATE_PAD = `${SP.md}px ${SP.lg}px`;

// Адаптер иконки инструмента: toolIcon() отдаёт стабильный LucideIcon из маппы
// модуля, но переменный JSX-тег правило считает компонентом, «созданным в рендере».
// Пропуск иконки пропом через модульный компонент находку снимает.
function ToolRowIcon({ icon: Icon }: { icon: LucideIcon }) {
  return <Icon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />;
}

export function ToolTargetPicker({ personaId, bindings, onSetMode, onOpenRule }: {
  personaId: string;
  // Все привязки персоны (из родителя) — по ним резолвится текущий режим каждого ключа
  bindings: PersonaBinding[];
  onSetMode: (target: BindingTarget, existing: PersonaBinding | undefined, mode: PersonaBindingMode) => Promise<void>;
  // Клик по телу строки — перейти к шагу «Правило» с выбранной целью
  onOpenRule: (target: BindingTarget) => void;
}) {
  const isMobile = useIsMobile();
  const [query, setQuery] = useState('');
  const [items, setItems] = useState<BindingTarget[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [reload, setReload] = useState(0);
  const [pending, setPending] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс списка перед перезагрузкой целей
    setItems(null);
    setLoadError(false);
    fetchBindingTargets('tool', undefined, personaId)
      .then(list => { if (alive) setItems(list); })
      .catch(() => { if (alive) { setItems([]); setLoadError(true); } });
    return () => { alive = false; };
  }, [personaId, reload]);

  // Текущая Tool-привязка по ключу инструмента (target регистронезависим)
  const bindingByKey = useMemo(() => {
    const m = new Map<string, PersonaBinding>();
    for (const b of bindings) if (b.type === 'tool') m.set(b.target.toLowerCase(), b);
    return m;
  }, [bindings]);

  const q = query.trim().toLowerCase();
  const groups = useMemo(() => {
    const byGroup = new Map<string, BindingTarget[]>();
    for (const t of items ?? []) {
      if (q && !t.label.toLowerCase().includes(q) && !(t.hint ?? '').toLowerCase().includes(q)) continue;
      const g = toolGroupOf(t.id);
      const arr = byGroup.get(g) ?? [];
      arr.push(t);
      byGroup.set(g, arr);
    }
    // «Свои MCP-серверы»: у пользователя без реестра группа пуста — вне поиска держим
    // её видимой (без элементов), чтобы показать пустое состояние с CTA «Добавить сервер»
    if (!q && !byGroup.has('mcpUser')) byGroup.set('mcpUser', []);
    return TOOL_GROUP_ORDER.filter(g => byGroup.has(g)).map(g => ({ key: g, items: byGroup.get(g)! }));
  }, [items, q]);

  const pick = async (t: BindingTarget, mode: PersonaBindingMode) => {
    if (pending) return;
    setPending(t.id.toLowerCase());
    try {
      await onSetMode(t, bindingByKey.get(t.id.toLowerCase()), mode);
    } finally {
      setPending(null);
    }
  };

  const renderRow = (t: BindingTarget) => (
    <ToolRow key={t.id} target={t} binding={bindingByKey.get(t.id.toLowerCase())}
      pending={pending === t.id.toLowerCase()} isMobile={isMobile} onPick={pick} onOpenRule={onOpenRule} />
  );

  return (
    <>
      <div style={{ marginTop: SP.md }}>
        <IconField
          value={query}
          onChange={setQuery}
          placeholder="Найти инструмент…"
          height={38}
          radius={R.lg}
          fontSize={FS.base}
          icon={<Search size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        />
      </div>
      <div style={{ background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl, marginTop: SP.sm, overflow: 'hidden' }}>
        {items === null && (
          <div style={{ padding: STATE_PAD }}>
            <WaitingIndicator />
          </div>
        )}
        {items !== null && loadError && (
          <div style={{ padding: STATE_PAD, fontSize: FS.sm, color: C.dangerText, display: 'flex', alignItems: 'center', gap: SP.sm }}>
            Не удалось загрузить список.
            <Button variant="ghost" size="sm" onClick={() => setReload(r => r + 1)}>Повторить</Button>
          </div>
        )}
        {items !== null && !loadError && groups.length === 0 && (
          <div style={{ padding: STATE_PAD, fontSize: FS.sm, color: C.textMuted }}>
            {q ? 'Ничего не найдено' : 'Список пуст'}
          </div>
        )}
        {items !== null && !loadError && groups.map(g => (
          <div key={g.key}>
            <div style={{
              fontSize: FS.xs, fontWeight: 600, textTransform: 'uppercase', letterSpacing: '0.05em',
              color: C.textSecondary, padding: `${SP.md}px ${SP.lg}px ${SP.xs}px`,
            }}>
              {TOOL_GROUPS[g.key as keyof typeof TOOL_GROUPS].title}
            </div>
            {g.key === 'danger' ? (
              <div style={{
                margin: `${SP.xs}px ${SP.sm}px ${SP.sm}px`,
                border: `1px dashed ${C.dangerBorder}`, borderRadius: R.lg,
                background: C.dangerBg, padding: SP.xs,
              }}>
                <div style={{ fontSize: FS.xs, color: C.dangerText, padding: `0 ${SP.xs}px ${SP.xs}px`, lineHeight: 1.4 }}>
                  Безвозвратное удаление файлов проектов и чатов — только по явной просьбе пользователя.
                </div>
                {g.items.map(renderRow)}
              </div>
            ) : g.key === 'mcpUser' && g.items.length === 0 ? (
              <McpUserEmptyState />
            ) : g.items.map(renderRow)}
          </div>
        ))}
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, lineHeight: 1.5 }}>
        Режим сохраняется сразу; повторный клик по активному — снять привязку и вернуть дефолт.
        Клик по строке — правило применения (условие).
      </div>
    </>
  );
}

function ToolRow({ target, binding, pending, isMobile, onPick, onOpenRule }: {
  target: BindingTarget;
  binding: PersonaBinding | undefined;
  pending: boolean;
  isMobile: boolean;
  onPick: (t: BindingTarget, mode: PersonaBindingMode) => Promise<void>;
  onOpenRule: (t: BindingTarget) => void;
}) {
  const group = TOOL_GROUPS[toolGroupOf(target.id)];
  const caption = binding ? null : toolDefaultCaption(target);
  // Сервер личного реестра (mcp:): условие применения на бэке игнорируется (см. target.hint) —
  // шаг «Правило» для него бессмысленен, клик по строке ничего не открывает
  const isMcpUser = isMcpUserKey(target.id);
  // hover/focus/press — inline-стилям недоступны псевдоклассы, ведём вручную
  const [hot, setHot] = useState(false);

  return (
    <div
      role={isMcpUser ? undefined : 'button'}
      tabIndex={isMcpUser ? undefined : 0}
      onClick={isMcpUser ? undefined : () => onOpenRule(target)}
      onKeyDown={isMcpUser ? undefined : e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpenRule(target); } }}
      onMouseEnter={isMcpUser ? undefined : () => setHot(true)}
      onMouseLeave={isMcpUser ? undefined : () => setHot(false)}
      onFocus={isMcpUser ? undefined : () => setHot(true)}
      onBlur={isMcpUser ? undefined : () => setHot(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.md, width: '100%', textAlign: 'left',
        padding: `${SP.sm}px ${SP.lg}px`, minHeight: 52, cursor: isMcpUser ? 'default' : 'pointer', fontFamily: FONT.sans,
        boxSizing: 'border-box', borderRadius: R.lg, flexWrap: 'wrap',
        background: hot ? C.bgSelected : 'transparent',
      }}
    >
      <span style={{
        width: 32, height: 32, borderRadius: R.full, flexShrink: 0,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        background: group.bg, color: group.fg,
      }}>
        <ToolRowIcon icon={toolIcon(target.id)} />
      </span>
      <span style={{ flex: 1, minWidth: 0 }}>
        <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
          {target.status && (
            <span
              title={mcpStatusTone(target.status).title}
              style={{
                width: 7, height: 7, borderRadius: R.full, flexShrink: 0,
                background: mcpStatusTone(target.status).dot,
              }}
            />
          )}
          <span style={{
            fontSize: FS.base, fontWeight: 600, color: C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>{target.label}</span>
        </span>
        {target.hint && (
          <span style={{ display: 'block', fontSize: FS.sm, color: C.textMuted, marginTop: SP.xxs, lineHeight: 1.35 }}>
            {target.hint}
          </span>
        )}
        {caption && (
          <span style={{ display: 'block', fontSize: FS.xs, color: caption.fg, marginTop: SP.xxs }}>
            {caption.text}
          </span>
        )}
      </span>
      <span onClick={e => e.stopPropagation()} onKeyDown={e => e.stopPropagation()}>
        <InlineSegmented<PersonaBindingMode>
          value={binding?.mode ?? null}
          options={MODE_SEGMENTS}
          disabled={pending}
          isMobile={isMobile}
          onChange={m => void onPick(target, m)}
        />
      </span>
    </div>
  );
}

// Тон точки статуса сервера личного реестра (BindingTarget.status — сырая строка с бэка,
// см. McpServerStatuses: connected/failed/needs-auth/unknown) — незнакомое значение уходит
// в нейтральный тон, чтобы будущие статусы бэка не ломали пикер
function mcpStatusTone(status: string): { dot: string; title: string } {
  switch (status.toLowerCase()) {
    case 'connected': return { dot: C.successText, title: 'Подключён' };
    case 'failed': return { dot: C.dangerText, title: 'Не удалось подключиться' };
    case 'needs-auth': return { dot: C.warningText, title: 'Нужен вход' };
    default: return { dot: C.textMuted, title: 'Статус неизвестен' };
  }
}

// Пустое состояние группы «Свои MCP-серверы» — у персоны без личного реестра нет ключей
// mcp:, но раздел остаётся видимым как приглашение завести первый сервер. Модалка
// управления реестром (волна 4) ещё не готова — кнопка пока неактивна.
function McpUserEmptyState() {
  return (
    <div style={{
      margin: `${SP.xs}px ${SP.sm}px ${SP.sm}px`, border: `1.5px dashed ${C.dashed}`,
      borderRadius: R.lg, padding: `${SP.md}px ${SP.lg}px`, textAlign: 'center',
    }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.4 }}>
        У вас пока нет своих MCP-серверов
      </div>
      <div style={{ marginTop: SP.sm }}>
        <Button variant="ghost" size="sm" disabled leftIcon={<Plug size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}>
          Добавить MCP-сервер
        </Button>
      </div>
    </div>
  );
}
