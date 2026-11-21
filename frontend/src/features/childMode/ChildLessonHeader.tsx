// Ф1h: шапка урока ребёнка (решение Андрея 19.11, разрез §16 «Куратор читает уроки»;
// макет kids-project-screen, .chead). Переезжает из удаляемого ChildLessonChat:
// аватар и имя репетитора, метка предмета — та же разметка, что в его шапке (Т3d).
// Добавляется строка «Уроки видит <куратор>»: куратор видит все уроки дословно, и
// ребёнку это видно ВСЕГДА — спокойная строка с иконкой глаза, без предупреждающего
// цвета (решение Андрея 19.11, п. 2: «скрытого наблюдения нет»).
//
// Чего здесь нет и почему: действия взрослой шапки (переименование, архив, расход) —
// взрослая ChatHeaderBar их не получает вовсе: при childFeedKinds ChatPanel рисует
// вместо неё эту шапку. Меток «закрыт» и «Помощник · программа» тоже нет: они были
// частью прежней оболочки, в новой их место решает не этот шаг.
//
// На мобиле остальная шапка свёрнута (как в прежней оболочке: имя и метку несёт
// родительский хаб), но строка кураторов остаётся видимой — для неё исключение,
// прямо названное в решении.

import { useState } from 'react';
import { Eye } from 'lucide-react';
import { C, CHAT_GUTTER_MOBILE, CHAT_MAX_W, FONT, FS, R, SP } from '../../lib/design';
import { Badge } from '../../components/ui/Badge';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { userAvatarColor } from '../../components/ui/UserAvatar';
import { cachedToken } from '../../lib/offline';
import { personaInitials } from '../../lib/personas';
import { subjectColors } from './subjectPalette';
import { tutorAvatarUrl } from './api';
import type { ChildSubjectTutor, ChildSubjectView } from './types';

// Строка кураторов (решение Андрея 19.11): один — «Уроки видит Папа», двое — «Уроки
// видят Папа и Мама», трое и больше — «Папа, Мама и Бабушка» (запятые, перед
// последним — «и»). Пустой список — строки нет вовсе: «никого» ребёнку не о чем
// говорить (записка Майи к макету: пустого «Эту папку видят:» не бывает).
// Имена — viewers ответа GET /api/child/subjects (Б14a; клиент — childApi.getSubjects,
// Ф1e), порядок — порядок кураторов профиля, мы его не меняем.
export function curatorsLine(names: readonly string[]): string | null {
  if (names.length === 0) return null;
  const verb = names.length === 1 ? 'видит' : 'видят';
  const joined = names.length === 1 ? names[0] : `${names.slice(0, -1).join(', ')} и ${names[names.length - 1]}`;
  return `Уроки ${verb} ${joined}`;
}

// Данные шапки для вызывающего (ChatPanel при childFeedKinds): их собирает владелец
// экрана — он же грузит предметы ребёнка (childApi.getSubjects, Ф1e) для дока.
export interface ChildLessonHeaderData {
  // Предмет чата (Т3c): view — живой предмет; null — закрытый или до-предметный чат
  subject: ChildSubjectView | null;
  // Имена кураторов (viewers предмета); нет поля или пусто — строки нет
  viewers?: string[];
}

interface Props extends ChildLessonHeaderData {
  // Имя чата — запасной заголовок, когда репетитора у чата нет (закрытый/до-предметный)
  title: string;
  // Узкий экран или нет — единственный источник: проп владельца экрана через ChatPanel
  // (тот же путь, что у FeatureChatCard/ChatHeaderBar). Внутренний useIsMobile здесь
  // был вторым источником и мог разойтись с раскладкой родителя (ревью d52edd91).
  isMobile: boolean;
}

// Предмет шапки: цвет — из палитры ключа (Т3b). null — закрытый/до-предметный чат:
// метки предмета нет (тот же разбор, что headerSubject в ChildLessonChat).
interface HeaderSubject { label: string; glyph: string; bg: string; fg: string }
function headerSubject(subject: ChildSubjectView | null): HeaderSubject | null {
  if (!subject) return null;
  return { label: subject.label, glyph: subject.glyph || '?', ...subjectColors(subject.color) };
}

