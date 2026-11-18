// KM-F5: главная экрана ребёнка («Уроки») — только разметка, без запросов:
// состояние и данные приезжают пропсами (KM-F8 приносит ChildStatus из childApi).
// Образец: docs/mockups/kids-tutors.html (Д1), раздел «Ребёнок · Главная» — плитки
// с глифом, именем предмета и строкой репетитора.
//
// Плитки: только из пропа `subjects` (GET /api/child/subjects, Т3c), порядок = порядок
// серверного списка (И5). Пустой список — пустой список: выдумывать нечего.

import { useState, type ReactNode } from 'react';
import { BookOpen, Brush, Users } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_PROPS, ICON_SIZE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { Badge } from '../../components/ui/Badge';
import { Button } from '../../components/ui/Button';
import { EmptyState } from '../../components/ui/EmptyState';
import { Island } from '../../components/ui/Island';
import { userAvatarColor } from '../../components/ui/UserAvatar';
import { cachedToken } from '../../lib/offline';
import { personaInitials } from '../../lib/personas';
import { subjectColors } from './subjectPalette';
import { tutorAvatarUrl } from './api';
import type { ChildStatus, ChildSubjectTutor, ChildSubjectView } from './types';

// «Вчера остановились тут»: subjectId — ключ предмета из профиля (slug ^[a-z0-9-]{1,32}$),
// поэтому тип — строка; подпись и цвет карточки приходят из найденной в subjects плитки.
// Предмета нет в живом списке (закрыт/удалён или список ещё не пришёл) — карточка остаётся:
// без метки и цвета предмета, в нейтральном цвете (Т3e).
export interface ChildLastLesson {
  subjectId: string;
  title: string;
}

interface Props {
  childName: string;
  // Старшая причина состояния (И3): rules_pending меняет экран на договорённость
  status: ChildStatus;
  // Плитки с сервера (GET /api/child/subjects). Пустой список — пустой список.
  subjects: ChildSubjectView[];
  lastLesson?: ChildLastLesson | null;
  // id — серверный ключ предмета (строка).
  onOpenSubject: (id: string) => void;
  onResume: () => void;
  onStartRules: () => void;
}

// Плитка в нормализованном виде: цвет — из палитры ключа, пустой глиф — «?».
interface Tile {
  id: string;
  label: string;
  // Пустой глиф — «?» (тот же фолбэк, что у .glyph в макете): плитка без знака не читается
  glyph: string;
  bg: string;
  fg: string;
  // null — висящий ключ персоны: имени и аватара нет, строки репетитора на плитке тоже нет
  tutor: ChildSubjectTutor | null;
}

const serverTiles = (subjects: ChildSubjectView[]): Tile[] => subjects.map((s) => {
  const { bg, fg } = subjectColors(s.color);
  return { id: s.id, label: s.label, glyph: s.glyph || '?', bg, fg, tutor: s.tutor };
});

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

// Знак репетитора (макет .pav): круг R.full. Есть аватар — картинка /api/personas/{id}/avatar
// (hasAvatar=true ⟺ ручка отдаёт картинку; пропала с диска — 404, и знак возвращается к
// инициалам: onError, тот же приём, что у PersonaAvatar). Нет аватара — инициалы имени на
// стабильном цвете по id персоны (тот же ключ цвета, что у ui/UserAvatar: тёзки не сливаются).
// Репетитор null (висящий ключ персоны) — этот компонент не зовут вовсе.
// Экспортирован: та же метка стоит в шапке урока (ChildLessonChat, Т3d) и в мобильном хабе
// оболочки (ChildWorkspace, .mobt) — три места рисуют её одним кодом.
export function TutorMark({ tutor, size }: { tutor: ChildSubjectTutor; size: number }) {
  const [imgFailed, setImgFailed] = useState(false);
  const round = { width: size, height: size, borderRadius: R.full, flexShrink: 0 } as const;
  if (tutor.hasAvatar && !imgFailed) {
    return (
      <img
        src={tutorAvatarUrl(tutor.personaId, cachedToken() ?? undefined)}
        alt=""
        aria-hidden
        onError={() => setImgFailed(true)}
        style={{ ...round, objectFit: 'cover', display: 'block' }}
      />
    );
  }
  return (
    <span
      aria-hidden
      style={{
        ...round, background: userAvatarColor(tutor.personaId), color: C.onDark,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.4), lineHeight: 1,
      }}
    >
      {personaInitials(tutor.name)}
    </span>
  );
}

