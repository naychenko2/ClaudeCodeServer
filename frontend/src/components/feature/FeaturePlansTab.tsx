// Вкладка «Планы и макеты» страницы фичи (волна 3, вторая после «Хронологии»).
//
// Раскладка: 1280 — список слева (240, липкий) + деталь справа (читалка / карточка
// макета); 360 — список во всю ширину, выбранный документ/макет — отдельный экран с
// «‹ Все документы».
//
// Документы (plans): порядок cut → plan → consult (макет); строка — значок роли,
// заголовок, автор, время, «без текста». Читалка — FeaturePlanReader (markdown через
// общий рендер). Макеты — FeatureMockups (строки, карточка, «Прошлые версии»).
//
// Состояния: документов нет, макеты есть → «Планов пока нет…»; mockupsUnavailable='device'
// → «Макеты локального проекта видны только на устройстве»; нет ничего → общее пустое.

import { useMemo, useState } from 'react';
import { ChevronLeft, MessageCircle } from 'lucide-react';
import { C, R, FS, FONT, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { getPersonaById } from '../../lib/personas';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import { splitMockups, isCurrentOfGroup } from '../../lib/mockupVersions';
import { RoleGlyph } from './FeaturePlanReader';
import { FeaturePlanReader } from './FeaturePlanReader';
import { MockupCard, MockupListSection, MockupDeviceNotice } from './FeatureMockups';
import type { FeaturePage } from '../../types/featureIndex';
import type { FeaturePlan, FeatureMockup } from '../../types/featurePlans';

// Выбран в детали: документ (plan) или макет (mockup).
type Selection = { kind: 'plan'; id: string } | { kind: 'mockup'; path: string } | null;

export interface FeaturePlansTabProps {
  // Данные страницы (plans / mockups / mockupsUnavailable).
  page: FeaturePage;
  isMobile?: boolean;
  // Открыть файл проекта (существующий просмотр, HTML в песочнице).
  onOpenFile?: (path: string) => void;
  // Открыть задачу (автор → «задача»).
  onOpenTask?: (taskId: string) => void;
  // «Открыть ключевой чат» (общее пустое).
  onOpenKeyChat?: () => void;
}

// Строка документа (список): значок роли · заголовок · автор · время · «без текста».
function PlanRow({ plan, selected, isMobile, onSelect }: {
  plan: FeaturePlan; selected: boolean; isMobile: boolean; onSelect: () => void;
}) {
  const [hover, setHover] = useState(false);
  const persona = plan.authorPersonaId ? getPersonaById(plan.authorPersonaId) : undefined;
  const authorName = persona?.name ?? (plan.authorPersonaId ? plan.authorPersonaId : null);
  const time = plan.completedAt ? `${dateOnly(plan.completedAt)} · ${hhmm(plan.completedAt)}` : null;
  return (
    <div
      data-testid="plan-row"
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(); } }}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      title={plan.title}
      style={{
        display: 'flex', alignItems: 'flex-start', gap: SP.sm,
        minHeight: isMobile ? 56 : 44, padding: `0 ${SP.sm}px`,
        background: selected ? C.accentLight : (hover ? C.bgSelected : 'transparent'),
        borderRadius: R.md, cursor: 'pointer',
      }}
    >
      <RoleGlyph role={plan.role} />
      <div style={{ minWidth: 0, flex: 1, display: 'flex', flexDirection: 'column', gap: 2 }}>
        {/* Заголовок — до двух строк (макет): line-clamp 2, строка растёт (minHeight). */}
        <span style={{ fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {plan.title}
        </span>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {authorName ? `${authorName} · ` : ''}{time ?? '—'}
        </span>
      </div>
      {!plan.hasText && (
        <span data-testid="plan-row-no-text" style={{ fontFamily: FONT.sans, fontSize: 10, color: C.textMuted, border: '1px dashed ' + C.dashed, borderRadius: R.sm, padding: '0 4px', whiteSpace: 'nowrap', flexShrink: 0, marginTop: 2 }}>
          без текста
        </span>
      )}
    </div>
  );
}

