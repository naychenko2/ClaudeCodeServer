// Ф2e: раздел «Безопасность и согласие» окна настроек ребёнка (ADR-041 ред. 4 решение 26 —
// одно согласие на профиль и его текст; ред. 6 решения 60–61 — кураторы это опекуны, флаг
// shared-projects и рубильник инстанса; макет Майи docs/mockups/kids-settings-window.html,
// раздел `safety`: порядок блоков «сначала решение, потом следствие» — согласие и модель
// помощника первыми, затем стоп-сигнал и опекуны).
//
// Контракт. Раздел — onChange-компонент: сам ничего не читает и не сохраняет. `route` и
// `acknowledge` — текущие поля черновика родителя (тот же порядок, что у values в ChildTimeSection);
// опекуны, рубильник, дата и автор согласия — read-only поля ответа профиля, приходят как есть.
//
// Согласие трёхзначно, как поле контракта: `acknowledge` — только действие сессии. null — не
// трогали: действительное состояние живое согласие сервера, его признак acknowledgedAt (пара
// момента и автора пишется вместе, UserStore.cs:701-713); true — поставить; false — отозвать.
// После успешного сохранения родитель НЕ переносит acknowledgeRouteRisk ответа в черновик
// (так же, как в прежнем редакторе профиля: его черновик не включал риск маршрута): иначе нетронутый профиль
// молча согласовал бы свой маршрут заново при каждом сохранении.
//
// Маршрут чата правил — полный список child-routes (docs/architecture/api.md:376 «child-routes —
// только для чата правил», решение 25): после ред. 4 маршрут профиля валидируется согласием
// (ChildProfileValidator.cs:42-49), поэтому пикер получает все записи, а не только пул.
// Флаг в value пикера — проекция согласия профиля, а не отдельное согласие: решение 26 —
// согласие сменой маршрута не сбрасывается, поэтому сброс флага пикером при смене поставщика
// (ChildRoutePicker.tsx:110-118) действием не считается; действием считается только явное
// переключение галки при том же маршруте.
//
// Тексты не называют ребёнка по имени: интерфейс не склоняет русские имена, а шапка окна уже
// назвала его (тот же вывод, что у текстов ChildSettingsOverview). Имена опекунов и автора
// согласия приходят извне (userNames от вызывающего): в ответе профиля только id, без имени
// строка показывает id (тот же порядок, что nameOf в ChildSettingsOverview.tsx:40).
//
// Г2: опекунов можно править из окна, без API. Раздел остаётся onChange-компонентом:
// выбор и кнопки меняют только черновик вызывающего (onGuardiansChange — целый новый список
// id; порядок — текущий плюс новый в конец). Записывает черновик каркас (PUT child-profile):
// запрос идёт из живого профиля, поэтому лимиты, окна и предметы не затираются; согласие каркас
// не трогает — это отдельная галка (onAcknowledgeChange): правка списка опекунов явный отзыв
// не стирает, живую пару согласия слияние стора не двигает (UserStore.SetChildProfile:
// «повторный PUT с тем же флагом его не переставляет»). Без onGuardiansChange
// раздел остаётся read-only (прежний контракт).
// Флаг shared-projects строки — только известный из ответа: у добавленного до сохранения
// опекуна его нет (UserDto флага не несёт, api.md:369-374 — флаг включает человек, не окно);
// строка без флага честнее выдуманного.

import { useState } from 'react';
import { Hand, Plus, Trash2, Users } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Badge } from '../../components/ui/Badge';
import { Button } from '../../components/ui/Button';
import { Checkbox } from '../../components/ui/Checkbox';
import { Field } from '../../components/ui/Field';
import { Notice } from '../../components/ui/Notice';
import { Select } from '../../components/ui/Select';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { ChildRoutePicker, type ChildRouteValue } from './ChildRoutePicker';
import type { ChildRoute } from './types';

// Строка опекуна в рендере: id — из списка черновика (порядок guardianUserIds), флаг — известный
// из ответа (ChildSettingsGuardian). Отсутствие флага — «ещё не сохранён», не «выключен».
export interface ChildSettingsGuardianRow {
  userId: string;
  sharedProjectsFlag?: boolean;
}

