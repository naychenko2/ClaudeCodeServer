// Мягкое приглашение к знакомству (фича default-personas-onboarding): не гейт, а
// закрываемая карточка в общем потоке. Показывается только владельцу проекта и только
// пока defaultPersonaId пуст; отказ записывается в localStorage, успешное знакомство
// гасит карточку само (defaultPersonaId больше не null). Серверу поля не нужно —
// цена отказа одна и та же: повтор на другом устройстве.
// Две раскладки: десктоп — горизонтальная полоска между HubHeader и DesktopWorkspace;
// мобиль — компактная карточка над списком вкладок (по аналогии с PersonasPage).

import { useCallback, useState } from 'react';
import { Sparkles } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button } from '../../components/ui';
import { useFeature, FLAGS } from '../../lib/featureFlags';
import { useMe } from '../../lib/defaultPersona';
import { OPEN_INTRO_EVENT } from '../onboarding/OnboardingPage';

// Ключ отказа: привязан к проекту. Префикс «cc_» — общий канон локальных ключей (см. lib/workspaceState).
const dismissedKey = (projectId: string) => `cc_project_intro_dismissed:${projectId}`;

interface Props {
  projectId: string;
  // Владелец определяется снаружи и пробрасывается пропсом, чтобы не тащить auth и project целиком.
  projectOwnerId?: string | null;
  // Актуальный defaultPersonaId (из state эффекта на api.projects.list): в WorkspacePage
  // отслеживается отдельно от project.defaultPersonaId, потому что объект из localStorage
  // о поле может не знать. null/undefined — «ещё не назначен».
  defaultPersonaId?: string | null;
  isMobile: boolean;
}

// Сама решает, показываться ли: все условия должны сойтись. Если карточка не нужна —
// возвращает null, чтобы родителю не пришлось вешать обёртку-условие.
export function ProjectIntroCard({ projectId, projectOwnerId, defaultPersonaId, isMobile }: Props) {
  const onboardingOn = useFeature(FLAGS.defaultPersonasOnboarding);
  const me = useMe();
  // Локальный «закрыт» пишем в state, чтобы после «Позже» карточка ушла сразу же —
  // localStorage уже содержит значение, но ререндер всё равно нужен. После монтирования
  // тоже ориентируемся на localStorage: reload страницы не должен показать карточку обратно.
  const [dismissed, setDismissed] = useState<boolean>(() => {
    try { return localStorage.getItem(dismissedKey(projectId)) === '1'; } catch { return false; }
  });

  // Только владелец. !ownerId — владелец по умолчанию (старые проекты без поля)
  const isOwner = !projectOwnerId || (!!me.userId && projectOwnerId === me.userId);
  const hasLead = !!defaultPersonaId;
  const show = onboardingOn && isOwner && !hasLead && !dismissed;
  if (!show) return null;

  const handleMeet = () => {
    window.dispatchEvent(new CustomEvent(OPEN_INTRO_EVENT, { detail: { projectId } }));
  };
  const handleLater = useCallback(() => {
    try { localStorage.setItem(dismissedKey(projectId), '1'); } catch { /* localStorage недоступен — карточка вернётся при следующем заходе, это терпимо */ }
    setDismissed(true);
  }, [projectId]);

  return isMobile ? (
    <div style={mobileCard}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Sparkles size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent, flexShrink: 0 }} />
        <div style={mobileTitle}>У проекта пока нет руководителя</div>
      </div>
      <div style={mobileText}>
        Расскажите о проекте — по короткому интервью появится его руководитель: персона по умолчанию для чатов этого проекта.
      </div>
      <Button variant="primary" size="md" fullWidth onClick={handleMeet}
        leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
        Познакомиться
      </Button>
      <Button variant="ghost" size="md" fullWidth onClick={handleLater}>
        Позже
      </Button>
    </div>
  ) : (
    <div style={desktopBar}>
      <div style={desktopBarIcon}>
        <Sparkles size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent }} />
      </div>
      <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <div style={desktopTitle}>У проекта пока нет руководителя</div>
        <div style={desktopText}>
          Расскажите о проекте — по короткому интервью появится его руководитель: персона по умолчанию для чатов этого проекта.
        </div>
      </div>
      <div style={{ flexShrink: 0, display: 'flex', gap: SP.sm }}>
        <Button variant="ghost" size="md" onClick={handleLater}>Позже</Button>
        <Button variant="primary" size="md" onClick={handleMeet}
          leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
          Познакомиться
        </Button>
      </div>
    </div>
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
