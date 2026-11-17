// KM-F11: форма профиля ребёнка для админа — тело PUT /api/users/{id}/child-profile (ADR-041, ручка №10).
// Вызывающий (редактор в «Пользователях», KM-F12) даёт пользователя и то, что уже знает о его профиле;
// «Сохранить» — единственный путь записи. Компонент не читает профиль сам: отдельной GET-ручки профиля
// в контракте нет (ADR-041, «Ручки» №10 — только PUT).
//
// Черновик привязан к user.id: сменили пользователя — черновик и только-чтение сброшены (сброс в рендере,
// по образцу «derived state» из React: старый вывод не коммитится). Ответ сохранения проверяется тем же
// id: ответ для прежнего пользователя отбрасывается, он не пишет чужие только-чтение и не зовёт onSaved.
// Время — строго «HH:mm» (контракт ADR): некорректное — подсветка поля и ни одного запроса; остальное
// решает сервер, его 400-код переводится в человеческий текст (REJECT_TEXT), message ответа показываем как есть.

import { useRef, useState } from 'react';
import { AlertTriangle, Plus, Trash2 } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { Notice } from '../../components/ui/Notice';
import { Button } from '../../components/ui/Button';
import { Checkbox } from '../../components/ui/Checkbox';
import { Field, TextField } from '../../components/ui/Field';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { childApi } from './api';
import type { ChildProfile, TimeWindow } from './types';

// Пользователь для редактора: id и имя — из списка «Пользователей»; профиль, тьютор и дата согласия —
// то, что знает вызывающий (из данных списка, когда бэк их добавит, или из последнего сохранения здесь).
export interface ChildProfileEditorUser {
  id: string;
  username: string;
  childProfile?: ChildProfile | null;
  tutorPersonaId?: string | null;
  rulesAgreedAt?: string | null;
}

interface Props {
  user: ChildProfileEditorUser;
  onSaved: (profile: ChildProfile) => void;
}

// Единственное пространство волны 1 («Уроки»); ключ — из ADR-041.
const LESSONS = 'lessons';
const DEFAULT_WARN_MINUTES = 5;
// Строгий формат времени контракта: две цифры часа 00..23, две цифры минуты 00..59.
const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d$/;
// Дни недели — ISO 1..7 (Пн..Вс); пустой выбор — каждый день (поля в теле нет, ADR-041).
const DAY_LABELS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];

// Человеческий текст отказов 400 ручки №10 (ADR-041): код — причина для интерфейса, человеку —
// что сделать. message ответа добавляется строкой ниже, как есть (ADR-041, «Общие правила»).
const REJECT_TEXT: Record<string, string> = {
  not_container: 'Детский режим включается только пользователям в песочнице. Сейчас процессы этого пользователя идут на машине сервера.',
  provider_not_api_key: 'Такого провайдера нет среди записей с API-ключом. Ребёнку нужна отдельная запись пула с ключом, не подписка.',
  invalid_window: 'Окно времени задано неверно: формат ЧЧ:ММ, конец позже начала или через полночь.',
  invalid_space: 'Пространство задано неверно. Сейчас доступно только «Уроки».',
  guardian_not_found: 'Среди опекунов есть id, которого нет среди пользователей. Проверь список через запятую.',
};
const REJECT_FALLBACK = 'Сохранить не удалось. Попробуй ещё раз.';

// Черновик — строки полей ввода (пусто = поля нет в теле), окна списком, дни — множества ISO.
interface DraftWindow { from: string; to: string; days: number[] }
interface Draft {
  enabled: boolean;
  providerKey: string;
  model: string;
  windows: DraftWindow[];
  dailyMinutes: string;
  warnMinutes: string;
  dailyBudget: string;
  monthlyBudget: string;
  guardians: string;
  resetRules: boolean;
  // Прочие пространства профиля (волна 2) форма не правит — они проходят в тело как есть.
  otherSpaces: Record<string, { windows: TimeWindow[]; dailyMinutes?: number | null }>;
}

// Снимок редактора: черновик + только-чтение, всё под одним id. Смена id — новый снимок.
interface Snapshot { id: string; draft: Draft; tutorPersonaId: string | null; rulesAgreedAt: string | null }

function emptyDraft(): Draft {
  return {
    enabled: false, providerKey: '', model: '', windows: [],
    dailyMinutes: '', warnMinutes: String(DEFAULT_WARN_MINUTES),
    dailyBudget: '', monthlyBudget: '', guardians: '', resetRules: false, otherSpaces: {},
  };
}

