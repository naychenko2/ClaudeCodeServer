// БЛОКИ 2–3 вкладки «Статистика» (волна 3, Ф7b, макет features-overview-3-final §«Статистика»):
//   · карточки итогов (по stats.timeline.totals);
//   · «Дольше всех сейчас» (stats.longestNow);
//   · разрезы (stats.breakdown: персоны/модели/роли) с долей, токенами, ценой и чатами;
//   · расход (stats.spend: токены, кэш, цена, общие чаты).
//
// Чистые помощники (formatTokens/formatUsd/sharePercent/roleGroupLabel/modelGroupLabel/
// personaGroupLabel) — в node, без DOM. Рендер-компоненты — токены C.*/FS/SP/R, кит ui/,
// обе темы, 360 (карточки в колонку, цели ≥ 40), клавиатура и фокус.
//
// Честность: данные ТОЛЬКО из stats (features/{id}); фронт не пересчитывает. Точность —
// значки (AccuracyBadge): «точно»/«≈»/«журнал»/«расход». null-поля → блока/строки нет
// (не выдумываем нули и заглушки «нужен журнал»).

import { useState } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { roleLabel } from '../../lib/roleLabel';
import { modelLabel, useModels } from '../../lib/models';
import { usePersonas } from '../../lib/personas';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { formatDuration } from './FeatureStatsBits';
import { NavTargetPair, NavTargetButton } from './NavTargetButton';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, type FeatureNavCtx } from '../../lib/featureNavMarks';
import { SegmentedControl } from '../ui/Segmented';
import { EmptyState } from '../ui/EmptyState';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import type { Persona } from '../../types';
import type {
  FeatureTimeline, FeatureBreakdown, FeatureBreakdownGroup,
  FeatureLongestNow, FeatureSpend, FeatureStatsSource,
} from '../../types/featureStats';

// === Чистые помощники (node, без DOM) ===

