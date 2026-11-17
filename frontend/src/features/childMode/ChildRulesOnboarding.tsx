// KM-F6: онбординг правил «Лиза и папа» — пять шагов общей договорённости в Modal
// (MODAL_W.wide; на телефоне сам Modal становится шторкой). Образец:
// docs/mockups/kids-shell-mockup:docs/mockups/kids-shell-v2.html — rulesModal()/stepBody()
// (стр. 692–725) и стили .mhead/.segs/.mbody/.rrow/.tiles/.two/.chain/.check
// (стр. 227–229, 349–373); записка kids-shell-v2-proposal.md: стр. 88 (Modal + ModalActions)
// и стр. 103 (сегменты над заголовком = 5 × ProgressBar: success / accent / track).
// Абзаца про фото в макете нет: он приехал из решения Андрея 17.11 (фото рассматривает
// облачная модель) — текстом постановки, константой PHOTO_NOTE, на шаге 1 в обоих режимах.
//
// Своего состояния у компонента нет: имя ребёнка, лимиты и дата согласия приезжают в `rules`
// (из профиля через статус — ADR-041, «Контракт /api/child/*»). Единственный запрос —
// POST /child/rules/agree на «Договорились».

import { Fragment, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Bell, CalendarClock, Check, ChevronRight, Cpu, Eye, EyeOff, Hand, Image as ImageIcon, Lightbulb, TriangleAlert, Users, Volume2, X } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { C, FONT, FS, MODAL_W, R, SP } from '../../lib/design';
import { ICON_PROPS, ICON_SIZE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { plural } from '../../lib/plural';
import { Badge } from '../../components/ui/Badge';
import { Checkbox } from '../../components/ui/Checkbox';
import { Island } from '../../components/ui/Island';
import { Modal } from '../../components/ui/Modal';
import { ModalActions } from '../../components/ui/ModalActions';
import { Notice } from '../../components/ui/Notice';
import { ProgressBar } from '../../components/ui/ProgressBar';
import { childApi } from './api';
import type { TimeWindow } from './types';

// Порядок шагов — по макету (STEPS в rulesModal); он же порядок сегментов над заголовком.
export const CHILD_RULES_STEPS = [
  'Кто такой помощник', 'Наше время', 'Что видит папа', 'Если что-то не так', 'Договорились?',
] as const;
const LAST = CHILD_RULES_STEPS.length;

// Фото уходят в облачную модель (решение Андрея 17.11), и это говорится ребёнку один раз,
// на шаге 1, отдельным абзацем — в обоих режимах, и при просмотре правил тоже.
// Тон — подсказка: слов «опасно», «нельзя», «запрещено» здесь нет намеренно.
export const PHOTO_NOTE = 'Фото помощник рассматривает на большом компьютере далеко отсюда. Перед снимком проверь, чтобы в кадр не попали лица, фамилия, школа на обложке тетради или наш адрес — их можно прикрыть листком.';

// Данные приходят из профиля через статус (ADR-041): окно строит их само, запросов у него нет.
export interface ChildRulesBrief {
  /** Имя ребёнка: подписи согласия («Я, Лиза, согласна») и строка согласия в summary */
  childName: string;
  /** Дневной лимит пространства в минутах (ChildProfile.spaces[space].dailyMinutes); null — лимита нет */
  dailyMinutes: number | null;
  /** Окна времени пространства из профиля: from/to «HH:mm», to < from — окно через полночь */
  windows: TimeWindow[];
  /** Порог предупреждения (ChildStatus.warnMinutes) */
  warnMinutes: number;
  /** Чат онбординга правил (POST /child/rules/start) — его id записывает согласие */
  sessionId: string;
  /** Дата согласия (ChildStatus.rulesAgreedAt) — для просмотра правил (review) */
  agreedAt?: string | null;
}

interface Props {
  open: boolean;
  /** agree — договорённость с отметками; review — просмотр уже согласованных правил */
  mode: 'agree' | 'review';
  rules: ChildRulesBrief;
  onAgreed: () => void;
  onClose: () => void;
}

// Потолок ручки (ADR-041 №7: summary 1..2000, иначе 400 summary_too_long).
const SUMMARY_MAX = 2000;

const DAY_SHORT = ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'вс'] as const;
// Дательный падеж множественного числа — форма после «по» («по понедельникам, средам»):
// Intl даёт именительный, «по понедельники» было бы неверно.
const DAY_TO = ['понедельникам', 'вторникам', 'средам', 'четвергам', 'пятницам', 'субботам', 'воскресеньям'] as const;
const WEEKDAYS = [1, 2, 3, 4, 5, 6, 7];