function draftOf(user: ChildProfileEditorUser): Draft {
  const d = emptyDraft();
  const p = user.childProfile;
  if (!p) return d;
  d.enabled = p.enabled;
  d.providerKey = p.providerKey ?? '';
  d.model = p.model ?? '';
  d.guardians = (p.guardianUserIds ?? []).join(', ');
  d.dailyBudget = p.dailyBudgetUsd == null ? '' : String(p.dailyBudgetUsd);
  d.monthlyBudget = p.monthlyBudgetUsd == null ? '' : String(p.monthlyBudgetUsd);
  d.warnMinutes = String(p.warnMinutes ?? DEFAULT_WARN_MINUTES);
  const { [LESSONS]: lessons, ...other } = p.spaces ?? {};
  d.otherSpaces = other;
  d.windows = (lessons?.windows ?? []).map((w) => ({ from: w.from, to: w.to, days: w.weekdays ?? [] }));
  d.dailyMinutes = lessons?.dailyMinutes == null ? '' : String(lessons.dailyMinutes);
  return d;
}

const numOrNull = (s: string): number | null => {
  const t = s.trim();
  if (!t) return null;
  const n = Number(t);
  return Number.isFinite(n) ? n : null;
};

const splitIds = (s: string): string[] => s.split(',').map((x) => x.trim()).filter(Boolean);

// Тело PUT: выключенный режим — `{ enabled: false }` (ADR-041, ручка №10); включённый — ChildProfileDto.
// resetRules отправляем только отмеченным: JSON.stringify выбрасывает undefined, как в соседних ручках.
function bodyOf(d: Draft): ChildProfile | { enabled: false } {
  if (!d.enabled) return { enabled: false };
  return {
    enabled: true,
    guardianUserIds: splitIds(d.guardians),
    providerKey: d.providerKey.trim(),
    model: d.model.trim(),
    spaces: {
      ...d.otherSpaces,
      [LESSONS]: {
        windows: d.windows.map((w) => ({ from: w.from, to: w.to, weekdays: w.days.length ? w.days : undefined })),
        dailyMinutes: numOrNull(d.dailyMinutes),
      },
    },
    dailyBudgetUsd: numOrNull(d.dailyBudget),
    monthlyBudgetUsd: numOrNull(d.monthlyBudget),
    warnMinutes: numOrNull(d.warnMinutes) ?? DEFAULT_WARN_MINUTES,
    resetRules: d.resetRules || undefined,
  };
}

const fmtDate = (iso: string): string =>
  new Date(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });

