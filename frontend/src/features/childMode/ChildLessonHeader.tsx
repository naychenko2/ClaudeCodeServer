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
// Мобила (Г5, находка P1 аудита 21.11): шапка урока рабочая, как в макете
// (kids-project-screen.html:343-352, .mtool/.mwatch): ряд 1 — «назад», знак
// репетитора, имя с шевроном (переключатель уроков: тап по имени ведёт к списку
// уроков, где переключение), урок в мете; ряд 2 — строка кураторов и плашка
// времени. До Г5 оставалась только строка кураторов, а без кураторов шапка
// исчезала целиком — штатного возврата к списку уроков не было ни у ребёнка,
// ни у взрослого.

import { useRef, useState } from 'react';
import { Bell, ChevronDown, ChevronLeft, Eye, OctagonAlert } from 'lucide-react';
import { C, CHAT_GUTTER_MOBILE, CHAT_MAX_W, FONT, FS, R, SP, TB } from '../../lib/design';
import { Badge } from '../../components/ui/Badge';
import { IconButton } from '../../components/ui/IconButton';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { userAvatarColor } from '../../components/ui/UserAvatar';
import { cachedToken } from '../../lib/offline';
import { personaInitials } from '../../lib/personas';
import { showToast } from '../../lib/toast';
import { childApi, tutorAvatarUrl } from './api';
import { ChildTimeBadge } from './ChildTimeBadge';
import { subjectColors } from './subjectPalette';
import type { ChildStatus, ChildSubjectTutor, ChildSubjectView } from './types';

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
// экрана — он же грузит предметы ребёнка (childApi.getSubjects, Ф1e) для дока и держит
// снимок времени (ChildStatusBridge).
export interface ChildLessonHeaderData {
  // Предмет чата (Т3c): view — живой предмет; null — закрытый или до-предметный чат
  subject: ChildSubjectView | null;
  // Имена кураторов (viewers предмета); нет поля или пусто — строки нет
  viewers?: string[];
  // Снимок времени ребёнка (childEff владельца экрана): компактная плашка времени
  // во втором ряду мобильной шапки. Сама плашка молчит вне ok/warn (ChildTimeBadge),
  // не задан — плашки нет. Десктопу время в шапке не нужно: его показывает полоса
  // над уроком (WorkspacePage, ChildTimeBadge без compact) — здесь не дублируем.
  time?: ChildStatus | null;
}

