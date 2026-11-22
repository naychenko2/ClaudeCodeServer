// Г6: выбор предмета ребёнком там, где дока предметов нет — телефон. Раньше отдельного
// экрана не было: детский эффект App сам открывал первый проект (App.tsx, детская ветка),
// и ребёнок с двумя предметами не мог выбрать второй (находка P2 аудита 21.11). Теперь
// ChildSubjectPicker — единственное место, где ребёнок без открытого проекта стоит и
// выбирает: список предметов — GET /api/child/subjects (И5: только включённые, порядок
// профиля), переход — его проект-предмета через onPick (открывает вызывающий: App).
// Ориентиры: макет kids-project-screen.html:329-336 (знак предмета — глиф на цвете палитры),
// ADR-041 решение 84 («док предметов»; плитка — та же строка «знак + имя», что у плиток
// главной до снятия ChildHome, 5d6e61abc).
//
// Состояния: список ещё грузится → «Открываю предметы»; сервер недоступен → спокойный
// «нет связи» с «Попробовать ещё раз»; предметов нет → «Предметов пока нет» (текст и
// пустое состояние переехали сюда из App без изменения). Плитка — только включённые
// предметы: фильтр enabled !== false, тот же, что у дока (правило решения 84).
// Тап без исхода (Г20): missSubjectId от вызывающего — предмет без проекта-предмета;
// над списком — плашка «…пока не открывается / попроси взрослого», пикер остаётся.

import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { BookOpen, Info, WifiOff } from 'lucide-react';
import { C, FONT, FS, R, SP, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
// Button/LoadingScreen — прямыми файл-импортами, не ui/index: тот тянет PanelShell →
// lib/pointer, а он ставит window-слушатель при ИМПОРТЕ модуля — App-тесты без
// fake window (App.childGate и соседние) падали бы на загрузке файла
import { Button } from '../../components/ui/Button';
import { LoadingScreen } from '../../components/ui/LoadingScreen';
import { EmptyState } from '../../components/ui/EmptyState';
import { Notice } from '../../components/ui/Notice';
import { useIsMobile } from '../../lib/breakpoints';
import { childApi, type ChildSubjectWithViewers } from './api';
import { subjectColors } from './subjectPalette';
import type { ChildSubjectTutor } from './types';

// Знак предмета — та же пара «цвет палитры + глиф», что у значков дока и плиток главной.
// Пустой глиф — «?» (фолбэк, общий с доком): плитка без знака не читается.
function SubjectGlyph({ bg, fg, children }: { bg: string; fg: string; children: ReactNode }) {
  return (
    <span style={{
      width: 40, height: 40, flexShrink: 0, borderRadius: R.lg,
      background: bg, color: fg, display: 'grid', placeItems: 'center',
      fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700,
    }}>
      {children}
    </span>
  );
}

// Строка репетитора на плитке — имя текстом: аватар не рисуем (TutorMark закрыт в
// ChildLessonHeader, и дублировать его здесь не тащим; в доке репетитора тоже нет).
function TutorLine({ tutor }: { tutor: ChildSubjectTutor | null }) {
  if (!tutor) return null;
  return (
    <span style={{
      display: 'block', fontSize: FS.sm, color: C.textMuted, minWidth: 0,
      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', marginTop: 2,
    }}>
      {tutor.name}
    </span>
  );
}

export function ChildSubjectPicker({ onPick, missSubjectId }: { onPick: (s: ChildSubjectWithViewers) => void; missSubjectId?: string | null }) {
  const isMobile = useIsMobile();
  const [subjects, setSubjects] = useState<ChildSubjectWithViewers[] | null>(null);
  const [error, setError] = useState(false);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setSubjects(null);
    setError(false);
    childApi.getSubjects()
      .then(list => {
        if (cancelled) return;
        // Только включённые предметы (решение 84), порядок — как с сервера (И5)
        setSubjects(list.filter(s => s.enabled !== false));
      })
      .catch(() => { if (!cancelled) setError(true); });
    return () => { cancelled = true; };
  }, [reloadKey]);

  const retry = useCallback(() => setReloadKey(k => k + 1), []);

  if (error) {
    return (
      <EmptyState
        icon={<WifiOff size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Нет связи с сервером"
        subtitle="Попробуй позже."
        action={(
          <Button size="md" variant="primary" dataTestId="child-subjects-retry" onClick={retry}>
            Попробовать ещё раз
          </Button>
        )}
      />
    );
  }
  if (!subjects) return <LoadingScreen hint="Открываю предметы" />;
  if (subjects.length === 0) {
    return (
      <EmptyState
        icon={<BookOpen size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Предметов пока нет"
        subtitle="Попроси маму или папу настроить твои предметы."
      />
    );
  }

  return (
    <div data-testid="child-subject-picker" style={{
      maxWidth: 760, width: '100%', margin: '0 auto', boxSizing: 'border-box',
      padding: isMobile ? `${SP.lg}px ${SP.lg}px` : `${SP.xxxl}px ${SP.xl}px`,
      display: 'flex', flexDirection: 'column', gap: SP.lg,
    }}>
      <div style={{
        fontFamily: FONT.serif, fontSize: isMobile ? FS.h1 : FS.display,
        fontWeight: 500, lineHeight: 1.2, color: C.textHeading,
      }}>
        Выбери предмет
      </div>
      {/* minmax(0,1fr), а не 1fr: минимум колонки — ноль, а не min-content (глиф 40 + gap +
          nowrap-строка репетитора), иначе на 360 колонка вылезает за правый край
          (тот же дефект c28b42bf, что у плиток главной) */}
      {/* Г20: тап по плитке, у которой нет проекта-предмета (его удалили из воркспейса),
          не должен молчать. Плитку заранее не блокируем: наличие проекта известно только
          из поиска при тапе (его делает вызывающий), поэтому промах — плашка после тапа.
          Тон — существующий детский: «…пока не открывается» + «Попроси взрослого помочь.»
          (тот же, что у «Правила пока не открываются» в воркспейсе предмета). Пикер
          остаётся на экране: ребёнок может выбрать другой предмет. */}
      {missSubjectId && (() => {
        const missed = subjects.find(s => s.id === missSubjectId);
        return missed ? (
          <Notice tone="info" icon={Info} data-testid="child-subject-miss" title={`«${missed.label}» пока не открывается`}>
            Попроси взрослого помочь.
          </Notice>
        ) : null;
      })()}
      <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : 'repeat(3, minmax(0, 1fr))', gap: SP.md }}>
        {subjects.map(s => {
          const { bg, fg } = subjectColors(s.color);
          return (
            <button
              key={s.id}
              type="button"
              data-testid={`child-subject-tile-${s.id}`}
              onClick={() => onPick(s)}
              style={{
                display: 'flex', alignItems: 'center', gap: SP.md, padding: SP.lg, minHeight: 40,
                background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl,
                boxShadow: SHADOW.card, cursor: 'pointer', textAlign: 'left', font: 'inherit', color: 'inherit',
              }}
            >
              <SubjectGlyph bg={bg} fg={fg}>{s.glyph || '?'}</SubjectGlyph>
              <span style={{ minWidth: 0 }}>
                <span style={{ display: 'block', fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>{s.label}</span>
                <TutorLine tutor={s.tutor} />
              </span>
            </button>
         );
        })}
      </div>
    </div>
  );
}