// Токены: «1,2М» / «640 тыс.» / «12 345». null → '' (нет данных).
export function formatTokens(n: number | null): string {
  if (n == null) return '';
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1).replace('.', ',')}М`;
  if (n >= 1_000) return `${Math.round(n / 1000)} тыс.`;
  return String(n);
}

// Деньги (USD): «$15,60» / «$0,42». null → '' (нет данных).
export function formatUsd(n: number | null): string {
  if (n == null) return '';
  return `$${n.toFixed(2).replace('.', ',')}`;
}

// Доля 0..1 → «31 %». null → '' (не считалась).
export function sharePercent(share: number | null): string {
  if (share == null) return '';
  return `${Math.round(share * 100)} %`;
}

// Ключ группы «роли» (сырой taskRole) → русская подпись (roleLabel).
// 'step' без stepRef/lane → roleLabel пуст → нейтральное «шаг».
export function roleGroupLabel(key: string): string {
  const l = roleLabel(key, null, null);
  if (l) return l;
  return key === 'step' ? 'шаг' : key;
}

// Ключ группы «модели» (modelFull) → читаемое имя модели (modelLabel; незнакомый — как есть).
export function modelGroupLabel(key: string): string {
  return modelLabel(key) || key;
}

// Ключ группы «персоны» (personaId) → имя персоны; недоступна → «персона недоступна» (не UUID).
export function personaGroupLabel(key: string, personas: Persona[]): string {
  const p = personas.find(x => x.id === key);
  return p ? p.name : 'персона недоступна';
}

// === Значок точности (один, инлайн) ===
// ex — «точно» (success); ap — «≈» (mono, muted); jr — «журнал» (info); sp — «расход» (muted).
export function AccuracyBadge({ kind }: { kind: 'ex' | 'ap' | 'jr' | 'sp' }) {
  const cfg: Record<'ex' | 'ap' | 'jr' | 'sp', { text: string; bg: string; color: string; mono?: boolean }> = {
    ex: { text: 'точно', bg: C.successBg, color: C.successText },
    ap: { text: '≈', bg: C.bgSelected, color: C.textMuted, mono: true },
    jr: { text: 'журнал', bg: C.infoBg, color: C.info },
    sp: { text: 'расход', bg: C.bgSelected, color: C.textMuted },
  };
  const c = cfg[kind];
  return (
    <span
      data-testid={`stats-acc-${kind}`}
      style={{
        fontFamily: c.mono ? FONT.mono : FONT.sans,
        fontSize: FS.xxs, fontWeight: 700,
        background: c.bg, color: c.color,
        borderRadius: R.sm, padding: '0 4px',
        whiteSpace: 'nowrap', flexShrink: 0,
        display: 'inline-flex', alignItems: 'center',
      }}
    >
      {c.text}
    </span>
  );
}

// Заголовок секции (макет .stl): 11, uppercase, muted, letter-spacing.
function SectionTitle({ children, testId }: { children: React.ReactNode; testId?: string }) {
  return (
    <div
      data-testid={testId}
      style={{
        fontSize: FS.xxs, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em',
        color: C.textMuted, fontFamily: FONT.sans,
      }}
    >
      {children}
    </div>
  );
}

// === 1. Карточки итогов (по stats.timeline.totals) ===
// Всего прошло (точно) · Работа (≈/журнал + «агенты в сумме») · Ожидание/Простой/Ошибки
// (только если не null, режим journal). В approx — только «всего» и «работа ≈».
// 360 — одна колонка; 1280 — сетка 2×N.
export function TotalsCards({ totals, source, isMobile = false }: {
  totals: FeatureTimeline['totals']; source: FeatureStatsSource; isMobile?: boolean;
}) {
  const t = totals;
  // Карточки: [label, value, accuracy, hint?].
  type Card = { label: string; value: string; acc: 'ex' | 'ap' | 'jr' | 'sp'; hint?: string };
  const cards: Card[] = [];
  // Всего прошло — всегда (календарь: точно).
  cards.push({ label: 'Всего прошло', value: formatDuration(t.elapsedMinutes), acc: 'ex', hint: 'от создания эпика' });
  // Работа — всегда; точность по source; «агенты в сумме» — нейтральная подпись.
  cards.push({
    label: 'Работа',
    value: (source === 'approx' ? '≈ ' : '') + formatDuration(t.workWallMinutes),
    acc: source === 'approx' ? 'ap' : 'jr',
    hint: `агенты в сумме ≈ ${formatDuration(t.agentsSumMinutes)}`,
  });
  // Не работали (макет: «не работали / простой» — ожидание и простой вместе) — ≈.
  cards.push({
    label: 'Не работали',
    value: (source === 'approx' ? '≈ ' : '') + formatDuration(t.notWorkingMinutes),
    acc: source === 'approx' ? 'ap' : 'jr',
  });
  // Ожидание / Простой / Ошибки — только если не null (режим journal).
  if (t.waitingMinutes != null) cards.push({ label: 'Ожидание', value: formatDuration(t.waitingMinutes), acc: 'jr' });
  if (t.idleMinutes != null) cards.push({ label: 'Простой', value: formatDuration(t.idleMinutes), acc: 'jr' });
  if (t.errorMinutes != null) {
    cards.push({
      label: 'Ошибки',
      value: formatDuration(t.errorMinutes),
      acc: 'jr',
      hint: t.errorCount != null ? `${t.errorCount} эпизодов` : undefined,
    });
  }

  const cols = isMobile ? 1 : 2;
  return (
    <div data-testid="stats-cards" style={{ display: 'grid', gridTemplateColumns: `repeat(${cols}, 1fr)`, gap: SP.sm }}>
      {cards.map((c, i) => (
        <div
          key={i}
          data-testid={`stats-card-${i}`}
          style={{
            background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
            padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0,
          }}
        >
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
            <span style={{ fontSize: FS.xs, color: C.textSecondary, fontWeight: 600, fontFamily: FONT.sans }}>{c.label}</span>
            <AccuracyBadge kind={c.acc} />
          </div>
          <div style={{ fontSize: FS.lg, fontWeight: 700, color: C.textHeading, fontFamily: FONT.sans }}>{c.value}</div>
          {c.hint && <div style={{ fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.sans }}>{c.hint}</div>}
        </div>
      ))}
    </div>
  );
}

// === 2. «Дольше всех сейчас» (stats.longestNow) ===
// null → блока нет. Исполнитель/шаг · минуты · доля · токены · «чат · задача».
export function LongestNow({ ln, nav, isMobile = false }: { ln: FeatureLongestNow; nav?: FeatureNavCtx; isMobile?: boolean }) {
  const marks = nav?.marks ?? EMPTY_NAV_MARKS;
  const personas = usePersonas();
  const persona = ln.personaId ? personas.find(p => p.id === ln.personaId) : undefined;
  const name = persona?.name ?? (ln.personaId ? 'персона недоступна' : '—');
  const step = ln.stepRef ? `шаг ${ln.stepRef}` : null;

  const detail: string[] = [];
  detail.push(formatDuration(ln.minutes));
  if (ln.share != null) detail.push(`${sharePercent(ln.share)} фичи`);
  if (ln.tokens != null) detail.push(`${formatTokens(ln.tokens)} токенов`);

  return (
    <div
      data-testid="stats-longest"
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
        background: C.warningBg, border: `1px solid ${C.warning}`, borderRadius: R.lg,
        padding: `${SP.sm}px ${SP.md}px`,
      }}
    >
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <div style={{ fontSize: FS.sm, fontWeight: 700, color: C.textHeading, fontFamily: FONT.sans }}>
          Дольше всех сейчас
        </div>
        <div style={{ fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.sans }}>
          {name}{step ? ` · ${step}` : ''} · {detail.join(' · ')}
        </div>
      </div>
      <NavTargetPair
        chatId={ln.chatId}
        chatArchived={marks.chatArchived(ln.chatId)}
        onOpenChat={() => {
          if (!ln.chatId) return;
          if (nav?.featureId) recordFeatureReturn(nav.featureId, { kind: 'chat', id: ln.chatId }, nav.featureTitle);
          void openChatById(ln.chatId);
        }}
        taskId={ln.taskId}
        taskClosed={marks.taskClosed(ln.taskId)}
        onOpenTask={() => {
          if (nav?.featureId) recordFeatureReturn(nav.featureId, { kind: 'task', id: ln.taskId }, nav.featureTitle);
          openTaskInSection({ id: ln.taskId });
        }}
        isMobile={isMobile}
      />
    </div>
  );
}

// === 3. Разрезы (stats.breakdown: personas/models/roles) ===
// Переключатель оси (кит) · группы: имя · минуты · доля (полоса) · токены крупно · цена ·
// чаты (раскрытие). Ключи переводятся: personaId → имя, modelFull → имя модели, role → roleLabel.
type Axis = 'personas' | 'models' | 'roles';

export function Breakdown({ breakdown, costPartial, nav, isMobile = false }: {
  breakdown: FeatureBreakdown; costPartial: boolean; nav?: FeatureNavCtx; isMobile?: boolean;
}) {
  const personas = usePersonas();
  useModels(); // реактивность: догрузка каталога моделей меняет подписи
  const [axis, setAxis] = useState<Axis>('personas');
  const [openKey, setOpenKey] = useState<string | null>(null);
  // «время · токены» (макет): метрика, по которой считается доля/полоса. Токены —
  // только если учёт расхода есть (иначе у групп tokens=null).
  const [metric, setMetric] = useState<'time' | 'tokens'>('time');

  const groups = breakdown[axis];
  const valueOf = (g: FeatureBreakdownGroup): number => metric === 'time' ? g.minutes : (g.tokens ?? 0);
  const totalVal = groups.reduce((a, g) => a + valueOf(g), 0);
  const maxVal = groups.length ? Math.max(...groups.map(g => valueOf(g))) : 1;
  // Токены доступны? (хотя бы одна группа с tokens) — иначе опция «токены» приглушена.
  const tokensAvailable = groups.some(g => g.tokens != null);

  const labelOf = (g: FeatureBreakdownGroup): string => {
    if (axis === 'personas') return personaGroupLabel(g.key, personas);
    if (axis === 'models') return modelGroupLabel(g.key);
    return roleGroupLabel(g.key);
  };

  const isPersonaAxis = axis === 'personas';

  return (
    <div data-testid="stats-breakdown" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <SectionTitle testId="stats-breakdown-title">Кто и на чём работал</SectionTitle>
      <SegmentedControl
        value={axis}
        onChange={setAxis}
        touch={isMobile}
        options={[
          { value: 'personas', label: 'персоны' },
          { value: 'models', label: 'модели' },
          { value: 'roles', label: 'роли' },
        ]}
      />
      {/* «время · токены» (макет): метрика доли/полосы; токены — приглушены без учёта расхода. */}
      <SegmentedControl
        value={metric}
        onChange={setMetric}
        touch={isMobile}
        options={[
          { value: 'time', label: 'время' },
          { value: 'tokens', label: 'токены', muted: !tokensAvailable, title: !tokensAvailable ? 'токены недоступны без учёта расхода' : undefined },
        ]}
      />
      {costPartial && (
        <div data-testid="stats-breakdown-cost-partial" style={{ fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.sans }}>
          цена по части моделей недоступна
        </div>
      )}
      {groups.length === 0 ? (
        <EmptyState
          compact
          inline
          icon="📊"
          title="Пока нечего разрезать"
          subtitle="Появится, когда будет работа по этой оси."
        />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {groups.map(g => {
            const open = openKey === g.key;
            // Доля/полоса — по выбранной метрике («время · токены»).
            const share = totalVal > 0 ? valueOf(g) / totalVal : 0;
            const persona = isPersonaAxis && personas.find(p => p.id === g.key);
            return (
              <div
                key={g.key}
                data-testid="stats-break-group"
                style={{ background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, overflow: 'hidden' }}
              >
                {/* Строка группы: раскрытие по клику (клавиатура, aria-expanded). */}
                <button
                  type="button"
                  onClick={() => setOpenKey(open ? null : g.key)}
                  aria-expanded={open}
                  aria-label={`${labelOf(g)}: ${formatDuration(g.minutes)} · ${Math.round(share * 100)} % — показать чаты`}
                  style={{
                    display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%',
                    padding: `${isMobile ? SP.sm : SP.xs}px ${SP.sm}px`,
                    background: 'transparent', border: 'none', cursor: 'pointer',
                    minHeight: isMobile ? 40 : undefined, textAlign: 'left',
                  }}
                >
                  {/* Аватар (только персоны) или нейтральный маркер. */}
                  {isPersonaAxis
                    ? (persona
                      ? <PersonaAvatar persona={persona} size={18} />
                      : <span aria-hidden style={{ width: 18, height: 18, borderRadius: R.full, background: C.bgSelected, flexShrink: 0, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.sans }}>?</span>)
                    : <span aria-hidden style={{ width: 6, height: 6, borderRadius: R.full, background: C.accent, flexShrink: 0 }} />}
                  {/* Имя группы. */}
                  <span style={{ flex: '0 1 auto', minWidth: 0, fontSize: FS.sm, fontWeight: 600, color: C.textPrimary, fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {labelOf(g)}
                  </span>
                  {/* Минуты. */}
                  <span style={{ fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.mono, flexShrink: 0 }}>
                    {formatDuration(g.minutes)}
                  </span>
                  {/* Доля: полоса (∝ максимуму группы по метрике) + процент (∝ сумме по метрике). */}
                  <span aria-hidden style={{ flex: 1, minWidth: 24, height: 6, borderRadius: R.max, background: C.progressTrack, overflow: 'hidden', display: 'inline-block' }}>
                    <span data-testid="stats-break-bar" style={{ display: 'block', height: '100%', width: `${Math.max(2, (valueOf(g) / maxVal) * 100)}%`, background: C.accent, borderRadius: R.max }} />
                  </span>
                  <span data-testid="stats-break-share" style={{ fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.mono, flexShrink: 0 }}>
                    {Math.round(share * 100)} %
                  </span>
                  {/* Токены «вход+выход» — крупно; null — нет данных (не показываем). */}
                  {g.tokens != null && (
                    <span data-testid="stats-break-tokens" style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, fontFamily: FONT.mono, flexShrink: 0 }}>
                      {formatTokens(g.tokens)}
                    </span>
                  )}
                  {/* Цена: null — не показываем; costPartial — пометка выше. */}
                  {g.costUsd != null && (
                    <span data-testid="stats-break-cost" style={{ fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.mono, flexShrink: 0 }}>
                      {formatUsd(g.costUsd)}
                    </span>
                  )}
                  {/* Стрелка раскрытия. */}
                  <span aria-hidden style={{ fontSize: FS.xs, color: C.textMuted, transform: open ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s', flexShrink: 0 }}>
                    ›
                  </span>
                </button>
                {/* Раскрытие: чаты группы (с переходами). */}
                {open && (
                  <div data-testid="stats-break-chats" style={{ padding: `${SP.xs}px ${SP.sm}px ${SP.sm}px`, borderTop: `1px solid ${C.borderLight}`, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                    {g.chatIds.length === 0 ? (
                      <div style={{ fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.sans }}>Чатов нет.</div>
                    ) : g.chatIds.map(cid => (
                      <ChatRow key={cid} chatId={cid} nav={nav} isMobile={isMobile} />
                    ))}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

// Строка чата в раскрытом разрезе: id (коротко) + переход «чат» (NavTargetButton: «в архиве»
// из данных страницы; точка возврата → чип «← к фиче · Статистика» в шапке чата).
function ChatRow({ chatId, nav, isMobile = false }: { chatId: string; nav?: FeatureNavCtx; isMobile?: boolean }) {
  const short = chatId.length > 12 ? `${chatId.slice(0, 8)}…` : chatId;
  const marks = nav?.marks ?? EMPTY_NAV_MARKS;
  return (
    <div
      data-testid="stats-break-chat"
      style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.mono }}
    >
      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{short}</span>
      <NavTargetButton
        kind="chat"
        chatId={chatId}
        chatArchived={marks.chatArchived(chatId)}
        title={`Открыть чат ${short}`}
        isMobile={isMobile}
        onOpen={() => {
          if (nav?.featureId) recordFeatureReturn(nav.featureId, { kind: 'chat', id: chatId }, nav.featureTitle);
          void openChatById(chatId);
        }}
      />
    </div>
  );
}

// === 4. Расход (stats.spend) ===
// inputTokens+outputTokens крупно · кэш (read/creation) мелко отдельной строкой ·
// costUsd с пометкой при costPartial · from («данные с …», только если расход ненулевой) ·
// shared («в т. ч. доля общих чатов ≈ N (оценка, по …)»). spend === null → блока нет.
// Метод оценки shared — из `spend.shared.method` (не из stats.source: расход и лента могут
// считаться разными способами).
export function Spend({ spend }: {
  spend: FeatureSpend;
}) {
  const main = spend.inputTokens + spend.outputTokens;
  const cache = spend.cacheReadTokens + spend.cacheCreationTokens;
  // Расход ненулевой? (у пустой фичи from = дата создания эпика — не вводить в заблуждение.)
  const nonzero = main > 0 || cache > 0 || spend.costUsd > 0;

  return (
    <div
      data-testid="stats-spend"
      style={{
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
        padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.sm,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs }}>
        <SectionTitle testId="stats-spend-title">Расход</SectionTitle>
        <AccuracyBadge kind="sp" />
      </div>
      {/* Токены «вход+выход» — крупно. */}
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
        <span data-testid="stats-spend-tokens" style={{ fontSize: FS.xl, fontWeight: 700, color: C.textHeading, fontFamily: FONT.mono }}>
          {formatTokens(main)}
        </span>
        <span style={{ fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.sans }}>
          токенов (вход + выход)
        </span>
      </div>
      {/* Кэш — мелко, отдельной строкой. */}
      {cache > 0 && (
        <div data-testid="stats-spend-cache" style={{ fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.mono }}>
          кэш: {formatTokens(spend.cacheReadTokens)} чтение · {formatTokens(spend.cacheCreationTokens)} создание
        </div>
      )}
      {/* Цена: с пометкой, если часть моделей без цены. */}
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
        <span data-testid="stats-spend-cost" style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, fontFamily: FONT.mono }}>
          {formatUsd(spend.costUsd)}
        </span>
        {spend.costPartial && (
          <span data-testid="stats-spend-cost-partial" style={{ fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.sans }}>
            часть моделей без цены: {spend.hiddenCostModels.join(', ')}
          </span>
        )}
      </div>
      {/* «Данные с …» — только если расход ненулевой (иначе у пустой фичи это дата эпика). */}
      {nonzero && (
        <div data-testid="stats-spend-from" style={{ fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.sans }}>
          данные с {new Date(spend.from).toLocaleDateString('ru-RU')}
        </div>
      )}
      {/* Доля общих чатов (уже включена в итоги) — оценка. */}
      {spend.shared && (
        <div data-testid="stats-spend-shared" style={{ fontSize: FS.xxs, color: C.textSecondary, fontFamily: FONT.sans }}>
          в т. ч. доля общих чатов ≈ {formatTokens(spend.shared.inputTokens + spend.shared.outputTokens)} токенов (оценка, по {spend.shared.method === 'journal' ? 'журналу' : 'задакам ≈'})
        </div>
      )}
    </div>
  );
}