interface Props {
  // Опекунa профиля: порядок — черновика (guardianUserIds), флаг — известного из ответа;
  // рубильник инстанса SharedProjects:Enabled (решение 60): без него флаги опекунов ничего
  // не открывают.
  guardians: ChildSettingsGuardianRow[];
  // Кандидаты пикера: id пользователей без детского режима (фильтр вызывающего: ребёнок
  // не бывает опекуном). Имена — из userNames, как у списка. Без пропа правки нет.
  guardianCandidates?: string[];
  // Целый новый список опекунов: вызывающий патчит черновик (guardianUserIds + согласие).
  onGuardiansChange?: (ids: string[]) => void;
  sharedProjectsInstanceEnabled?: boolean;
  // Имена по id — список вызывающего. Без имени строка показывает id.
  userNames?: Record<string, string>;
  // Согласие: действие сессии (null — не трогали) и read-only поля ответа.
  acknowledge: boolean | null;
  acknowledgedAt?: string | null;
  acknowledgedBy?: string | null;
  // Действие бывает только true/false: «не трогали» — отсутствие поля в теле, его раздел
  // сам не выдаёт: это не-действие.
  onAcknowledgeChange: (v: boolean) => void;
  // Маршрут чата правил: полный список GET /api/admin/child-routes; null — ещё не загружен
  // (пустой список пикер называет «нет записей» — пока список грузится, этого говорить нельзя,
  // тот же смысл, что у «список ещё не загружен» в прежнем редакторе профиля).
  routes: ChildRoute[] | null;
  // Текущее значение черновика: providerKey/model профиля.
  route: { providerKey: string; model: string };
  onRouteChange: (next: { providerKey: string; model: string }) => void;
  // Гасит контролы на время сохранения — состояние родителя.
  disabled?: boolean;
}

// Текст согласия — дословно решение 26 ADR-041 (постановка шага), на «вы» к админу.
// Пункты impersonalны, как в ADR: они описывают риск, а не просят действие — действие просит
// галка ниже.
const RISK_POINTS = [
  'Условия Anthropic для подписок — 18+; за нарушение может быть заблокирован аккаунт со всеми чатами команды.',
  'Облачный поставщик получает переписку ребёнка.',
  'Локальная модель слабее держит правила и стоп-сигнал.',
  'При сбое урок на один ход ответит соседняя модель из цепочки.',
  'Лимиты общие со взрослыми.',
  'Бюджет по подписке считается «как по API».',
];
// Имя галки — из макета (диалог согласия): та же формулировка, что видят предметы в окне;
// здесь она про весь профиль сразу.
const ACK_LABEL = 'Я понимаю риски и беру их на себя';

const nameOf = (userId: string | null | undefined, names?: Record<string, string>): string =>
  (userId && names?.[userId]) || userId || '';