// Дни окна: метка плитки и читаемая форма строки договорённости. Сплошной отрезок недели
// называется как люди («по будням», «по выходным», «каждый день»), остальное — перечислением.
function daysShort(weekdays?: number[]): string {
  const d = (weekdays ?? []).filter((x) => WEEKDAYS.includes(x)).sort((a, b) => a - b);
  if (!d.length || d.join() === WEEKDAYS.join()) return 'каждый день';
  if (d.join() === '1,2,3,4,5') return 'пн–пт';
  if (d.join() === '6,7') return 'сб–вс';
  if (d.every((x, i) => x === d[0] + i)) return `${DAY_SHORT[d[0] - 1]}–${DAY_SHORT[d[d.length - 1] - 1]}`;
  return d.map((x) => DAY_SHORT[x - 1]).join(', ');
}

// Читаемая форма для строки договорённости. Пусто — когда окно на все дни: в строке
// «…в день: с 15:00 до 19:00» «каждый день» уже сказано самим «в день».
function daysPhrase(weekdays?: number[]): string {
  const d = (weekdays ?? []).filter((x) => WEEKDAYS.includes(x)).sort((a, b) => a - b);
  if (!d.length || d.join() === WEEKDAYS.join()) return '';
  if (d.join() === '1,2,3,4,5') return 'по будням';
  if (d.join() === '6,7') return 'по выходным';
  return `по ${d.map((x) => DAY_TO[x - 1]).join(', ')}`;
}

// Часы окна из профиля (таймзона ребёнка, ADR-041 «Общие правила») читаются прямо из строки:
// пересчёт в таймзону браузера сдвинул бы их (тот же приём, что windowEndsClock в ChildHome).
function hh(value: string): string {
  const m = value.match(/^\d{2}:\d{2}/);
  return m ? m[0] : value;
}

function minutesWord(n: number): string {
  return plural(n, 'минута', 'минуты', 'минут');
}

interface LimitTile { value: string; unit: string }

// Плитки «Наше время» — из лимитов профиля. Без лимита плитки минут нет: пустой «0 минут»
// читался бы как запрет, а это не так.
function limitTiles(r: ChildRulesBrief): LimitTile[] {
  const tiles: LimitTile[] = [];
  if (r.dailyMinutes != null && r.dailyMinutes > 0) {
    tiles.push({ value: `${r.dailyMinutes} ${minutesWord(r.dailyMinutes)}`, unit: 'в день' });
  }
  for (const w of r.windows) {
    tiles.push({ value: daysShort(w.weekdays), unit: `с ${hh(w.from)} до ${hh(w.to)}` });
  }
  return tiles;
}

// Строки договорённости: тот же текст, что ребёнок видит галками на шаге 5, и тот же уходит
// на сервер как summary. Один источник — чтобы отмеченное и записанное не разъехались.
function rulesLines(r: ChildRulesBrief): string[] {
  const when = r.windows
    .map((w) => {
      const days = daysPhrase(w.weekdays);
      const hours = `с ${hh(w.from)} до ${hh(w.to)}`;
      return days ? `${days} ${hours}` : hours;
    })
    .join('; ');
  const head = r.dailyMinutes != null && r.dailyMinutes > 0
    ? `Я занимаюсь ${r.dailyMinutes} ${minutesWord(r.dailyMinutes)} в день`
    : 'Я занимаюсь, когда открыто время уроков';
  const time = `${head}${when ? `: ${when}` : ''}.`;
  return [
    'Я знаю, что помощник — программа, а не человек и не друг.',
    time,
    'Решаю сама, а помощник подсказывает.',
    'Если что-то не так, я говорю папе.',
  ];
}

function summaryOf(r: ChildRulesBrief): string {
  const text = [...rulesLines(r), `${r.childName} и папа согласны.`].join('\n');
  // Составная строка заметно короче потолка, но имя и окна приходят извне: если что-то
  // вытянуло её за 2000 — режем по границе строки, а не посреди слова.
  if (text.length <= SUMMARY_MAX) return text;
  const cut = text.lastIndexOf('\n', SUMMARY_MAX);
  return text.slice(0, cut > 0 ? cut : SUMMARY_MAX);
}