// Знак репетитора (макет .pav): круг R.full. Есть аватар — картинка /api/personas/{id}/avatar
// (hasAvatar=true ⟺ ручка отдаёт картинку; пропала с диска — 404, и знак возвращается к
// инициалам: onError, тот же приём, что у PersonaAvatar). Нет аватара — инициалы имени на
// стабильном цвете по id персоны (тот же ключ цвета, что у ui/UserAvatar: тёзки не сливаются).
// Репетитор null (висящий ключ персоны) — этот компонент не зовут вовсе.
// Перенесён 1:1 из удалённого ChildHome (Ф1g): шапка урока — единственный его потребитель.
// data-testid: знак проверяют тесты шапки (присутствие на десктопе, отсутствие на мобиле).
function TutorMark({ tutor, size }: { tutor: ChildSubjectTutor; size: number }) {
  const [imgFailed, setImgFailed] = useState(false);
  const round = { width: size, height: size, borderRadius: R.full, flexShrink: 0 } as const;
  if (tutor.hasAvatar && !imgFailed) {
    return (
      <img
        data-testid="tutor-mark"
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
      data-testid="tutor-mark"
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

export function ChildLessonHeader({ subject, viewers, title, isMobile }: Props) {
  const seen = curatorsLine(viewers ?? []);
  const view = headerSubject(subject);

  // Строка кураторов: тон textSecondary, иконка Eye, без предупреждающего цвета —
  // это спокойная правда о приватности, а не сигнал тревоги. Длинный список — одна
  // строка с обрезкой, полный текст в title (тот же приём, что у плашки дока Ф1e).
  // Текст — в собственном span: на flex-контейнере textOverflow не работает (текст
  // становится анонимным flex-элементом) — длинная строка обрезалась без «…» или
  // вылезала за край (ревью d52edd91).
  const line = (
    <span
      data-testid="child-curators-line"
      style={{
        display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0,
        color: C.textSecondary, fontSize: FS.sm,
      }}
    >
      <Eye size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} aria-hidden />
      <span
        title={seen ?? undefined}
        style={{ minWidth: 0, overflow: 'hidden', whiteSpace: 'nowrap', textOverflow: 'ellipsis' }}
      >
        {seen}
      </span>
    </span>
  );

  // Мобильная раскладка: остальная шапка свёрнута (имя и метку несёт родительский
  // хаб), строка кураторов — нет: она видна всегда (решение Андрея 19.11, п. 2).
  // Кураторов нет — показывать нечего, и шапки на мобиле нет вовсе.
  if (isMobile) {
    if (!seen) return null;
    return (
      <header
        data-testid="child-lesson-header"
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm,
          maxWidth: CHAT_MAX_W, margin: '0 auto', boxSizing: 'border-box',
          padding: `${SP.xs}px ${CHAT_GUTTER_MOBILE}px`,
        }}
      >
        {line}
      </header>
    );
  }

  // Десктоп: коробка шапки = колонка ленты — ровно та же разметка, что у взрослой
  // hero-ветки (ChatHeaderBar.tsx:2345-2347) и у contentRef ленты (ChatPanel.tsx:2878):
  // CHAT_MAX_W по центру, БЕЗ бокового паддинга. Поле CHAT_GUTTER_L живёт внутри
  // коробки прокрутки как её paddingLeft (ChatPanel.tsx:2863, chatGutter.ts:28-37), а
  // содержимое ленты — у краёв колонки; боковой паддинг внутри колонки (бывший SP.xl
  // из самостоятельной оболочки ChildLessonChat.tsx:216-217, где лента носила тот же
  // отступ) сдвигал заголовок на 24px правее сообщений (ревью d52edd91).
  const col = { width: '100%', maxWidth: CHAT_MAX_W, margin: '0 auto', boxSizing: 'border-box' } as const;
  return (
    <header
      data-testid="child-lesson-header"
      style={{ ...col, paddingTop: SP.lg, paddingBottom: SP.md, display: 'flex', alignItems: 'center', gap: SP.md }}
    >
      {view && subject?.tutor && <TutorMark tutor={subject.tutor} size={52} />}
      <div style={{ minWidth: 0 }}>
        <h1 title={subject?.tutor?.name ?? title} style={{
          margin: 0, fontFamily: FONT.serif, fontSize: FS.h1, fontWeight: 500, lineHeight: 1.25,
          color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>{subject?.tutor?.name ?? title}</h1>
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginTop: SP.xs }}>
          {view && (
            // Полный текст метки — в title: при обрезке (внутренний span Badge) имя
            // предмета всё равно можно прочитать (ревью d52edd91)
            <Badge tone="neutral" title={view.label}>
              <span aria-hidden style={{
                width: 22, height: 22, borderRadius: R.sm, flexShrink: 0, background: view.bg, color: view.fg,
                display: 'grid', placeItems: 'center', fontFamily: FONT.serif, fontSize: FS.xs, fontWeight: 700,
              }}>{view.glyph}</span>
              {view.label}
            </Badge>
          )}
        </div>
      </div>
      {/* Строка — в правом краю шапки, в слоте прежней пометки стоп-сигнала (макет
          kids-project-screen, .chead): там ребёнок и должен её видеть. */}
      {seen && <span style={{ marginLeft: 'auto', flexShrink: 1, minWidth: 0, display: 'flex' }}>{line}</span>}
    </header>
  );
}
