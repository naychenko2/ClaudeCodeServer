// Мягкое приглашение к знакомству (фича default-personas-onboarding): не гейт, а
// закрываемая карточка в общем потоке. Показывается только владельцу проекта.
//
// Два варианта:
//   1. У проекта ещё нет руководителя (defaultPersonaId пуст) — "Познакомиться"
//      открывает онбординг, чтобы завести персону; "Позже" гасит карточку.
//   2. Руководитель есть, но каркас пресета ещё не разложен (presetKey === 'pending') —
//      "Продолжить знакомство" открывает онбординг, "Позже" гасит свой (отдельный
//      от варианта 1) флаг, чтобы можно было вернуться к знакомству позже.
//
// Раскладки: десктоп — горизонтальная полоска; мобиль — компактная карточка.

import { useCallback, useMemo, useState } from 'react';
import { Sparkles } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, Modal } from '../../components/ui';
import { useMe } from '../../lib/defaultPersona';
import { usePersonas, personaLabel } from '../../lib/personas';
import { makeLead } from '../personas/TeamCommandCenter';
import { PersonaAvatar } from '../personas/PersonaAvatar';
import { OPEN_INTRO_EVENT } from '../onboarding/OnboardingPage';

// Ключи отказа — РАЗДЕЛЬНЫЕ для двух вариантов карточки: «познакомиться» и
// «разложить каркас» — отдельные намерения, повторно показывать должны независимо.
// Префикс «cc_» — общий канон локальных ключей (см. lib/workspaceState).
const dismissedIntroKey = (projectId: string) => `cc_project_intro_dismissed:${projectId}`;
const dismissedScaffoldKey = (projectId: string) => `cc_project_scaffold_dismissed:${projectId}`;

interface Props {
  projectId: string;
  // Владелец определяется снаружи и пробрасывается пропсом, чтобы не тащить auth и project целиком.
  projectOwnerId?: string | null;
  // Актуальный defaultPersonaId (из state эффекта на api.projects.list): в WorkspacePage
  // отслеживается отдельно от project.defaultPersonaId, потому что объект из localStorage
  // о поле может не знать. null/undefined — «ещё не назначен».
  defaultPersonaId?: string | null;
  // Состояние каркаса: 'pending' — лидер заговорил, но человек не выбрал пресет;
  // 'none' / 'docs' / 'dev' / 'personal' — каркас разложен или человек отказался;
  // null — проект создан до фичи, к каркасу возвращаться не нужно.
  presetKey?: string | null;
  isMobile: boolean;
}