// Дата согласия для просмотра правил: «16 ноября», без года (макет: «Договорились 16 ноября»).
function agreedLabel(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime())
    ? ''
    : new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long' }).format(d);
}

// Строка объяснения: квадрат 40 с иконкой (макет .rrow .tile) и текст рядом.
function Row({ icon: Icon, children }: { icon: LucideIcon; children: ReactNode }) {
  return (
    <div style={{ display: 'flex', gap: SP.md, alignItems: 'flex-start' }}>
      <span style={{
        width: 40, height: 40, flexShrink: 0, borderRadius: R.lg,
        background: C.bgInset, color: C.textSecondary, display: 'grid', placeItems: 'center',
      }}>
        <Icon {...ICON_PROPS} size={ICON_SIZE.md} />
      </span>
      <div style={{ paddingTop: SP.sm, fontSize: FS.lg, lineHeight: 1.5, color: C.textPrimary }}>{children}</div>
    </div>
  );
}

// Список карточки «видит / не видит» (макет .two .card h3 + .list).
function CardList({ icon: Icon, title, items, muted }: {
  icon: LucideIcon;
  title: string;
  items: string[];
  muted?: boolean;
}) {
  return (
    <Island bg={C.bgCard} borderColor={C.borderLight} style={{ flex: 1, minWidth: 0, padding: SP.lg, borderRadius: R.xl }}>
      <div style={{ display: 'flex', gap: SP.xs, alignItems: 'center', fontSize: FS.md, fontWeight: 600, color: C.textHeading, marginBottom: SP.sm }}>
        <Icon {...ICON_PROPS} size={ICON_SIZE.sm} />
        {title}
      </div>
      <ul style={{ margin: 0, padding: 0, listStyle: 'none', display: 'flex', flexDirection: 'column', gap: SP.sm, fontSize: FS.md, lineHeight: 1.45, color: muted ? C.textSecondary : C.textPrimary }}>
        {items.map((text) => (
          <li key={text} style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start' }}>
            {muted
              ? <X {...ICON_PROPS} size={ICON_SIZE.xs} style={{ color: C.textMuted, flexShrink: 0, marginTop: 2 }} />
              : <Check {...ICON_PROPS} size={ICON_SIZE.xs} style={{ color: C.success, flexShrink: 0, marginTop: 2 }} />}
            {text}
          </li>
        ))}
      </ul>
    </Island>
  );
}

// Строка согласия: галка ui-kit + текст, который тоже является целью нажатия (макет .check —
// строка 48px). На самой строке-обёртке обработчика нет: иначе галка и текст переключали бы
// состояние дважды.
function ConsentRow({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <div style={{
      display: 'flex', alignItems: 'center', gap: SP.md, padding: `${SP.sm}px ${SP.md}px`, minHeight: 48,
      borderRadius: R.xl, border: `1px solid ${checked ? C.success : C.border}`,
      background: checked ? C.successBg : C.bgWhite,
    }}>
      <Checkbox checked={checked} onChange={onChange} ariaLabel={label} />
      <span onClick={() => onChange(!checked)} style={{ fontSize: FS.md, color: checked ? C.successText : C.textHeading, cursor: 'pointer' }}>
        {label}
      </span>
    </div>
  );
}

// Сегменты над заголовком (макет .segs, записка стр. 103): пройденное — success,
// текущий — accent, будущие — пустая дорожка.
function Segments({ step }: { step: number }) {
  return (
    <span style={{ display: 'flex', gap: SP.xs, margin: `${SP.sm}px 0 ${SP.md}px` }}>
      {CHILD_RULES_STEPS.map((title, i) => {
        const n = i + 1;
        return (
          <span key={title} style={{ flex: 1 }}>
            <ProgressBar
              value={n <= step ? 100 : 0}
              tone={n < step ? 'success' : n === step ? 'accent' : 'muted'}
              label={`Шаг ${n} из ${LAST}: ${title}`}
            />
          </span>
        );
      })}
    </span>
  );
}

