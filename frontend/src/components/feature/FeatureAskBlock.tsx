// «Нужно от тебя / Что мешает / От тебя ничего не нужно» (FS-FY): презентационная
// плашка над вкладками страницы фичи (макет feature-page-simplify-v4-clickable, §2
// «Плашка «Нужно от тебя / Что мешает»», §7 FS-FS/FS-FY).
//
// Геометрия — как у FeaturePageAlert (FeaturePageHeader.tsx): поля 8×12 (SP.sm SP.md),
// R.lg, текст FS.base; спокойный вид — фон/рамка как у его neutral-тона
// (bgSelected + borderLight). Тон тревоги — ровно токены Notice (components/ui/Notice.tsx):
// warning — warningBg/warning/warningText, danger — dangerBg/dangerBorder/dangerText.
// Стиль строк (подпись/фраза/подстрока) — по CSS .alert в макете v4.
//
// Вид выбирается ТОЛЬКО по ask.source (контракт бэка, FeatureBrief.BriefAsk):
// null — спокойный, wait/merge — «Нужно от тебя», stopped — «Что мешает».
// Фронт состояние не выводит и ничего не пересчитывает: давность — formatDuration
// от ask.since (единственный источник правила — chatGroups.ts, нижняя граница 1 мин
// в нём же). Последствие ожидания («без ответа Ш9 не начнётся») — величины бэка нет,
// не рисуем.
//
// Кнопка — только при переданном onAnswer; куда вести (чат или диалог мержа), решает
// FS-FS. На 360 кнопка во всю ширину под текстом, minHeight 40 (правило тач-целей);
// на десктопе — справа от текста (приём action в Notice). Внешний отступ блока —
// забота страницы (FS-FS), здесь его нет.

import { C, FONT, FS, R, SP, TB } from '../../lib/design';
import { Button } from '../ui/Button';
import { formatDuration } from '../../lib/chatGroups';
import { useNow } from '../../lib/useNow';
import { markdownToPlain } from '../../lib/markdownPlain';
import { minutesSince } from './FeatureStatsBits';
import { dativeName, genitiveName } from './FeatureBriefModel';
import type { FeatureAsk } from '../../lib/featurePage';

// Общий словарь подписей (макет v5 §7): те же слова импортирует карточка фичи в
// шапке чата (FS-FCb) — своих строк там не заводятся.
export const ASK_LABEL = { need: 'Нужно от тебя', blocker: 'Что мешает' } as const;

export interface FeatureAskBlockProps {
  // brief.ask как есть (бэк режет text до 120 символов; строки переносятся,
  // не обрезаются). null/undefined — тревоги нет: спокойный вид.
  ask?: FeatureAsk | null;
  // Имя человека по id (именительный падеж) — для подстроки; кнопки склоняют
  // его существующими helper'ами (dativeName/genitiveName).
  nameOf: (personaId: string) => string;
  isMobile: boolean;
  // Действие кнопки; не передан — кнопки нет (текст остаётся).
  onAnswer?: () => void;
}

export function FeatureAskBlock({ ask, nameOf, isMobile, onAnswer }: FeatureAskBlockProps) {
  // Живая давность — через useNow (Date.now() в ренере морозит «назад» на последнем
  // ренере, хвост RV-K-Q5I-2); интервал живёт, только пока есть от чего считать.
  const live = useNow(60_000, ask?.since != null);
  const calm = ask == null;
  const stopped = ask?.source === 'stopped';
  const name = ask?.personaId ? nameOf(ask.personaId) : null;
  const sinceMin = ask?.since ? minutesSince(ask.since, live) : null;
  const dur = sinceMin != null ? formatDuration(sinceMin) : null;

  // Тон — по виду: спокойный — neutral FeaturePageAlert, тревожный — TONE Notice.
  const tone = calm ? 'calm' : stopped ? 'danger' : 'warning';
  const bg = calm ? C.bgSelected : stopped ? C.dangerBg : C.warningBg;
  const border = calm ? C.borderLight : stopped ? C.dangerBorder : C.warning;
  const labelColor = calm ? C.textMuted : stopped ? C.dangerText : C.warningText;

  const label = calm ? 'От тебя' : stopped ? ASK_LABEL.blocker : ASK_LABEL.need;
  const phrase = ask ? markdownToPlain(ask.text) : 'Ничего не нужно — работа идёт';
  // Подстрока — только когда есть имя (контракт: нет personaId — подстроки нет);
  // давность добавляется, только когда есть от чего считать.
  const who = name ? `${name} ${stopped ? 'стоит' : 'ждёт'}${dur ? ` ${dur}` : ''}` : null;
  const buttonLabel = stopped
    ? (name ? `Открыть чат ${genitiveName(name)}` : 'Открыть чат')
    : ask?.source === 'merge'
      ? 'Решить по мержу'
      : name ? `Ответить ${dativeName(name)}` : 'Открыть чат';

  return (
    <div
      data-testid="feature-ask"
      data-tone={tone}
      role="status"
      aria-live="polite"
      style={{
        // Мобила: перенос — кнопка уходит под текст полной шириной (ревью F2-13,
        // как action в Notice); десктоп: текст занимает остаток строки, кнопка справа.
        display: 'flex', alignItems: 'center', gap: SP.md, flexWrap: isMobile ? 'wrap' : undefined,
        padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg,
        background: bg, border: `1px solid ${border}`,
        fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary,
      }}
    >
      <div style={{ flex: '1 1 220px', minWidth: 0 }}>
        <div style={{ fontSize: FS.xs, fontWeight: 600, lineHeight: 1.4, color: labelColor }}>{label}</div>
        <div style={{ marginTop: SP.xxs, fontWeight: calm ? 500 : 600, color: calm ? C.textSecondary : C.textHeading }}>{phrase}</div>
        {who && <div style={{ marginTop: SP.xxs, fontSize: FS.sm, color: C.textSecondary }}>{who}</div>}
      </div>
      {ask && onAnswer && (
        <Button
          size="sm"
          variant="primary"
          fullWidth={isMobile}
          dataTestId="feature-ask-answer"
          style={isMobile ? { minHeight: TB.iconHitMobile } : { marginLeft: 'auto', flexShrink: 0 }}
          onClick={onAnswer}
        >
          {buttonLabel}
        </Button>
      )}
    </div>
  );
}
