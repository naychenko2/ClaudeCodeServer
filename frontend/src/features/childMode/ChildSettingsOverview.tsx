// Ф2b: раздел «Обзор» окна настроек ребёнка (макет docs/mockups/kids-settings-window.html,
// secOverview + .tiles/.tile).
//
// Что читается и откуда: всё — из профиля GET child-profile (ChildSettingsProfile),
// второго запроса нет. Поэтому:
//  • «Время» — НАСТРОЕННОЕ время профиля (дневной лимит и окна). Фактический расход
//    минут ребёнка держит его собственный снимок /api/child/status (per-owner,
//    админской ручки у него нет), в этом окне его нет — макет показывает «18 мин из 45»,
//    здесь стоит «45 мин в день»: расхождение названо в отчёте шага.
//  • «Согласие» — одно на профиль (решение 26): acknowledgeRouteRisk, автор и момент —
//    routeRiskAcknowledgedAt/By. Согласия предмета в профиле больше нет (ред. 7 §13).
//  • «Опекуны» — guardians[] {userId, sharedProjectsFlag} и рубильник инстанса
//    sharedProjectsInstanceEnabled (решение 60). Окно показывает, у кого флаг не включён;
//    сам флаг включает человек (docs/architecture/api.md:366-374), не сервер.
// Имена опекунов приходят извне (userNames от вызывающего): в ответе профиля только id.
//
// «N предмета ждут согласия» макета здесь не строится: после ред. 7 §13 маршрут предмета
// живёт в персоне проекта-предмета, в профиле его нет — про «ждут» окно сказать нечего,
// есть только общее согласие профиля (оно — своей плиткой).

import { BookOpen, ShieldAlert, Users } from 'lucide-react';
import { useIsMobileModal } from '../../components/ui/useIsMobileModal';
import { Badge } from '../../components/ui/Badge';
import { Button } from '../../components/ui/Button';
import { EmptyState } from '../../components/ui/EmptyState';
import { Notice } from '../../components/ui/Notice';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { ChildSettingsProfile, TimeWindow } from './types';
import type { ChildSettingsSection } from './ChildSettingsWindow';

interface Props {
  profile: ChildSettingsProfile;
  // Имена опекунов по id — от вызывающего (у него список пользователей). Без имени — id.
  userNames?: Record<string, string>;
  // Плитки и плашки ведут человека в тот раздел, где это правится; навигацией владеет окно.
  openSection: (s: ChildSettingsSection) => void;
}

const nameOf = (userId: string, names?: Record<string, string>) => (names && names[userId]) || userId;