// Заголовок группы (FS.xs, капс, C.textMuted) + счётчик.
function GroupHeader({ label, count, isMobile }: { label: string; count: number; isMobile: boolean }) {
  return (
    <div data-testid="plans-group-header" style={{ display: 'flex', alignItems: 'center', gap: SP.xs, padding: `${isMobile ? SP.xs : 2}px ${SP.sm}px` }}>
      <span style={{ fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase', color: C.textMuted }}>{label}</span>
      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{count}</span>
    </div>
  );
}

export function FeaturePlansTab({ page, isMobile = false, onOpenFile, onOpenTask, onOpenKeyChat }: FeaturePlansTabProps) {
  // Документы: порядок cut → plan → consult (макет). Дедуп планов (решение Андрея):
  // переписанный план — показываем ПОСЛЕДНИЙ по createdAt; разрезы и консультации — все.
  const plans = useMemo(() => {
    const all = page.plans ?? [];
    const byRole = (role: FeaturePlan['role']) => all.filter(p => p.role === role);
    // Последний план по createdAt: сортируем по возрастанию и берём хвост.
    const planTasks = [...byRole('plan')].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
    const latestPlan = planTasks.length > 0 ? planTasks[planTasks.length - 1] : null;
    return [
      ...byRole('cut'),
      ...(latestPlan ? [latestPlan] : []),
      ...byRole('consult'),
    ];
  }, [page.plans]);
  const mockups: FeatureMockup[] = page.mockups ?? [];
  const device = page.mockupsUnavailable === 'device';

  const [selected, setSelected] = useState<Selection>(() => {
    // 1280: первый документ открыт по умолчанию (макет); на 360 — список.
    if (isMobile) return null;
    return plans.length > 0 ? { kind: 'plan', id: plans[0].taskId } : null;
  });
  const selectedPlan = selected?.kind === 'plan' ? plans.find(p => p.taskId === selected.id) ?? null : null;
  const selectedMockup = selected?.kind === 'mockup' ? mockups.find(m => m.path === selected.path) ?? null : null;
  // Выбранный макет — прошлая версия (не текущая в группе) → чип «заменён» в карточке.
  const selectedMockupReplaced = selectedMockup ? !isCurrentOfGroup(selectedMockup, mockups) : false;

  const hasAnything = plans.length > 0 || mockups.length > 0;
  // «Документов и макетов пока нет» — только когда пусто И макеты не «на устройстве»:
  // при mockupsUnavailable='device' макеты ЕСТЬ (на устройстве), пустота бы врала —
  // показываем честную причину (MockupDeviceNotice) независимо от наличия планов.
  const showNoDocuments = !hasAnything && !device;

  // === 360: одна колонка (список ИЛИ экран документа/макета) ===
  if (isMobile) {
    if (selectedPlan || selectedMockup) {
      return (
        <div data-testid="plans-mobile-detail" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
          <button
            type="button"
            data-testid="plans-back-to-list"
            onClick={() => setSelected(null)}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 4, alignSelf: 'flex-start',
              height: 44, padding: '0 8px', border: 'none', background: 'transparent',
              cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary,
            }}
          >
            <ChevronLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} /> Все документы
          </button>
          {selectedPlan
            ? <FeaturePlanReader plan={selectedPlan} mockups={mockups} isMobile onOpenMockup={p => setSelected({ kind: 'mockup', path: p })} onOpenTask={onOpenTask} />
            : selectedMockup
              ? <MockupCard mockup={selectedMockup} plans={plans} onOpenFile={onOpenFile} isMobile replaced={selectedMockupReplaced} />
              : null}
        </div>
      );
    }
    // Список во всю ширину.
    return (
      <div data-testid="plans-list" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
        {showNoDocuments ? (
          <NoDocuments hasKeyChat={!!onOpenKeyChat} onOpenKeyChat={onOpenKeyChat} isMobile />
        ) : (
          <>
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
              <GroupHeader label="Документы" count={plans.length} isMobile />
              {plans.length === 0
                ? <div data-testid="plans-empty-docs" style={{ border: `1px dashed ${C.dashed}`, borderRadius: R.md, padding: `${SP.md}px ${SP.md}px`, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textHeading }}>Планов пока нет…</div>
                : plans.map(p => (
                  <PlanRow key={p.taskId} plan={p} selected={false} isMobile onSelect={() => setSelected({ kind: 'plan', id: p.taskId })} />
                ))}
            </div>
            {device
              ? <MockupDeviceNotice isMobile />
              : <MockupListSection mockups={mockups} selectedPath={selected?.kind === 'mockup' ? selected.path : null} onSelect={p => setSelected({ kind: 'mockup', path: p })} isMobile />}
          </>
        )}
      </div>
    );
  }

  // === 1280: список (240, липкий) + деталь ===
  return (
    <div data-testid="plans-desktop" style={{ display: 'flex', gap: SP.lg, alignItems: 'flex-start' }}>
      <div data-testid="plans-list" style={{ width: 240, flexShrink: 0, position: 'sticky', top: 8, display: 'flex', flexDirection: 'column', gap: SP.md, maxHeight: 'calc(100vh - 120px)', overflowY: 'auto' }}>
        {showNoDocuments ? (
          <NoDocuments hasKeyChat={!!onOpenKeyChat} onOpenKeyChat={onOpenKeyChat} isMobile={false} />
        ) : (
          <>
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
              <GroupHeader label="Документы" count={plans.length} isMobile={false} />
              {plans.length === 0
                ? <div data-testid="plans-empty-docs" style={{ border: `1px dashed ${C.dashed}`, borderRadius: R.md, padding: `${SP.sm}px ${SP.md}px`, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textHeading }}>Планов пока нет…</div>
                : plans.map(p => (
                  <PlanRow key={p.taskId} plan={p} selected={selected?.kind === 'plan' && selected.id === p.taskId} isMobile={false} onSelect={() => setSelected({ kind: 'plan', id: p.taskId })} />
                ))}
            </div>
            {device
              ? <MockupDeviceNotice isMobile={false} />
              : <MockupListSection mockups={mockups} selectedPath={selected?.kind === 'mockup' ? selected.path : null} onSelect={p => setSelected({ kind: 'mockup', path: p })} isMobile={false} />}
          </>
        )}
      </div>
      <div data-testid="plans-detail" style={{ flex: 1, minWidth: 0 }}>
        {selectedPlan
          ? <FeaturePlanReader plan={selectedPlan} mockups={mockups} isMobile={false} onOpenMockup={p => setSelected({ kind: 'mockup', path: p })} onOpenTask={onOpenTask} />
          : selectedMockup
            ? <MockupCard mockup={selectedMockup} plans={plans} onOpenFile={onOpenFile} isMobile={false} replaced={selectedMockupReplaced} />
            : (
              <div data-testid="plans-detail-empty" style={{ padding: `${SP.xl}px 0`, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted }}>
                Выберите документ или макет
              </div>
            )}
      </div>
    </div>
  );
}

// Общее пустое: «Документов и макетов пока нет» + «Открыть ключевой чат».
function NoDocuments({ hasKeyChat, onOpenKeyChat, isMobile }: { hasKeyChat: boolean; onOpenKeyChat?: () => void; isMobile: boolean }) {
  return (
    <div data-testid="plans-all-empty" style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.md, padding: `${SP.xl}px 0`, textAlign: 'center' }}>
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary }}>
        <MessageCircle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        Документов и макетов пока нет
      </span>
      {hasKeyChat && (
        <button
          type="button"
          data-testid="plans-open-key-chat"
          onClick={onOpenKeyChat}
          style={{
            display: 'inline-flex', alignItems: 'center', gap: 6,
            height: isMobile ? 44 : 32, padding: '0 12px',
            border: `1px solid ${C.border}`, background: C.bgWhite, borderRadius: R.lg,
            cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.sm, color: C.accent,
          }}
        >
          Открыть ключевой чат
        </button>
      )}
    </div>
  );
}

// Счётчик вкладки «Планы и макеты»: документы + живые макеты (без отклонённых).
export function plansTabCount(page: FeaturePage): number {
  const plans = (page.plans ?? []).length;
  const live = splitMockups(page.mockups ?? []).live.length;
  return plans + live;
}