// Сама решает, показываться ли: всегда только владельцу. Вариант выбирается по двум
// независимым условиям — наличию руководителя и состоянию каркаса. Если карточка
// не нужна — возвращает null, чтобы родителю не пришлось вешать обёртку-условие.
export function ProjectIntroCard({ projectId, projectOwnerId, defaultPersonaId, presetKey, isMobile }: Props) {
  const me = useMe();
  // Локальный «закрыт» пишем в state, чтобы после «Позже» карточка ушла сразу же —
  // localStorage уже содержит значение, но ререндер всё равно нужен. После монтирования
  // тоже ориентируемся на localStorage: reload страницы не должен показать карточку обратно.
  const [introDismissed, setIntroDismissed] = useState<boolean>(() => {
    try { return localStorage.getItem(dismissedIntroKey(projectId)) === '1'; } catch { return false; }
  });
  const [scaffoldDismissed, setScaffoldDismissed] = useState<boolean>(() => {
    try { return localStorage.getItem(dismissedScaffoldKey(projectId)) === '1'; } catch { return false; }
  });
  // Пикер «Выбрать из команды» — отдельный state: открывается по клику на новую
  // кнопку в showIntro-варианте, закрывается при выборе или отмене. Держим здесь,
  // а не в TeamCommandCenter, чтобы карточка оставалась самодостаточной (у неё
  // свой сценарий «первого назначения», не требующий открывать командный центр).
  const [pickerOpen, setPickerOpen] = useState(false);

  // Список персон проекта — тот же источник и фильтр, что в TeamCommandCenter
  // (usePersonas + scope === 'project' && projectId === ...). Перерендер
  // сработает и при приходе personas_changed (realtime, см. lib/personas.ts).
  const personas = usePersonas();
  const team = useMemo(
    () => personas.filter(p => p.scope === 'project' && p.projectId === projectId),
    [personas, projectId],
  );

  // Только владелец. !ownerId — владелец по умолчанию (старые проекты без поля)
  const isOwner = !projectOwnerId || (!!me.userId && projectOwnerId === me.userId);
  const hasLead = !!defaultPersonaId;
  const scaffoldPending = presetKey === 'pending';
  // Кнопку «Выбрать из команды» показываем только когда команда непуста и это
  // вариант «нет руководителя». При пустой команде единственный путь — онбординг.
  const canPickFromTeam = !hasLead && team.length > 0;

  // Вариант 1: нет руководителя (и закрыли не «Позже» в этом виде карточки)
  const showIntro = isOwner && !hasLead && !introDismissed;
  // Вариант 2: руководитель есть, но каркас не разложен (и не закрыли «Позже» в этом виде)
  const showScaffold = isOwner && hasLead && scaffoldPending && !scaffoldDismissed;

  // Хуки должны быть выше любого условного return — иначе React считает разное
  // число хуков между рендерами и падает на «Rendered more hooks than during the previous render».
  const handleMeet = () => {
    window.dispatchEvent(new CustomEvent(OPEN_INTRO_EVENT, { detail: { projectId } }));
  };
  const handleLaterIntro = useCallback(() => {
    try { localStorage.setItem(dismissedIntroKey(projectId), '1'); } catch { /* localStorage недоступен — карточка вернётся при следующем заходе, это терпимо */ }
    setIntroDismissed(true);
  }, [projectId]);
  const handleLaterScaffold = useCallback(() => {
    try { localStorage.setItem(dismissedScaffoldKey(projectId), '1'); } catch { /* см. выше */ }
    setScaffoldDismissed(true);
  }, [projectId]);
  // Закрываем модалку сразу при выборе — карточка исчезнет сама, когда бэк пришлёт
  // personas_changed action='default' и App.tsx/WorkspacePage прокинет обновлённый
  // defaultPersonaId в проп (см. App.tsx:348-360, WorkspacePage.tsx:971-973).
  const handlePickFromTeam = useCallback((personaId: string) => {
    const p = team.find(t => t.id === personaId);
    setPickerOpen(false);
    if (!p) return;
    void makeLead(p);
  }, [team]);

  if (!showIntro && !showScaffold) return null;

  // Тексты — для каждого варианта свои. Кнопка действия открывает тот же онбординг:
  // во 2-м варианте она не «продолжить знакомство с лидером», а возобновляет знакомство
  // — оттуда же подтянется и предложение каркаса (если ещё актуально).
  const isScaffold = showScaffold;
  const title = isScaffold ? 'Разложить проект по полочкам?' : 'У проекта пока нет руководителя';
  const body = isScaffold
    ? 'Расскажите, чем занят проект, — предложу папки и правила под его тип.'
    : 'Расскажите о проекте — по короткому интервью появится его руководитель: персона по умолчанию для чатов этого проекта.';
  const primaryLabel = isScaffold ? 'Продолжить знакомство' : 'Познакомиться';

  return (
    <>
      {isMobile ? (
        <div style={mobileCard}>
          <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
            <Sparkles size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
            <div style={mobileTitle}>{title}</div>
          </div>
          <div style={mobileText}>{body}</div>
          <Button variant="primary" size="md" fullWidth onClick={handleMeet}
            leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
            {primaryLabel}
          </Button>
          {/* Кнопка только в showIntro-варианте (не в showScaffold), и только при непустой
              команде: в пустой «Выбрать из команды» бессмысленно — онбординг единственный путь. */}
          {canPickFromTeam && (
            <Button variant="ghost" size="md" fullWidth onClick={() => setPickerOpen(true)}>
              Выбрать из команды
            </Button>
          )}
          <Button variant="ghost" size="md" fullWidth onClick={isScaffold ? handleLaterScaffold : handleLaterIntro}>
            Позже
          </Button>
        </div>
      ) : (
        <div style={desktopBar}>
          <div style={desktopBarIcon}>
            <Sparkles size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent }} />
          </div>
          <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
            <div style={desktopTitle}>{title}</div>
            <div style={desktopText}>{body}</div>
          </div>
          <div style={{ flexShrink: 0, display: 'flex', gap: SP.sm }}>
            <Button variant="ghost" size="md" onClick={isScaffold ? handleLaterScaffold : handleLaterIntro}>
              Позже
            </Button>
            {/* Тот же гейт, что в мобиле: показываем только в showIntro и при непустой команде. */}
            {canPickFromTeam && (
              <Button variant="ghost" size="md" onClick={() => setPickerOpen(true)}>
                Выбрать из команды
              </Button>
            )}
            <Button variant="primary" size="md" onClick={handleMeet}
              leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
              {primaryLabel}
            </Button>
          </div>
        </div>
      )}
      {/* Модалка-пикер — общая для обеих раскладок, Modal сам адаптируется под
          ширину окна (центрированная карточка на десктопе / шторка на мобиле).
          Только в showIntro: в showScaffold руководитель уже есть, и кнопки нет. */}
      {canPickFromTeam && pickerOpen && (
        <Modal title="Выбрать из команды"
          subtitle="Назначить руководителем проекта — будет дефолт-персоной чатов этого проекта."
          onClose={() => setPickerOpen(false)}>
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            {team.map(p => (
              <button key={p.id} onClick={() => handlePickFromTeam(p.id)}
                style={pickerRow}>
                <PersonaAvatar persona={p} size={32} />
                <span style={pickerLabel}>{personaLabel(p)}</span>
              </button>
            ))}
          </div>
        </Modal>
      )}
    </>
  );
}