// Дата — тот же числовой формат каталога, что у согласия в Overview (ChildSettingsOverview.tsx:44-49):
// dd.mm.yyyy по полям строки, без перевода в пояс процесса.
const fmtDate = (iso: string): string => {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getDate())}.${p(d.getMonth() + 1)}.${d.getFullYear()}`;
};

const SectionLabel = ({ children }: { children: string }) => (
  <div style={{ fontSize: FS.xxs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em', marginTop: SP.lg, marginBottom: SP.sm }}>
    {children}
  </div>
);

const panelStyle = {
  display: 'flex', flexDirection: 'column' as const, gap: SP.sm,
  padding: SP.md, borderRadius: R.xl, background: C.bgPanel,
};
const rowStyle = { display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' as const };

export function ChildSafetySection({
  guardians, guardianCandidates, onGuardiansChange,
  sharedProjectsInstanceEnabled, userNames,
  acknowledge, acknowledgedAt, acknowledgedBy, onAcknowledgeChange,
  routes, route, onRouteChange, disabled,
}: Props) {
  // Живое согласие сервера — его дата (пара момента и автора пишется вместе);
  // действие сессии перекрывает его.
  const consentOn = acknowledge ?? acknowledgedAt != null;

  // Выбор пикера — UI-состояние раздела (не данные: в черновик оно не едет, действие —
  // сама кнопка «Добавить»). Кандидаты — переданные минус уже в списке: тот же id не бывает
  // опекуном дважды (защита и на случай невнимательного вызывающего).
  const [selected, setSelected] = useState('');
  const candidateIds = (guardianCandidates ?? []).filter((id) => !guardians.some((g) => g.userId === id));
  const addGuardian = () => {
    if (!selected || !onGuardiansChange) return;
    // Порядок: текущий список как есть, новый — в конец (порядок guardianUserIds — порядок
    // строк ответа, его не переставляем). Выбор после действия снимается: добавленный больше
    // не кандидат.
    onGuardiansChange([...guardians.map((g) => g.userId), selected]);
    setSelected('');
  };

  // Строка «Дано {дата} · {кто}» — только когда согласие стоит и есть что ставить:
  // только что поставленное (действия ещё нет в ответе) подтверждает сама отмеченная галка,
  // отзыв — снятая. Прежнее согласие без автора (поле добавлено Б4a) — дата без «·».
  const givenLine = [
    acknowledgedAt ? fmtDate(acknowledgedAt) : '',
    acknowledgedBy ? nameOf(acknowledgedBy, userNames) : '',
  ].filter(Boolean).join(' · ');

  const onPickerChange = (v: ChildRouteValue) => {
    if (v.providerKey !== route.providerKey || v.model !== route.model) {
      // Смена маршрута — только маршрут: согласие сменой маршрута не сбрасывается (решение 26),
      // а отдаваемый пикером при новом поставщике сброшенный флаг — его внутреннее правило
      // (ChildRoutePicker.tsx:110-118), не действие админа.
      onRouteChange({ providerKey: v.providerKey, model: v.model });
      return;
    }
    // Тот же маршрут и другой флаг — это переключение галки пикера: то же согласие профиля.
    if (v.acknowledgeRouteRisk !== undefined && v.acknowledgeRouteRisk !== consentOn) {
      onAcknowledgeChange(v.acknowledgeRouteRisk);
    }
  };

  return (
    <div data-testid="child-safety-section" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <div>
        <div style={{ fontFamily: FONT.serif, fontSize: FS.xl, color: C.textHeading, lineHeight: 1.2 }}>Безопасность и согласие</div>
        <p style={{ fontSize: FS.base, color: C.textMuted, lineHeight: 1.45, margin: `${SP.xs}px 0 0` }}>
          Что можно ребёнку, что происходит, если ему стало неприятно, и кто об этом узнаёт.
        </p>
      </div>

      <SectionLabel>Согласие на модели</SectionLabel>
      <div data-testid="consent-block" style={panelStyle}>
        <p style={{ fontSize: FS.sm, color: C.textPrimary, lineHeight: 1.5, margin: 0 }}>
          Разрешая ребёнку любую модель — подписку, облачную или локальную, — вы берёте на себя:
        </p>
        <ol style={{ margin: 0, paddingLeft: SP.lg, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {RISK_POINTS.map((p) => (
            <li key={p} style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>{p}</li>
          ))}
        </ol>
        <div style={rowStyle}>
          <Checkbox checked={consentOn} disabled={disabled} ariaLabel={ACK_LABEL} onChange={onAcknowledgeChange} />
          <span style={{ fontSize: FS.md, color: C.textPrimary }}>{ACK_LABEL}</span>
        </div>
        {consentOn && givenLine && (
          <span data-testid="consent-given" style={{ fontSize: FS.sm, color: C.textMuted }}>
            Дано {givenLine}
          </span>
        )}
      </div>

      <SectionLabel>Модель помощника правил</SectionLabel>
      {routes === null ? (
        <span data-testid="safety-routes-loading" style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаю список моделей…</span>
      ) : (
        <div data-testid="rules-route" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <ChildRoutePicker
            routes={routes}
            // Флаг пикера — проекция согласия профиля: та же галка, что выше, а не вторая.
            value={{ ...route, acknowledgeRouteRisk: consentOn }}
            onChange={onPickerChange}
            disabled={disabled}
          />
          <span style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5 }}>
            С ним ребёнок договаривается о правилах. Новые предметы берут эту модель по умолчанию.
          </span>
        </div>
      )}

      <SectionLabel>Стоп-сигнал</SectionLabel>
      <div
        data-testid="stop-signal-info"
        style={{ display: 'flex', gap: SP.md, alignItems: 'flex-start', padding: SP.md, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, background: C.bgCard }}
      >
        <span aria-hidden style={{ width: 40, height: 40, borderRadius: R.lg, display: 'grid', placeItems: 'center', background: C.dangerBg, color: C.dangerText, flexShrink: 0 }}>
          <Hand size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
        </span>
        <p style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5, margin: 0 }}>
          Кнопка «Мне неприятно» и сигнал от репетитора присылают уведомление тебе и опекунам:
          кто, когда и в каком предмете. Текста переписки в уведомлении нет.
        </p>
      </div>

      <SectionLabel>Опекуны</SectionLabel>
      <p style={{ fontSize: FS.sm, color: C.textSecondary, margin: 0 }}>
        Кому ещё приходят стоп-сигналы и кто работает с проектами предметов
      </p>
      {guardians.length === 0 ? (
        <p style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5, margin: 0 }}>
          Опекунов нет: стоп-сигналы ребёнка и доступ к проектам предметов — только у того, кто включил режим.
        </p>
      ) : (
        <div data-testid="guardians-list" style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.xxl, background: C.bgWhite, overflow: 'hidden' }}>
          {guardians.map((g, i) => {
            const name = nameOf(g.userId, userNames);
            return (
              <div
                key={g.userId}
                data-testid={`guardian-${g.userId}`}
                style={{ display: 'flex', alignItems: 'center', gap: SP.md, padding: `${SP.md}px ${SP.lg}px`, minHeight: 48, borderTop: i ? `1px solid ${C.borderLight}` : 'none', flexWrap: 'wrap' }}
              >
                <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', flex: 1 }}>
                  {name}
                </span>
                {g.sharedProjectsFlag === true && (
                  <Badge tone="success" size="xs">shared-projects включён</Badge>
                )}
                {g.sharedProjectsFlag === false && (
                  <>
                    <Badge tone="warning" size="xs">нужен флаг shared-projects</Badge>
                    <span style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5, flexBasis: '100%' }}>
                      Включите shared-projects у {name} — без этого флага опекун
                      не станет куратором проекта-предмета и не сможет работать с его файлами и знаниями.
                    </span>
                  </>
                )}
                {g.sharedProjectsFlag === undefined && (
                  <span data-testid={`guardian-flag-unknown-${g.userId}`} style={{ fontSize: FS.sm, color: C.textMuted }}>
                    Флаг shared-projects будет виден после сохранения.
                  </span>
                )}
                {onGuardiansChange && (
                  <Button
                    variant="danger" size="sm" dataTestId={`guardian-remove-${g.userId}`}
                    ariaLabel={`Убрать опекуна «${name}»`} disabled={disabled}
                    onClick={() => onGuardiansChange(guardians.filter((x) => x.userId !== g.userId).map((x) => x.userId))}
                  >
                    <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
                    Убрать
                  </Button>
                )}
              </div>
            );
          })}
        </div>
      )}
      {onGuardiansChange && (candidateIds.length === 0 ? (
        <p data-testid="guardian-add-empty" style={{ fontSize: FS.sm, color: C.textMuted, margin: 0 }}>
          Добавить некого: остальные пользователи уже опекуны или в детском режиме.
        </p>
      ) : (
        <div data-testid="guardian-add" style={{ display: 'flex', gap: SP.md, alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <div style={{ flex: 1, minWidth: 220 }}>
            <Field label="Добавить опекуна">
              <Select
                value={selected}
                onChange={(v) => setSelected(v)}
                options={candidateIds.map((id) => ({ value: id, label: nameOf(id, userNames) }))}
                placeholder="Выбери пользователя"
                disabled={disabled}
              />
            </Field>
          </div>
          <Button
            variant="secondary" size="md" dataTestId="guardian-add-btn"
            leftIcon={<Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
            disabled={disabled || !selected} onClick={addGuardian}
          >
            Добавить
          </Button>
        </div>
      ))}
      <p style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.5, margin: 0 }}>
        Опекун получает уведомления и открывает проекты предметов (файлы, знания, репетиторов),
        но не видит уроков ребёнка.
      </p>
      {guardians.length > 0 && sharedProjectsInstanceEnabled === false && (
        <Notice data-testid="shared-projects-off" tone="warning" icon={Users} title="Рубильник общих проектов на сервере выключен">
          Флаги опекунов ничего не откроют, пока его не включат.
        </Notice>
      )}
    </div>
  );
}