// Закрытое окно — нет ни модалки, ни состояния: поток снимается вместе с ним, поэтому
// следующее открытие всегда начинается с первого шага и без отметок. Сброс состоянием
// в эффекте дал бы каскадные рендеры (предупреждение react-hooks/set-state-in-effect).
export function ChildRulesOnboarding({ open, mode, rules, onAgreed, onClose }: Props) {
  if (!open) return null;
  return <RulesFlow mode={mode} rules={rules} onAgreed={onAgreed} onClose={onClose} />;
}

function RulesFlow({ mode, rules, onAgreed, onClose }: Omit<Props, 'open'>) {
  const isMobile = useIsMobile();
  const review = mode === 'review';
  const [step, setStep] = useState(1);
  const [me, setMe] = useState(false);
  const [dad, setDad] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  // Номер запроса: ответ ушедшего запроса не должен действовать после закрытия окна
  // (контракт шага: «модалку закрыли до ответа — onAgreed не зовётся»).
  const reqRef = useRef(0);

  // Снятие окна (закрыл родитель, сменился экран) гасит unanswered запрос: поздний ответ
  // ушедшего agree не должен звать onAgreed уже несущегося компонента.
  useEffect(() => () => { reqRef.current += 1; }, []);

  const handleClose = () => {
    reqRef.current += 1;
    onClose();
  };

  const agree = async () => {
    // Второй клик до ответа: флаг ставится до await — иначе второй клик успел бы
    // отправить второй agree (тот же механизм, что у сигнала «Мне неприятно»).
    if (busy) return;
    const id = (reqRef.current += 1);
    setBusy(true);
    setFailed(false);
    try {
      await childApi.rulesAgree(rules.sessionId, summaryOf(rules));
      if (reqRef.current === id) onAgreed();
    } catch {
      if (reqRef.current === id) setFailed(true);
    } finally {
      if (reqRef.current === id) setBusy(false);
    }
  };

  const next = () => {
    if (step < LAST) {
      setStep(step + 1);
      return;
    }
    if (review) {
      handleClose();
      return;
    }
    void agree();
  };

  const tiles = limitTiles(rules);
  const lines = rulesLines(rules);
  const agreedDate = review && rules.agreedAt ? agreedLabel(rules.agreedAt) : '';
  // Порог предупреждения — из warnMinutes профиля. Строка собирается целиком: React рвёт
  // соседние текстовые узлы комментарием, и по частям её не прочитать ни тесту, ни экранирующему.
  const warnLine = `За ${rules.warnMinutes} ${minutesWord(rules.warnMinutes)} до конца я предупрежу.`;
  const agreedBadge = agreedDate ? `Договорились ${agreedDate}: ${rules.childName} и папа` : '';

  const body = (() => {
    if (step === 1) {
      return (
        <>
          <Row icon={Cpu}>Помощник — <b>компьютерная программа</b>, а не человек и не друг.</Row>
          <Row icon={Lightbulb}>Он подсказывает, а решаешь ты сама.</Row>
          <Row icon={Volume2}>Ответы помощник может читать вслух.</Row>
          <Row icon={Users}>Важное и личное — живым людям: папе, маме, учителю.</Row>
          <Notice tone="info" icon={ImageIcon}>{PHOTO_NOTE}</Notice>
        </>
      );
    }
    if (step === 2) {
      return (
        <>
          {tiles.length > 0 && (
            <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : `repeat(${Math.min(3, tiles.length)}, 1fr)`, gap: SP.md }}>
              {tiles.map((t, i) => (
                // Ключ по индексу: два одинаковых окна дали бы дублирующийся ключ из текста.
                <Island key={`tile-${i}`} bg={C.bgCard} borderColor={C.borderLight} style={{ padding: SP.lg, borderRadius: R.xl, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                  <CalendarClock {...ICON_PROPS} size={ICON_SIZE.md} style={{ color: C.textSecondary }} />
                  <span style={{ fontFamily: FONT.serif, fontSize: FS.h2, color: C.textHeading }}>{t.value}</span>
                  <span style={{ fontSize: FS.sm, color: C.textMuted }}>{t.unit}</span>
                </Island>
              ))}
            </div>
          )}
          <Row icon={Bell}>{warnLine}</Row>
          <Row icon={Bell}>Остаток называю крупно, а не по секундам.</Row>
          <Notice tone="info" icon={Users}>Обсудите вместе. Если время не подходит, папа поменяет его в настройках.</Notice>
        </>
      );
    }
    if (step === 3) {
      return (
        <div style={{ display: 'flex', gap: SP.md, flexDirection: isMobile ? 'column' : 'row' }}>
          <CardList icon={Eye} title="Папа видит" items={['Сколько времени ты занималась', 'Предметы и темы', 'Где было трудно']} />
          <CardList icon={EyeOff} title="Папа не видит" items={['Ваши сообщения', 'Ошибки по одной', 'Черновики']} muted />
        </div>
      );
    }
    if (step === 4) {
      const chain = [
        'Ты пишешь: «мне неприятно» или «что-то не так»',
        'Помощник советует рассказать взрослому',
        'Папе приходит сигнал',
      ];
      return (
        <>
          <div style={{ display: 'flex', alignItems: 'stretch', gap: SP.sm, flexDirection: isMobile ? 'column' : 'row' }}>
            {chain.map((text, i) => (
              <Fragment key={text}>
                {i > 0 && <ChevronRight {...ICON_PROPS} size={ICON_SIZE.md} style={{ alignSelf: 'center', flexShrink: 0, color: C.textMuted, transform: isMobile ? 'rotate(90deg)' : undefined }} />}
                <Island bg={C.bgCard} borderColor={C.borderLight} style={{ flex: 1, minWidth: 0, padding: SP.md, borderRadius: R.lg, fontSize: FS.sm, lineHeight: 1.45, color: C.textPrimary }}>
                  {text}
                </Island>
              </Fragment>
            ))}
          </div>
          <Row icon={EyeOff}>Папа узнаёт, что нужна помощь, но не читает ваш разговор.</Row>
          <Row icon={Hand}>Ты ни в чём не виновата. Можно подойти к папе и самой.</Row>
        </>
      );
    }
    return (
      <>
        <ul style={{ margin: 0, padding: 0, listStyle: 'none', display: 'flex', flexDirection: 'column', gap: SP.sm, fontSize: FS.md, lineHeight: 1.45, color: C.textPrimary }}>
          {lines.map((text) => (
            <li key={text} style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start' }}>
              <Check {...ICON_PROPS} size={ICON_SIZE.sm} style={{ color: C.success, flexShrink: 0, marginTop: 2 }} />
              {text}
            </li>
          ))}
        </ul>
        {review ? (
          agreedBadge
            ? <Badge tone="success" size="sm" icon={<Check {...ICON_PROPS} size={ICON_SIZE.xs} />}>{agreedBadge}</Badge>
            : null
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
            <ConsentRow label={`Я, ${rules.childName}, согласна`} checked={me} onChange={setMe} />
            <ConsentRow label="Папа рядом и тоже согласен" checked={dad} onChange={setDad} />
          </div>
        )}
      </>
    );
  })();

  const confirmLabel = step === LAST ? (review ? 'Закрыть' : 'Договорились') : step === 2 && !review ? 'Нам подходит' : 'Дальше';
  const confirmDisabled = step === LAST && !review && !(me && dad);

  return (
    <Modal
      width={MODAL_W.wide}
      // Шапка по макету: надзаголовок → сегменты → название шага. Всё приезжает в title
      // одним содержимым: у Modal нет слота над заголовком, а свой ряд в теле уехал бы
      // под него. h2 остаётся доступным именем окна целиком.
      title={
        <>
          <span style={{ display: 'block', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textMuted }}>
            {review ? 'Наши правила' : `${rules.childName} и папа`} · шаг {step} из {LAST}
          </span>
          <Segments step={step} />
          {CHILD_RULES_STEPS[step - 1]}
        </>
      }
      footer={
        <ModalActions
          confirmLabel={confirmLabel}
          confirmDisabled={confirmDisabled}
          loading={busy}
          onConfirm={next}
          cancelLabel={step === 1 ? 'Отмена' : 'Назад'}
          onCancel={step === 1 ? handleClose : () => setStep(step - 1)}
        />
      }
      onClose={handleClose}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, fontSize: FS.lg, lineHeight: 1.5, color: C.textPrimary }}>
        {failed && (
          <Notice tone="warning" icon={TriangleAlert} title="Не получилось сохранить">
            Попробуй ещё раз — отмечать заново не нужно.
          </Notice>
        )}
        {body}
      </div>
    </Modal>
  );
}
