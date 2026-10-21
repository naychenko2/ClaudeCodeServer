// Читалка документа фичи (волна 3, вкладка «Планы и макеты»).
//
// Шапка: метка роли, заголовок, автор (имя персоны + «задача» — NavTargetButton),
// время сдачи. Тело: markdown через ТОТ ЖЕ рендер, что в ленте чата (MarkdownContent —
// свой не заводить: таблицы со своей прокруткой, код, mermaid — как в ленте).
//
// Текст — по клику: GET /api/tasks/{id} → resultMarkdown; кэш на время жизни страницы
// (модульный: переживает перемонтирование, лента/страница перерисовываются).
//
// Состояния: hasText=false → «Текста пока нет»; loading (скелет); error + «Повторить»;
// ok → markdown. Чипы «Макеты в тексте» — по plan.mockupPaths (тап → карточка макета).
//
// Длинный документ (6 и больше `##`): «Содержание · N» — на 1280 раскрыто, на 360 свёрнуто
// (свёртка/развёртка «Свернуть все / Развернуть все» — шаг Ф-plans-b, не здесь).

import { useEffect, useRef, useState } from 'react';
import { AlertTriangle, FileText, RotateCw, Scissors, MessageCircle, ChevronDown, type LucideIcon } from 'lucide-react';
import { C, R, FS, FONT, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Button } from '../ui/Button';
import { MarkdownContent } from '../chat/MarkdownContent';
import { getPersonaById } from '../../lib/personas';
import { openTaskInSection } from '../../lib/tasks';
import { NavTargetButton, NavTargetPair } from './NavTargetButton';
import { hhmm, dateOnly } from '../chat/feature/featureTime';
import { api } from '../../lib/api';
import type { FeaturePlan, FeatureMockup } from '../../types/featurePlans';

// === Роль документа: подпись + цвет значка (макет §токены) ===
// план — planLight/planText; разрез — infoBg/info; консультация — bgSelected/textSecondary.
export const PLAN_ROLE_LABEL: Record<FeaturePlan['role'], string> = {
  plan: 'план',
  cut: 'разрез',
  consult: 'консультация',
};
export const PLAN_ROLE_STYLE: Record<FeaturePlan['role'], { bg: string; fg: string; Icon: LucideIcon }> = {
  plan: { bg: C.planLight, fg: C.planText, Icon: FileText },
  cut: { bg: C.infoBg, fg: C.info, Icon: Scissors },
  consult: { bg: C.bgSelected, fg: C.textSecondary, Icon: MessageCircle },
};

// Кэш текста документа на время жизни страницы: (taskId, completedAt) → состояние.
// Модульный — переживает перемонтирование (лента/страница перерисовываются на каждом
// обновлении). Ключ включает completedAt: перезаписанный план (task обновлён) меняет
// completedAt → старый текст не показывается, идёт повторный запрос.
type TextState =
  | { status: 'loading' }
  | { status: 'error' }
  | { status: 'ok'; text: string };
const _textCache = new Map<string, TextState>();
const textCacheKey = (p: FeaturePlan) => `${p.taskId}|${p.completedAt ?? ''}`;

// `##`-заголовки markdown (для «Содержание · N»). Только уровень 2, без `###`.
function extractH2(text: string): string[] {
  const out: string[] = [];
  for (const line of text.split('\n')) {
    const m = /^##\s+(.+?)\s*$/.exec(line);
    if (m) out.push(m[1]);
  }
  return out;
}