export function ChildProfileEditor({ user, onSaved }: Props) {
  const isMobile = useIsMobile();
  const [snap, setSnap] = useState<Snapshot>(() => ({
    id: user.id, draft: draftOf(user), tutorPersonaId: user.tutorPersonaId ?? null, rulesAgreedAt: user.rulesAgreedAt ?? null,
  }));
  // Смена пользователя — новый снимок по новому id (черновик сброшен).
  if (snap.id !== user.id) {
    setSnap({ id: user.id, draft: draftOf(user), tutorPersonaId: user.tutorPersonaId ?? null, rulesAgreedAt: user.rulesAgreedAt ?? null });
  }
  const d = snap.id === user.id ? snap.draft : draftOf(user);
  const [saving, setSaving] = useState(false);
  // Пустое поле окна подсвечивается только после попытки сохранить; некорректное — сразу, пока правят.
  const [attempted, setAttempted] = useState(false);
  const [reject, setReject] = useState<{ text: string; server?: string } | null>(null);
  // Текущий пользователь на момент прихода ответа: по нему отбрасываем устаревший ответ.
  const activeIdRef = useRef(user.id);
  activeIdRef.current = user.id;

  const patch = (fn: (prev: Draft) => Draft) => setSnap((s) => (s.id === user.id ? { ...s, draft: fn(s.draft) } : s));
  const patchWindow = (i: number, fn: (w: DraftWindow) => DraftWindow) =>
    patch((prev) => ({ ...prev, windows: prev.windows.map((w, j) => (j === i ? fn(w) : w)) }));

  const badTime = (v: string) => !TIME_RE.test(v);
  const showBadTime = (v: string) => badTime(v) && (attempted || v !== '');

  const save = async () => {
    if (saving) return; // два нажатия — один запрос
    if (d.enabled && d.windows.some((w) => badTime(w.from) || badTime(w.to))) {
      setAttempted(true); // подсветка полей, запроса нет
      return;
    }
    const forId = user.id;
    setReject(null);
    setSaving(true);
    try {
      const res = await childApi.putProfile(forId, bodyOf(d));
      // Ответ прежнего пользователя: не применяется и не держит кнопку в загрузке —
      // у нового пользователя своего запроса нет (второй не стартует, пока saving), поэтому
      // снять флаг безопасно.
      if (activeIdRef.current !== forId) { setSaving(false); return; }
      setSaving(false);
      setAttempted(false);
      setSnap((s) => (s.id === forId ? {
        ...s,
        // Тьютора берём из ответа: при первом включении его создаёт сервер. Согласие сбрасываем только
        // когда сброс просили этим сохранением — дата не теряется сама.
        tutorPersonaId: res.tutorPersonaId ?? s.tutorPersonaId,
        rulesAgreedAt: d.resetRules ? null : s.rulesAgreedAt,
      } : s));
      onSaved(res.profile);
    } catch (e) {
      if (activeIdRef.current !== forId) { setSaving(false); return; }
      const err = e as { status?: number; body?: { code?: string; message?: string } } | null;
      const body = err?.body;
      const server = typeof body?.message === 'string' && body.message.trim() ? body.message.trim() : undefined;
      const code = typeof body?.code === 'string' ? body.code : undefined;
      setReject({ text: (code && REJECT_TEXT[code]) || REJECT_FALLBACK, server });
      setSaving(false);
    }
  };

  const rowGap = isMobile ? SP.sm : SP.md;
  const fieldStyle = { display: 'flex', flexDirection: 'column' as const, gap: rowGap };
  const panelStyle = {
    display: 'flex', flexDirection: 'column' as const, gap: SP.md,
    padding: isMobile ? `${SP.sm}px ${SP.md}px` : SP.md,
    border: `1px solid ${C.border}`, borderRadius: R.xl, background: C.bgCard, boxShadow: SHADOW.card,
  };
  const sectionTitle = (t: string) => (
    <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>{t}</span>
  );
  const staticRow = (label: string, value: string, testid: string, mono?: boolean) => (
    <div data-testid={testid} style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
      <span style={{ fontSize: FS.sm, color: C.textMuted, minWidth: 120 }}>{label}</span>
      <span style={{ fontSize: FS.base, color: C.textPrimary, fontFamily: mono ? FONT.mono : 'inherit' }}>{value}</span>
    </div>
  );
  const dayChip = (day: number, i: number) => {
    const on = d.windows[i].days.includes(day);
    return (
      <Button
        key={day}
        variant={on ? 'ghostAccent' : 'ghost'}
        size="md"
        ariaPressed={on}
        ariaLabel={`День недели ${DAY_LABELS[day - 1]}`}
        onClick={() => patchWindow(i, (w) => ({ ...w, days: on ? w.days.filter((x) => x !== day) : [...w.days, day].sort() }))}
      >
        {DAY_LABELS[day - 1]}
      </Button>
    );
  };

  return (
    <div data-testid="child-profile-editor" style={panelStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>Детский режим</span>
        <span style={{ fontSize: FS.sm, color: C.textMuted }}>{user.username}</span>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Checkbox checked={d.enabled} onChange={(v) => patch((prev) => ({ ...prev, enabled: v }))} ariaLabel="Детский режим" />
        <span style={{ fontSize: FS.md, color: C.textPrimary }}>Детский режим</span>
      </div>
      <span style={{ fontSize: FS.sm, color: C.textMuted, marginTop: -SP.sm }}>
        {d.enabled
          ? 'Ограничения действуют с момента сохранения: окна, лимиты и только эта модель по API-ключу.'
          : 'Выключено: пользователь обычный, его ход не ограничен. Настройки ниже сохранятся, когда включишь режим.'}
      </span>

      {reject && (
        <Notice tone="warning" icon={AlertTriangle} title="Сохранить не удалось">
          <div>{reject.text}</div>
          {reject.server && <div style={{ marginTop: SP.xxs, opacity: 0.85 }}>{reject.server}</div>}
        </Notice>
      )}

      <div style={fieldStyle}>
        {sectionTitle('Маршрут модели')}
        <Field label="Провайдер (ключ записи пула)" hint="Запись пула с API-ключом: ход ребёнка не идёт по подписке (И2)">
          <TextField value={d.providerKey} mono onChange={(v) => patch((prev) => ({ ...prev, providerKey: v }))} placeholder="anthropic-api-kids" />
        </Field>
        <Field label="Модель" hint="Закреплённая модель «Уроков», например sonnet">
          <TextField value={d.model} onChange={(v) => patch((prev) => ({ ...prev, model: v }))} placeholder="sonnet" />
        </Field>
      </div>

      <div style={fieldStyle}>
        {sectionTitle('Окно «Уроков»')}
        {d.windows.length === 0 && (
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>Окон нет — время разрешено весь день, если хватит дневных минут.</span>
        )}
        {d.windows.map((w, i) => (
          <div key={i} data-testid="time-window" style={{ ...fieldStyle, padding: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.lg }}>
            <div data-testid="window-row" style={{ display: 'flex', gap: rowGap, flexDirection: isMobile ? 'column' : 'row' }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <Field label="С" error={showBadTime(w.from) ? 'Формат ЧЧ:ММ' : undefined}>
                  <TextField value={w.from} mono invalid={showBadTime(w.from)} inputMode="numeric" placeholder="09:00"
                    onChange={(v) => patchWindow(i, (prev) => ({ ...prev, from: v }))} />
                </Field>
              </div>
              <div style={{ flex: 1, minWidth: 0 }}>
                <Field label="По" error={showBadTime(w.to) ? 'Формат ЧЧ:ММ' : undefined} hint="Раньше «С» — окно через полночь">
                  <TextField value={w.to} mono invalid={showBadTime(w.to)} inputMode="numeric" placeholder="20:00"
                    onChange={(v) => patchWindow(i, (prev) => ({ ...prev, to: v }))} />
                </Field>
              </div>
              <Button variant="ghost" size="md" ariaLabel="Удалить окно" dataTestId={`window-remove-${i}`}
                onClick={() => patch((prev) => ({ ...prev, windows: prev.windows.filter((_, j) => j !== i) }))}>
                <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              </Button>
            </div>
            <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
              {DAY_LABELS.map((_, dayIdx) => dayChip(dayIdx + 1, i))}
            </div>
            <span style={{ fontSize: FS.sm, color: C.textMuted }}>
              {w.days.length ? `Дни: ${w.days.map((x) => DAY_LABELS[x - 1]).join(', ')}` : 'Дни не выбраны — каждый день'}
            </span>
          </div>
        ))}
        <Button variant="dashed" size="md" dataTestId="add-window" leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={() => patch((prev) => ({ ...prev, windows: [...prev.windows, { from: '', to: '', days: [] }] }))}>
          Добавить окно
        </Button>
        <Field label="Минут в день" hint="Пусто — дневного лимита нет">
          <TextField value={d.dailyMinutes} inputMode="numeric" onChange={(v) => patch((prev) => ({ ...prev, dailyMinutes: v }))} placeholder="30" />
        </Field>
        <Field label="Предупредить за, мин" hint="Сколько остаётся, когда ребёнок видит предупреждение">
          <TextField value={d.warnMinutes} inputMode="numeric" onChange={(v) => patch((prev) => ({ ...prev, warnMinutes: v }))} />
        </Field>
      </div>

      <div style={fieldStyle}>
        {sectionTitle('Бюджет')}
        <div style={{ display: 'flex', gap: rowGap, flexDirection: isMobile ? 'column' : 'row' }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Field label="День, $" hint="Пусто — дневного бюджета нет">
              <TextField value={d.dailyBudget} inputMode="decimal" onChange={(v) => patch((prev) => ({ ...prev, dailyBudget: v }))} placeholder="2" />
            </Field>
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Field label="Месяц, $" hint="Пусто — месячного бюджета нет">
              <TextField value={d.monthlyBudget} inputMode="decimal" onChange={(v) => patch((prev) => ({ ...prev, monthlyBudget: v }))} placeholder="40" />
            </Field>
          </div>
        </div>
      </div>

      <Field label="Опекуны (id через запятую)" hint="Уведомления о стоп-сигнале доходят и им">
        <TextField value={d.guardians} mono onChange={(v) => patch((prev) => ({ ...prev, guardians: v }))} placeholder="id-1, id-2" />
      </Field>

      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Checkbox checked={d.resetRules} onChange={(v) => patch((prev) => ({ ...prev, resetRules: v }))} ariaLabel="Сбросить согласие с правилами" />
        <span style={{ fontSize: FS.md, color: C.textPrimary }}>Сбросить согласие с правилами</span>
      </div>

      <div style={fieldStyle}>
        {sectionTitle('Только чтение')}
        {staticRow('Персона-тьютор', snap.tutorPersonaId ?? 'ещё нет', 'tutor-state', true)}
        {staticRow('Правила договорены', snap.rulesAgreedAt ? fmtDate(snap.rulesAgreedAt) : 'ещё нет', 'rules-state')}
      </div>

      <Button variant="primary" size="md" fullWidth loading={saving} dataTestId="save-profile" onClick={save}>
        Сохранить
      </Button>
    </div>
  );
}
