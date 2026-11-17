// KM-F5: главная экрана ребёнка («Уроки») — только разметка, без запросов:
// состояние и данные приезжают пропсами (KM-F8 приносит ChildStatus из childApi).
// Образец: docs/mockups/kids-shell-v2.html (ветка docs/kids-shell-mockup), секция «Главная».
//
// CHILD_SUBJECTS — единственный источник плиток предметов и их порядка:
// KM-F8/F10 импортируют её отсюда, порядок плиток = порядок константы.

import type { ReactNode } from 'react';
import { Brush, Users } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_PROPS, ICON_SIZE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { Badge } from '../../components/ui/Badge';
import { Button } from '../../components/ui/Button';
import { EmptyState } from '../../components/ui/EmptyState';
import { Island } from '../../components/ui/Island';
import type { ChildStatus } from './types';

// Предметы волны 1. Цвета не новые: макет красит глифы существующими парами токенов
// (math → info, russian → rose, english → success); подложка rose смешана из токена —
// как .g-ru в макете (color-mix из var(), не новый hex).
export const CHILD_SUBJECTS = [
  { id: 'math', label: 'Математика', glyph: '×÷', bg: C.infoBg, fg: C.info },
  { id: 'russian', label: 'Русский язык', glyph: 'Аа', bg: `color-mix(in srgb, ${C.rose} 14%, ${C.bgCard})`, fg: C.roseText },
  { id: 'english', label: 'Английский', glyph: 'Ab', bg: C.successBg, fg: C.successText },
] as const;

export type ChildSubjectId = (typeof CHILD_SUBJECTS)[number]['id'];

// «Вчера остановились тут»: subjectId резолвится по CHILD_SUBJECTS —
// подпись и цвет карточки приходят из константы, не с сервера.
export interface ChildLastLesson {
  subjectId: ChildSubjectId;
  title: string;
  // «Шаг 2 из 3» — как принёс сервер; нет — строка шага не рисуется
  progress?: string | null;
}

interface Props {
  childName: string;
  // Старшая причина состояния (И3): rules_pending меняет экран на договорённость
  status: ChildStatus;
  lastLesson?: ChildLastLesson | null;
  onOpenSubject: (id: ChildSubjectId) => void;
  onResume: () => void;
  onStartRules: () => void;
}

// windowEndsAt — ISO 8601 со смещением таймзоны ребёнка (ADR-041, «Общие правила»),
// поэтому часы читаются прямо из строки: пересчёт в таймзону браузера сдвинул бы их.
function windowEndsClock(iso: string): string | null {
  const m = iso.match(/T(\d{2}:\d{2})/);
  return m ? m[1] : null;
}

// Глиф предмета: квадрат 40 под тач-цель (макет .glyph), serif — как подписи предметов в макете
function SubjectGlyph({ bg, fg, children }: { bg: string; fg: string; children: ReactNode }) {
  return (
    <span style={{
      width: 40, height: 40, flexShrink: 0, borderRadius: R.lg,
      background: bg, color: fg, display: 'grid', placeItems: 'center',
      fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700,
    }}>
      {children}
    </span>
  );
}

export function ChildHome({ childName, status, lastLesson, onOpenSubject, onResume, onStartRules }: Props) {
  const isMobile = useIsMobile();

  // Первый день (правила не согласованы): вместо всего экрана — договорённость с родителем
  // (макет home(), S.day='first').
  if (status.state === 'rules_pending') {
    return (
      <EmptyState
        icon={<Users {...ICON_PROPS} size={ICON_SIZE.xl} />}
        title="Сначала договоримся о правилах"
        subtitle="Это займёт минут пять. Позови папу — пройдёте вместе, а потом начнём уроки."
        action={
          <Button
            size="lg"
            variant="primary"
            glow
            leftIcon={<Users {...ICON_PROPS} size={ICON_SIZE.md} />}
            onClick={onStartRules}
          >
            Начать вместе с папой
          </Button>
        }
      />
    );
  }

  const endsClock = status.windowEndsAt ? windowEndsClock(status.windowEndsAt) : null;
  const lastSubject = lastLesson ? CHILD_SUBJECTS.find((s) => s.id === lastLesson.subjectId) : undefined;

  return (
    <div style={{
      maxWidth: 760, width: '100%', margin: '0 auto', boxSizing: 'border-box',
      padding: isMobile ? `${SP.xl}px ${SP.lg}px` : `${SP.xxxl}px ${SP.xl}px`,
      display: 'flex', flexDirection: 'column', gap: SP.lg,
    }}>
      <div>
        <div style={{
          fontFamily: FONT.serif, fontSize: isMobile ? FS.h1 : FS.display,
          fontWeight: 500, lineHeight: 1.2, color: C.textHeading,
        }}>
          Привет, {childName}
        </div>
        {endsClock && (
          <div style={{ fontSize: FS.lg, lineHeight: 1.55, color: C.textSecondary, marginTop: SP.sm }}>
            Уроки открыты до {endsClock}.
          </div>
        )}
      </div>

      {lastLesson && lastSubject && (
        <Island bg={C.bgCard} borderColor={C.borderLight} shadow={SHADOW.card} style={{ borderRadius: R.xl, padding: SP.lg }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.lg, flexWrap: 'wrap' }}>
            <SubjectGlyph bg={lastSubject.bg} fg={lastSubject.fg}>{lastSubject.glyph}</SubjectGlyph>
            <div style={{ flex: 1, minWidth: 200 }}>
              <div style={{ fontSize: FS.sm, color: C.textMuted }}>Вчера остановились тут</div>
              <div style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading, marginTop: 2 }}>
                {lastSubject.label} · {lastLesson.title}
              </div>
              {lastLesson.progress && (
                <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>{lastLesson.progress}</div>
              )}
            </div>
            <Button size="md" variant="primary" fullWidth={isMobile} onClick={onResume}>
              Продолжить
            </Button>
          </div>
        </Island>
      )}

      <div style={{ fontSize: FS.xxs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted }}>
        Предметы
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : 'repeat(3, 1fr)', gap: SP.md }}>
        {CHILD_SUBJECTS.map((s) => (
          <button
            key={s.id}
            type="button"
            onClick={() => onOpenSubject(s.id)}
            style={{
              display: 'flex', alignItems: 'center', gap: SP.md, padding: SP.lg, minHeight: 40,
              background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
              boxShadow: SHADOW.card, cursor: 'pointer', textAlign: 'left', font: 'inherit', color: 'inherit',
            }}
          >
            <SubjectGlyph bg={s.bg} fg={s.fg}>{s.glyph}</SubjectGlyph>
            <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>{s.label}</span>
          </button>
        ))}
      </div>

      {/* Студия волны 2: плитка видна, но неактивна — как .card.dash с aria-disabled в макете */}
      <div
        aria-disabled="true"
        style={{
          display: 'flex', alignItems: 'center', gap: SP.md, padding: SP.lg, minHeight: 40,
          background: 'transparent', border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl,
        }}
      >
        <SubjectGlyph bg={C.planLight} fg={C.planText}>
          <Brush {...ICON_PROPS} size={ICON_SIZE.md} />
        </SubjectGlyph>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading, display: 'flex', alignItems: 'center', gap: SP.sm }}>
            Студия
            <Badge tone="plan" size="xs">скоро</Badge>
          </div>
          <div style={{ fontSize: FS.sm, color: C.textMuted, marginTop: 2 }}>
            Здесь можно будет рисовать и сочинять вместе с помощником.
          </div>
        </div>
      </div>
    </div>
  );
}
