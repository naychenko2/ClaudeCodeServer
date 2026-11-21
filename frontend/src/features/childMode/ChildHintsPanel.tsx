// KM-F7: содержимое панели «Как мы учимся» — лесенка подсказок и сигнал «Мне неприятно».
// Только содержимое: рамку панели (PanelShell/шторку) ставит KM-F10. Вид — по panelBody
// макета kids-shell-v2.html: ступени 1→2→3 сверху вниз, пройденная — маркер на successBg,
// текущая — строка на infoBg с маркером на info, будущая — приглушённый номер на bgInset.
// Сигнал — POST /child/stop-signal (ADR-041, ручка №9): 202 и 429 too_frequent — оба
// «сигнал отправлен» (сервер схлопнул повтор, повторный запрос не повод мучить ребёнка);
// любой другой отказ — warning-плашка и кнопка снова доступна.
// «Сигнал отправлен» привязан к sessionId: сменили чат — состояния старого чата не
// показываются, поздний ответ старого запроса в новом не всплывает.

import { useRef, useState } from 'react';
import { AlertTriangle, Bell, Check, Hand } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { Button } from '../../components/ui/Button';
import { Notice } from '../../components/ui/Notice';
import { SectionLabel } from '../tasks/bits';
import { childApi } from './api';

const STEPS = ['Намёк', 'Похожий пример', 'Один шаг вместе'];

// Маркер ступени: 24 px, как .n в макете (число 12 px, 700).
const MARK = 24;

interface Props {
  // Пройденные ступени лесенки: 0 — ничего пройденного (текущая — первая),
  // 3 — все пройдены (текущей нет). Значение вне 0..3 не ломает рисунок:
  // ступени всегда ровно STEPS.length.
  step: number;
  sessionId: string;
}

export function ChildHintsPanel({ step, sessionId }: Props) {
  const isMobile = useIsMobile();
  const [sending, setSending] = useState(false);
  const [sentFor, setSentFor] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const inFlight = useRef(false);

  async function sendSignal() {
    // Блокировка кнопки до ответа: в браузере disabled не кликается; ref держит
    // «один запрос» и там, где клик синтетический (быстрый двойной, dispatch из теста).
    if (inFlight.current) return;
    inFlight.current = true;
    setSending(true);
    setFailed(false);
    const sid = sessionId; // запрос принадлежит чату, с которого нажали
    try {
      await childApi.stopSignal(sid);
      setSentFor(sid);
    } catch (e) {
      const err = e as { status?: number; body?: { code?: string } } | null;
      if (err?.status === 429 && err?.body?.code === 'too_frequent') {
        // Сервер схлопнул повтор — сигнал уже ушёл, ребёнку показываем то же состояние,
        // что и при 202 (ADR-041: «не чаще раза в минуту на чат»).
        setSentFor(sid);
      } else {
        setFailed(true);
      }
    } finally {
      inFlight.current = false;
      setSending(false);
    }
  }

  // Ключ состояния — sessionId: для другого чата (в т. ч. позднего ответа старого
  // запроса) плашки «сигнал отправлен» нет.
  const sent = sentFor !== null && sentFor === sessionId;

  return (
    <div>
      <SectionLabel style={{ padding: `${SP.md}px ${SP.md}px ${SP.sm}px` }}>Лесенка подсказок</SectionLabel>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `0 ${SP.md}px` }}>
        {STEPS.map((label, i) => {
          const done = i < step;
          const cur = i === step && step < STEPS.length;
          return (
            <div
              key={label}
              data-testid="hint-step"
              data-state={done ? 'done' : cur ? 'cur' : 'future'}
              style={{
                display: 'flex', gap: SP.sm, alignItems: 'center',
                padding: `${SP.sm}px ${SP.sm + 2}px`, borderRadius: R.lg,
                fontFamily: FONT.sans, fontSize: FS.base,
                color: cur ? C.textHeading : C.textSecondary,
                background: cur ? C.infoBg : 'transparent',
              }}
            >
              <span
                aria-hidden
                style={{
                  width: MARK, height: MARK, borderRadius: R.full, flexShrink: 0,
                  display: 'grid', placeItems: 'center',
                  fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 700,
                  background: done ? C.successBg : cur ? C.info : C.bgInset,
                  color: done ? C.successText : cur ? C.onAccent : C.textMuted,
                }}
              >
                {done ? <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} /> : i + 1}
              </span>
              {label}
            </div>
          );
        })}
      </div>
      <p style={{ margin: `${SP.sm}px 0 0`, padding: `0 ${SP.md}px ${SP.lg}px`, fontFamily: FONT.sans, fontSize: FS.base, lineHeight: 1.5, color: C.textSecondary }}>
        Готового ответа помощник не даёт — его находишь ты. Каждая ступень подсказывает чуть больше.
      </p>

      <SectionLabel style={{ padding: `0 ${SP.md}px ${SP.sm}px` }}>Если что-то не так</SectionLabel>
      <div style={{ padding: `0 ${SP.md}px ${SP.lg}px`, display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <p style={{ margin: 0, fontFamily: FONT.sans, fontSize: FS.base, lineHeight: 1.5, color: C.textSecondary }}>
          Если тебе неприятно, страшно или помощник сказал что-то странное — скажи. Это не стыдно.
        </p>
        {/* Куратор видит уроки ребёнка дословно (решение Андрея 19.11): плашка говорит только
            то, что делает сам сигнал. Формулировка — по карточке Ф1i. */}
        {sent ? (
          <Notice tone="info" icon={Bell}>
            Папе пришёл сигнал, что тебе нужна помощь. Ты ни в чём не виновата.
          </Notice>
        ) : (
          <>
            {failed && (
              <Notice tone="warning" icon={AlertTriangle}>
                Не получилось отправить, попробуй ещё раз
              </Notice>
            )}
            <Button
              variant="ghost"
              // sm (32) на широком экране — по макету; на мобиле тач-цель ≥ 40 px (спека)
              size={isMobile ? 'md' : 'sm'}
              loading={sending}
              leftIcon={<Hand size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
              onClick={sendSignal}
              dataTestId="stop-signal"
            >
              Мне неприятно
            </Button>
          </>
        )}
      </div>
    </div>
  );
}