// Стили — близнецы mobileInviteCard из PersonasPage, чтобы карточки были узнаваемы
// как «приглашение к знакомству». Внешние отступы — отдельно, чтобы родитель сам
// управлял зазорами.
const mobileCard: React.CSSProperties = {
  flex: 'none', display: 'flex', flexDirection: 'column', alignItems: 'stretch', gap: SP.sm,
  background: C.accentLight, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: SP.md,
};
const mobileTitle: React.CSSProperties = {
  fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 600, color: C.textHeading, lineHeight: 1.3,
};
const mobileText: React.CSSProperties = { fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 };

// Десктоп — горизонтальная полоса между шапкой и рельсой. accentLight повторяет
// «пригласительную» палитру, чтобы карточки в разделах выглядели родственными.
const desktopBar: React.CSSProperties = {
  flex: 'none', display: 'flex', alignItems: 'center', gap: SP.md,
  background: C.accentLight, border: `1px solid ${C.border}`, borderRadius: R.xl,
  padding: `${SP.sm}px ${SP.md}px`,
  margin: `${SP.sm}px ${SP.lg}px 0`,
};
const desktopBarIcon: React.CSSProperties = {
  width: 36, height: 36, flexShrink: 0,
  borderRadius: R.md, background: C.bgWhite, border: `1px solid ${C.border}`,
  display: 'flex', alignItems: 'center', justifyContent: 'center',
};
const desktopTitle: React.CSSProperties = {
  fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 600, color: C.textHeading, lineHeight: 1.3,
};
const desktopText: React.CSSProperties = { fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 };

// Стили пикера «Выбрать из команды» — близнецы memberCard из TeamCommandCenter:
// плоская кнопка-строка с аватаром и подписью, та же плотность и скругление.
// Hover не окрашиваем (CSS-inline без состояний) — палитра сама подсказывает
// кликабельность: фон белый, граница светлее фона карточки.
const pickerRow: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', textAlign: 'left',
  background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
  padding: '10px 12px', cursor: 'pointer', fontFamily: FONT.sans,
};
const pickerLabel: React.CSSProperties = {
  fontSize: FS.base, fontWeight: 600, color: C.textHeading,
  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0, flex: 1,
};