// Дата согласия — тот же локальный формат, что у соседних файлов каталога:
// dd.mm.yyyy по полям самой строки, без перевода в пояс процесса.
const fmtDate = (iso: string): string => {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}.${p(d.getMonth() + 1)}.${d.getFullYear()}`;
};

const plural = (n: number, one: string, few: string, many: string) => {
  const t = n % 10, h = n % 100;
  if (t === 1 && h !== 11) return one;
  if (t >= 2 && t <= 4 && !(h >= 12 && h <= 14)) return few;
  return many;
};

// Окно доступа из профиля: «15:00–19:00» (HH:mm как лежат, без пересчёта — время ребёнка).
const windowLine = (w: TimeWindow) => `${w.from}–${w.to}`;

const tileStyle = {
  padding: `${SP.md}px ${SP.lg}px`, borderRadius: R.xxl, background: C.bgWhite,
  border: `1px solid ${C.borderLight}`, display: 'flex', flexDirection: 'column' as const, gap: SP.xs,
  minWidth: 0,
};
const tileKey = { fontSize: FS.xxs, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase' as const, letterSpacing: '0.05em' };
const tileVal = { fontFamily: FONT.serif, fontSize: FS.xl, color: C.textHeading, lineHeight: 1.15, overflow: 'hidden', textOverflow: 'ellipsis' as const };
const tileSub = { fontSize: FS.sm, color: C.textMuted, lineHeight: 1.4, overflow: 'hidden', textOverflow: 'ellipsis' as const };

const Tile = ({ k, v, s, testid }: { k: string; v: string; s: string; testid: string }) => (
  <div data-testid={testid} style={tileStyle}>
    <span style={tileKey}>{k}</span>
    <span style={tileVal}>{v}</span>
    <span style={tileSub}>{s}</span>
  </div>
);

const SectionLabel = ({ children }: { children: string }) => (
  <div style={{ fontSize: FS.xxs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em', marginTop: SP.lg, marginBottom: SP.sm }}>
    {children}
  </div>
);

export function ChildSettingsOverview({ profile, userNames, openSection }: Props) {
  const isMobile = useIsMobileModal();

  const subjects = profile.subjects ?? [];
  const on = subjects.filter((s) => s.enabled !== false);
  const off = subjects.length - on.length;
  const lessonSpace = profile.spaces?.lessons;
  const windows = lessonSpace?.windows ?? [];
  const daily = lessonSpace?.dailyMinutes ?? null;
  const consentOn = profile.acknowledgeRouteRisk === true;
  const guardians = profile.guardians ?? [];
  const flagOff = guardians.filter((g) => !g.sharedProjectsFlag);

  const timeValue = daily && daily > 0 ? `${daily} мин в день` : 'без лимита';
  const timeSub = windows.length
    ? `${plural(windows.length, 'окно', 'окна', 'окон')}: ${windows.map(windowLine).join(', ')}`
    : 'окон нет — весь день';
  const consentSub = consentOn
    ? [fmtDate(profile.routeRiskAcknowledgedAt || ''), profile.routeRiskAcknowledgedBy || ''].filter(Boolean).join(' · ') || 'дано раньше'
    : 'нужно для подписок, облачных и локальных моделей';
  const guardiansSub = !guardians.length
    ? 'их нет'
    : flagOff.length
      ? `${flagOff.length} без флага shared-projects`
      : 'все с флагом shared-projects';

  // Действие плашки/пустого состояния — переход в тот раздел, где это правится; testid —
  // чтобы вызывающий (и тест) нажимал именно этот переход, а не «первую кнопку рядом».
  const goBtn = (label: string, section: ChildSettingsSection, testid: string) => (
    <Button variant="ghost" size="sm" dataTestId={testid} onClick={() => openSection(section)}>{label}</Button>
  );

  return (
    <div data-testid="child-settings-overview" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div>
        <div style={{ fontFamily: FONT.serif, fontSize: FS.xl, color: C.textHeading, lineHeight: 1.2 }}>Обзор</div>
        <p style={{ fontSize: FS.base, color: C.textMuted, lineHeight: 1.45, margin: `${SP.xs}px 0 0` }}>
          Как сейчас устроены занятия ребёнка. Всё, что требует внимания, — сверху.
        </p>
      </div>

      {!consentOn && (
        <Notice
          data-testid="overview-consent-missing"
          tone="warning"
          icon={ShieldAlert}
          title="Согласие на риск маршрута не дано"
          action={goBtn('Решить это', 'safety', 'overview-go-safety')}
        >
          Ребёнку открыты только модели с API-ключом. Подписка, облачная или локальная модель
          потребит твоего согласия — оно одно на все предметы и на помощника правил.
        </Notice>
      )}

      {flagOff.length > 0 && (
        <Notice
          data-testid="overview-guardians-flag"
          tone="warning"
          icon={Users}
          title={`${plural(flagOff.length, 'Опекун без общих проектов', 'Опекуна без общих проектов', 'Опекунов без общих проектов')}`}
          action={goBtn('Посмотреть опекунов', 'safety', 'overview-go-guardians')}
        >
          Включите shared-projects у {flagOff.map((g) => nameOf(g.userId, userNames)).join(', ')} —
          без этого флага опекун не станет куратором проекта-предмета и не сможет работать с
          его файлами и знаниями.
        </Notice>
      )}

      {subjects.length === 0 && (
        <EmptyState
          compact
          inline
          icon={<BookOpen size={ICON_SIZE.xl} strokeWidth={ICON_STROKE} color={C.textMuted} />}
          title="Предметов пока нет"
          subtitle="Предмет — это плитка на главной у ребёнка. Его создаёт раздел «Предметы»: проект и репетитор появятся сами."
          action={goBtn('Открыть «Предметы»', 'subjects', 'overview-go-subjects')}
        />
      )}

      <div style={{ display: 'grid', gridTemplateColumns: isMobile ? 'repeat(2, minmax(0, 1fr))' : 'repeat(4, minmax(0, 1fr))', gap: isMobile ? SP.sm : SP.md }}>
        <Tile
          testid="tile-subjects"
          k="Предметы"
          v={subjects.length ? `${on.length} из ${subjects.length}` : 'нет'}
          s={subjects.length ? (off ? `${off} ${plural(off, 'выключен', 'выключено', 'выключено')}` : 'все включены') : 'главная пустая'}
        />
        <Tile testid="tile-time" k="Время" v={timeValue} s={timeSub} />
        <Tile testid="tile-consent" k="Согласие" v={consentOn ? 'дано' : 'не дано'} s={consentSub} />
        <Tile testid="tile-guardians" k="Опекуны" v={String(guardians.length)} s={guardiansSub} />
      </div>

      <SectionLabel>Опекуны и общие проекты</SectionLabel>
      {guardians.length === 0 ? (
        <p style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5, margin: 0 }}>
          Опекунов нет: стоп-сигналы и доступ к проектам предметов — только у того, кто включил режим.
        </p>
      ) : (
        <div data-testid="overview-guardians" style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.xxl, background: C.bgWhite, overflow: 'hidden' }}>
          {guardians.map((g, i) => (
            <div
              key={g.userId}
              data-testid={`overview-guardian-${g.userId}`}
              // Первая строка не удваивает верхнюю границу контейнера: разделитель — только между строками.
              style={{ display: 'flex', alignItems: 'center', gap: SP.md, padding: `${SP.md}px ${SP.lg}px`, minHeight: 48, borderTop: i ? `1px solid ${C.borderLight}` : 'none', flexWrap: 'wrap' }}
            >
              <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', flex: 1 }}>
                {nameOf(g.userId, userNames)}
              </span>
              <Badge tone={g.sharedProjectsFlag ? 'success' : 'warning'} size="xs">
                {g.sharedProjectsFlag ? 'shared-projects включён' : 'нужен флаг shared-projects'}
              </Badge>
            </div>
          ))}
        </div>
      )}
      {guardians.length > 0 && profile.sharedProjectsInstanceEnabled === false && (
        <p style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5, margin: 0 }}>
          Механизм общих проектов на сервере выключен: флаги опекунов ничего не откроют, пока его не включат.
        </p>
      )}
    </div>
  );
}