export function ChildHome({ childName, status, subjects, lastLesson, onOpenSubject, onResume, onStartRules }: Props) {
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
  const tiles = serverTiles(subjects);
  const lastSubject = lastLesson ? tiles.find((t) => t.id === lastLesson.subjectId) : undefined;

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

      {/* Т3e (ревью d1b9dc6e, п.1): карточка — всегда, когда есть последний урок: на 360 сайдбара
          нет и она единственный путь к прежнему уроку. Предмета нет в живом списке (закрыт/удалён
          или список ещё не пришёл) — карточка без метки и цвета предмета: нейтральный квадрат
          (та же пара, что у панели закрытого чата: C.bgInset/C.textSecondary). «Продолжить»
          открывает чат как есть: для закрытого/удалённого предмета сервер отдаёт
          subjectClosed=true (ChildChatsController.cs:102) — чат только для чтения. */}
      {lastLesson && (
        <Island bg={C.bgCard} borderColor={C.borderLight} shadow={SHADOW.card} style={{ borderRadius: R.xl, padding: SP.lg }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.lg, flexWrap: 'wrap' }}>
            {lastSubject ? (
              <SubjectGlyph bg={lastSubject.bg} fg={lastSubject.fg}>{lastSubject.glyph}</SubjectGlyph>
            ) : (
              <SubjectGlyph bg={C.bgInset} fg={C.textSecondary}>
                <BookOpen {...ICON_PROPS} size={ICON_SIZE.md} />
              </SubjectGlyph>
            )}
            <div style={{ flex: 1, minWidth: 200 }}>
              <div style={{ fontSize: FS.sm, color: C.textMuted }}>Вчера остановились тут</div>
              <div style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading, marginTop: 2 }}>
                {lastSubject ? `${lastSubject.label} · ${lastLesson.title}` : lastLesson.title}
              </div>
              {/* Строка репетитора (макет .card): аватар 20 + имя репетитора */}
              {lastSubject?.tutor && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: SP.xs, fontSize: FS.sm, color: C.textMuted, minWidth: 0 }}>
                  <TutorMark tutor={lastSubject.tutor} size={20} />
                  <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {lastSubject.tutor.name}
                  </span>
                </div>
              )}
            </div>
            <Button size="md" variant="primary" fullWidth={isMobile} onClick={onResume}>
              Продолжить
            </Button>
          </div>
        </Island>
      )}

      {/* Подписи без содержимого нет: с сервера может прийти пустой список — тогда
          секции предметов на экране просто не будет (пустую решётку показывать нечем) */}
      {tiles.length > 0 && <div style={{ fontSize: FS.xxs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted }}>
        Предметы
      </div>}
      {/* minmax(0,1fr), а не 1fr: минимум 1fr — min-content содержимого плитки (глиф 40 +
          gap + nowrap-строка репетитора), и на узких планшетах (768) три колонки не сжимаются
          в ширину контейнера — сетка вылезает за правый край (дефект c28b42bf). С нулевым
          минимумом колонки делят ровно ширину контейнера; при 1280 размеры треков те же. */}
      <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : 'repeat(3, minmax(0, 1fr))', gap: SP.md }}>
        {tiles.map((s) => (
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
            <span style={{ minWidth: 0 }}>
              <span style={{ display: 'block', fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>{s.label}</span>
              {s.tutor && (
                <span style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: SP.xs, fontSize: FS.sm, color: C.textMuted, minWidth: 0 }}>
                  <TutorMark tutor={s.tutor} size={20} />
                  <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{s.tutor.name}</span>
                </span>
              )}
            </span>
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