interface Props extends ChildLessonHeaderData {
  // Имя чата — запасной заголовок, когда репетитора у чата нет (закрытый/до-предметный)
  title: string;
  // Узкий экран или нет — единственный источник: проп владельца экрана через ChatPanel
  // (тот же путь, что у FeatureChatCard/ChatHeaderBar). Внутренний useIsMobile здесь
  // был вторым источником и мог разойтись с раскладкой родителя (ревью d52edd91).
  isMobile: boolean;
  // Возврат к списку уроков — тот же onBack, что WorkspacePage кладёт в ChatPanel
  // (backFromChat: на мобиле — к панели уроков). Не задан — кнопки «назад» и
  // переключателя нет.
  onBack?: () => void;
  // id чата — адресат стоп-сигнала (childApi.stopSignal, ручка №9 ADR-041).
  // Не задан — стоп-сигнала нет.
  sessionId?: string;
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
// data-testid: знак проверяют тесты шапки (десктоп — в hero, мобила — в шапке урока).
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

// Стоп-сигнал шапки урока (Г5, макет kids-project-screen .stopsig, aria-label из
// макета): тот же POST /api/child/stop-signal, что у панели «Как мы учимся»
// (ChildHintsPanel, KM-F7): 202 и 429 too_frequent — «сигнал ушёл»; другой отказ —
// тост, и кнопка снова доступна (повтор — за ребёнком). Состояние по sessionId:
// смена урока до ответа — старое состояние не всплывает (тот же приём, что у
// sentFor в ChildHintsPanel).
function StopSignalButton({ sessionId }: { sessionId: string }) {
  const [sending, setSending] = useState(false);
  const [sentFor, setSentFor] = useState<string | null>(null);
  // Блокировка «один запрос» — ref: где клик синтетический (быстрый двойной, dispatch
  // из теста), disabled не удержит (тот же приём, что inFlight в ChildHintsPanel).
  const inFlight = useRef(false);

  async function send() {
    if (inFlight.current) return;
    inFlight.current = true;
    setSending(true);
    const sid = sessionId;
    try {
      await childApi.stopSignal(sid);
      setSentFor(sid);
    } catch (e) {
      const err = e as { status?: number; body?: { code?: string } } | null;
      if (err?.status === 429 && err?.body?.code === 'too_frequent') {
        // Сервер схлопнул повтор — сигнал уже ушёл (ADR-041: «не чаще раза в минуту
        // на чат»), ребёнку то же состояние, что и при 202.
        setSentFor(sid);
      } else {
        // В шапке места нет ни заметке, ни плашке — отказ говорит тост; кнопка снова
        // доступна, повтор — за ребёнком.
        showToast('Не получилось отправить', 'Сигнал взрослому не ушёл. Попробуй ещё раз.', 'error');
      }
    } finally {
      inFlight.current = false;
      setSending(false);
    }
  }

  const sent = sentFor !== null && sentFor === sessionId;
  if (sent) {
    // «Ушёл» — иконка колокольчика, кнопка не активна: повтор не нужен, сервер
    // всё равно схлопнул бы (429 too_frequent).
    return (
      <IconButton size="lg" disabled ariaLabel="Сигнал отправлен" color={C.textSecondary}>
        <Bell size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
      </IconButton>
    );
  }
  return (
    <IconButton
      size="lg"
      disabled={sending}
      ariaLabel="Мне неприятно — позвать взрослого"
      color={C.dangerText}
      onClick={send}
    >
      <OctagonAlert size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
    </IconButton>
  );
}

export function ChildLessonHeader({ subject, viewers, time, title, isMobile, onBack, sessionId }: Props) {
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

  // Мобильная раскладка (Г5, макет .mtool/.mwatch): рабочая шапка урока целиком,
  // не зависящая от наличия кураторов (P1: раньше шапка исчезала целиком). Ряд 1:
  // «назад» (возврат к списку уроков), знак репетитора и имя с шевроном — тап по
  // нему тоже ведёт к списку уроков (переключатель уроков: список там, где
  // переключение, своей шторки у шапки нет). Ряд 2: строка кураторов и плашка
  // времени; ряда нет вовсе, когда нечего показать — но шапка (ряд 1) есть всегда.
  if (isMobile) {
    // Мета под именем — имя урока; без репетитора (закрытый/до-предметный чат) имя
    // чата уже в заголовке, дубль в мете не нужен.
    const lessonName = subject?.tutor ? title : null;
    return (
      <header data-testid="child-lesson-header" style={{ flexShrink: 0 }}>
        <div
          style={{
            display: 'flex', alignItems: 'center', gap: SP.sm,
            maxWidth: CHAT_MAX_W, margin: '0 auto', boxSizing: 'border-box',
            minHeight: TB.heightMobile,
            padding: `${SP.sm}px ${CHAT_GUTTER_MOBILE}px`,
            borderBottom: `1px solid ${C.borderLight}`,
          }}
        >
          {onBack && (
            <IconButton size="lg" onClick={onBack} ariaLabel="К списку уроков" style={{ marginLeft: -SP.sm }}>
              <ChevronLeft size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
            </IconButton>
          )}
          {/* Переключатель уроков: тап по знаку/имени урока — к списку уроков (onBack).
              Chevron внизу имени — из макета (.nm с ic('down',14)): список уроков
              открывается в шторке списка, не под именем. */}
          <button
            type="button"
            data-testid="child-lesson-switcher"
            onClick={onBack}
            aria-label="К списку уроков"
            style={{
              display: 'flex', alignItems: 'center', gap: SP.sm,
              flex: 1, minWidth: 0,
              minHeight: TB.iconHitMobile,
              background: 'none', border: 'none', padding: 0,
              cursor: 'pointer', textAlign: 'left', fontFamily: FONT.sans,
            }}
          >
            {view && subject?.tutor && <TutorMark tutor={subject.tutor} size={28} />}
            <span style={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
              <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                {/* Имя репетитора — переносом, а не ellipsis (дефект QA-8 c7082105): автоимя
                    «Репетитор: Математика» на 360 не влезало в ряд с «назад» и стоп-сигналом
                    (scrollWidth 200 при clientWidth 186) и резалось многоточием — ребёнок не
                    видел, к какому репетитору открыт урок. Многоточие вместо целиком читаемого
                    имени у детской шапки недопустимо; имя урока в мете ниже остаётся обрезанным
                    с полным текстом в title — как в макете (.meta). */}
                <span
                  style={{
                    fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 600,
                    color: C.textHeading, minWidth: 0,
                    whiteSpace: 'normal', overflowWrap: 'anywhere',
                  }}
                >
                  {subject?.tutor?.name ?? title}
                </span>
                <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, color: C.textMuted }} aria-hidden />
              </span>
              <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                {lessonName && (
                  <span
                    title={title}
                    style={{
                      fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted, minWidth: 0,
                      overflow: 'hidden', whiteSpace: 'nowrap', textOverflow: 'ellipsis',
                    }}
                  >
                    {title}
                  </span>
                )}
              </span>
            </span>
          </button>
          {sessionId && <StopSignalButton sessionId={sessionId} />}
        </div>
        {(seen || time) && (
          <div
            style={{
              display: 'flex', alignItems: 'center', gap: SP.sm,
              maxWidth: CHAT_MAX_W, margin: '0 auto', boxSizing: 'border-box',
              padding: `${SP.xs}px ${CHAT_GUTTER_MOBILE}px`,
              borderBottom: `1px solid ${C.border}`,
            }}
          >
            {seen && line}
            {time && (
              <span style={{ marginLeft: 'auto', display: 'flex', flexShrink: 0 }}>
                <ChildTimeBadge status={time} compact />
              </span>
            )}
          </div>
        )}
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
      style={{ ...col, paddingTop: SP.lg, paddingBottom: SP.md, display: 'flex', alignItems: 'center', gap: SP.md, flexWrap: 'wrap' }}
    >
      {view && subject?.tutor && <TutorMark tutor={subject.tutor} size={52} />}
      {/* Блок имени — flex:1 + min-width:240 из макета (.hero .tt): имя не сжимается
          в ряду с действиями справа; при тесноте ряд переносится (flexWrap ниже) и
          правый кластер уходит на вторую строку (L2 аудита 21.11). */}
      <div style={{ flex: 1, minWidth: 240 }}>
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