// Значок роли: 24×24, R 7, цвет по роли (макет §токены).
export function RoleGlyph({ role }: { role: FeaturePlan['role'] }) {
  const { bg, fg, Icon } = PLAN_ROLE_STYLE[role];
  return (
    <span
      data-testid="plan-role-glyph"
      aria-label={PLAN_ROLE_LABEL[role]}
      style={{
        width: 24, height: 24, borderRadius: 7, flexShrink: 0,
        background: bg, color: fg,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      <Icon size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
    </span>
  );
}

// Служебные строки markdown (```fence```, `inline`) — не заголовки.
export interface FeaturePlanReaderProps {
  // Документ (план/разрез/консультация).
  plan: FeaturePlan;
  // Все макеты фичи — для чипов «Макеты в тексте» (сопоставление по mockupPaths).
  mockups: FeatureMockup[];
  isMobile?: boolean;
  // Тап по чипу «Макеты в тексте» → карточка макета в группе «Макеты».
  onOpenMockup?: (path: string) => void;
  // Открыть задачу документа (автор → «задача»).
  onOpenTask?: (taskId: string) => void;
}

export function FeaturePlanReader({ plan, mockups, isMobile = false, onOpenMockup, onOpenTask }: FeaturePlanReaderProps) {
  const [state, setState] = useState<TextState>(() => _textCache.get(textCacheKey(plan)) ?? { status: 'loading' });
  const [reload, setReload] = useState(0);
  // «Содержание · N» — раскрыто на 1280, свёрнуто на 360.
  const [tocOpen, setTocOpen] = useState(!isMobile);
  // Корень тела — для скролла к `##`-секции по пункту «Содержание» (под липкие вкладки).
  const bodyRef = useRef<HTMLDivElement>(null);
  // Пункт «Содержание» → докрутить соответствующую `##`-секцию (макет).
  const scrollToH2 = (i: number) => {
    const root = bodyRef.current;
    if (!root) return;
    const target = root.querySelectorAll('h2')[i] as HTMLElement | undefined;
    if (!target) return;
    // Компенсация липкой шапки/вкладок: секция встаёт под ними, а не под верх.
    target.style.scrollMarginTop = '72px';
    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };

  // Текст по клику: GET /api/tasks/{id} → resultMarkdown. Кэш на время жизни страницы.
  useEffect(() => {
    if (!plan.hasText) return; // «Текста пока нет» — запрос не идёт
    const key = textCacheKey(plan);
    // Повтор (reload > 0): сбросить кэш (в т.ч. ошибку), чтобы тянуть заново.
    if (reload > 0) _textCache.delete(key);
    const cached = _textCache.get(key);
    if (cached && cached.status !== 'loading') { setState(cached); return; }
    setState({ status: 'loading' });
    let cancelled = false;
    void api.tasks.get(plan.taskId)
      .then(t => {
        const s: TextState = { status: 'ok', text: t.resultMarkdown ?? '' };
        _textCache.set(key, s);
        if (!cancelled) setState(s);
      })
      .catch(() => {
        const s: TextState = { status: 'error' };
        _textCache.set(key, s);
        if (!cancelled) setState(s);
      });
    return () => { cancelled = true; };
  }, [plan.taskId, plan.completedAt, plan.hasText, reload]);

  const persona = plan.authorPersonaId ? getPersonaById(plan.authorPersonaId) : undefined;
  const authorName = persona?.name ?? (plan.authorPersonaId ? plan.authorPersonaId : null);
  const time = plan.completedAt ? `${dateOnly(plan.completedAt)} · ${hhmm(plan.completedAt)}` : null;

  // «Макеты в тексте»: mockupPaths → макеты (по path или по файлам).
  const mentioned = mockups.filter(m =>
    plan.mockupPaths.some(p => p === m.path || m.files.some(f => f.path === p))
  );

  const h2 = state.status === 'ok' ? extractH2(state.text) : [];
  const longDoc = h2.length >= 6;

  return (
    <div data-testid="plan-reader" style={{ display: 'flex', flexDirection: 'column', gap: SP.md, fontFamily: FONT.sans }}>
      {/* Шапка: роль · заголовок · автор · время */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
          <RoleGlyph role={plan.role} />
          <span
            data-testid="plan-reader-role"
            style={{ fontSize: FS.xs, fontWeight: 600, color: PLAN_ROLE_STYLE[plan.role].fg, background: PLAN_ROLE_STYLE[plan.role].bg, borderRadius: R.sm, padding: '0 6px' }}
          >
            {PLAN_ROLE_LABEL[plan.role]}
          </span>
        </div>
        <h1 data-testid="plan-reader-title" style={{ fontFamily: FONT.serif, fontSize: isMobile ? 18 : 20, fontWeight: 700, color: C.textHeading, letterSpacing: '-0.01em', margin: 0, lineHeight: 1.25 }}>
          {plan.title}
        </h1>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          {authorName && (
            <span style={{ fontSize: FS.sm, color: C.textSecondary, display: 'inline-flex', alignItems: 'center', gap: 4 }}>
              {authorName}
            </span>
          )}
          {plan.taskId && (
            <NavTargetButton
              kind="task"
              taskId={plan.taskId}
              onOpen={() => (onOpenTask ? onOpenTask(plan.taskId) : openTaskInSection({ id: plan.taskId }))}
              title="Открыть задачу документа"
              isMobile={isMobile}
            />
          )}
          {time && (
            <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{time}</span>
          )}
        </div>
      </div>

      <hr style={{ border: 'none', borderTop: `1px solid ${C.borderLight}`, margin: 0 }} />

      {/* «Макеты в тексте»: чипы по mockupPaths (тап → карточка макета). */}
      {mentioned.length > 0 && (
        <div data-testid="plan-reader-mockups" style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted, marginRight: 2 }}>Макеты в тексте:</span>
          {mentioned.map(m => (
            <button
              key={m.path}
              type="button"
              data-testid="plan-reader-mockup-chip"
              onClick={() => onOpenMockup?.(m.path)}
              style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                height: isMobile ? 40 : 26, padding: '0 8px',
                border: `1px solid ${C.border}`, background: C.bgWhite, borderRadius: R.sm,
                fontFamily: FONT.mono, fontSize: 12, color: C.accent, cursor: 'pointer',
              }}
            >
              {m.title}
            </button>
          ))}
        </div>
      )}

      {/* «Содержание · N» — длинный документ (6+ `##`): 1280 раскрыто, 360 свёрнуто. */}
      {longDoc && (
        <div data-testid="plan-reader-toc" style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.md, background: C.bgCard }}>
          <button
            type="button"
            aria-expanded={tocOpen}
            onClick={() => setTocOpen(v => !v)}
            style={{
              width: '100%', display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm,
              height: isMobile ? 44 : 32, padding: `0 ${SP.md}px`, border: 'none', background: 'transparent',
              cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
            }}
          >
            <span>Содержание · {h2.length}</span>
            <ChevronDown size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ transform: tocOpen ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }} />
          </button>
          {tocOpen && (
            <ol style={{ margin: 0, padding: `${SP.xs}px ${SP.md}px ${SP.sm}px`, listStyle: 'none', display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: `${SP.xs}px ${SP.md}px` }}>
              {h2.map((t, i) => (
                <li key={i}>
                  <button
                    type="button"
                    data-testid="plan-reader-toc-item"
                    onClick={() => scrollToH2(i)}
                    title={t}
                    style={{
                      width: '100%', textAlign: 'left', border: 'none', background: 'transparent', cursor: 'pointer',
                      padding: 0, fontFamily: FONT.sans, fontSize: FS.sm, color: C.accent,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}
                  >
                    {t}
                  </button>
                </li>
              ))}
            </ol>
          )}
        </div>
      )}

      {/* Тело: состояния */}
      {!plan.hasText ? (
        <div data-testid="plan-reader-no-text" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `${SP.md}px 0` }}>
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>Текста пока нет</span>
          {plan.taskId && (
            <NavTargetPair
              chatId={null}
              showNoChat={false}
              taskId={plan.taskId}
              onOpenTask={() => (onOpenTask ? onOpenTask(plan.taskId) : openTaskInSection({ id: plan.taskId }))}
              taskTitle="Открыть задачу документа"
              isMobile={isMobile}
            />
          )}
        </div>
      ) : state.status === 'loading' ? (
        <div data-testid="plan-reader-loading" style={{ display: 'flex', flexDirection: 'column', gap: 8, padding: `${SP.md}px 0` }}>
          {[70, 92, 84, 60].map((w, i) => (
            <div key={i} style={{ height: 12, width: `${w}%`, background: C.bgSelected, borderRadius: R.sm }} />
          ))}
        </div>
      ) : state.status === 'error' ? (
        <div data-testid="plan-reader-error" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', padding: `${SP.md}px 0` }}>
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: C.dangerText, fontSize: FS.sm }}>
            <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> Не удалось загрузить текст
          </span>
          <Button size="xs" variant="ghost" onClick={() => setReload(n => n + 1)}>
            <RotateCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> Повторить
          </Button>
          {plan.taskId && (
            <NavTargetButton
              kind="task"
              taskId={plan.taskId}
              onOpen={() => (onOpenTask ? onOpenTask(plan.taskId) : openTaskInSection({ id: plan.taskId }))}
              title="Открыть задачу документа"
              isMobile={isMobile}
            />
          )}
        </div>
      ) : (
        <>
          <div ref={bodyRef} data-testid="plan-reader-body" style={{ fontSize: FS.base, lineHeight: 1.6, color: C.textPrimary }}>
            <MarkdownContent text={state.text} />
          </div>
          {/* Подвал: текст — итог задачи, здесь только чтение (макет). */}
          <div data-testid="plan-reader-footer" style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: SP.md, paddingTop: SP.sm, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
            Текст — итог задачи. Здесь только чтение; правка — в задаче.
          </div>
        </>
      )}
    </div>
  );
}
