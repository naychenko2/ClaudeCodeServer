import { useEffect, useState } from 'react';
import { History } from 'lucide-react';
import type { ChangelogDay, DaySummaryStub } from '../../types';
import { api } from '../../lib/api';
import { C, FONT } from '../../lib/design';
import { PRODUCT_HISTORY_EVENT, productHistorySeenKey } from '../../components/HubHeader';
import { HERO_SCORE, ScoreBadge, scoreBadge } from '../../components/ProductHistory';
import { WidgetCard, WidgetAction, WidgetEmpty } from './WidgetCard';

// Человеческая подпись дня сводки («за сегодня» / «за вчера» / «за 16 июля»)
function dayLabel(date: string): string {
  const [y, m, d] = date.split('-').map(Number);
  const day = new Date(y, m - 1, d);
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const diff = Math.round((today.getTime() - day.getTime()) / 86_400_000);
  if (diff === 0) return 'за сегодня';
  if (diff === 1) return 'за вчера';
  return `за ${day.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' })}`;
}

// «Что нового»: топ-5 пунктов последнего дня с ГОТОВОЙ сводкой продукта.
// Важно: читаем только кеш — day() дергается исключительно для cached-дней
// (запрос холодного дня запустил бы LLM-генерацию на минуты). Кеш держит
// теплым фоновый прогрев (ChangelogWarmupService).
export function WhatsNewWidget({ userId }: { userId?: string | null }) {
  const [day, setDay] = useState<ChangelogDay | null>(null);
  const [freshest, setFreshest] = useState<DaySummaryStub | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [hasNew, setHasNew] = useState(false);

  useEffect(() => {
    let alive = true;
    api.history.days()
      .then(days => {
        if (!alive) return;
        setFreshest(days[0] ?? null);
        const cached = days.find(d => d.cached);
        if (!cached) { setLoaded(true); return; }
        return api.history.day(cached.date).then(d => {
          if (!alive) return;
          setDay(d);
          setLoaded(true);
        });
      })
      .catch(() => { if (alive) setLoaded(true); });
    return () => { alive = false; };
  }, []);

  // Индикатор новизны — та же логика, что бейдж в HubHeader: нет метки
  // «просмотрено» либо есть коммиты новее нее → accent-точка у заголовка
  useEffect(() => {
    let seen: string | null = null;
    try { seen = localStorage.getItem(productHistorySeenKey(userId)); } catch { /* ignore */ }
    if (!seen) setHasNew(true);
    else api.history.newCount(seen).then(({ count }) => setHasNew(count > 0)).catch(() => {});
    // Открыли историю (из любого места) → App пишет метку, гасим точку
    const reset = () => setHasNew(false);
    window.addEventListener(PRODUCT_HISTORY_EVENT, reset);
    return () => window.removeEventListener(PRODUCT_HISTORY_EVENT, reset);
  }, [userId]);

  const openHistory = () => window.dispatchEvent(new Event(PRODUCT_HISTORY_EVENT));

  const top = day
    ? [...day.items].sort((a, b) => b.score - a.score).slice(0, 5)
    : [];

  return (
    <WidgetCard
      icon={(
        <span style={{ position: 'relative', display: 'flex' }}>
          <History size={16} strokeWidth={2} />
          {hasNew && (
            <span style={{
              position: 'absolute', top: -2, right: -3, width: 7, height: 7,
              borderRadius: '50%', background: C.accent, border: `1.5px solid ${C.bgWhite}`,
            }} />
          )}
        </span>
      )}
      title="Что нового"
      action={<WidgetAction label="Вся история →" onClick={openHistory} />}
    >
      {!loaded ? (
        <WidgetEmpty text="Загрузка…" />
      ) : !day ? (
        <WidgetEmpty text={freshest && freshest.commitCount > 0
          ? `Сводка готовится — ${freshest.commitCount} изменений на подходе.`
          : 'Пока без изменений.'} />
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column' }}>
          <div style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, padding: '0 0 4px' }}>
            {dayLabel(day.date)}
          </div>
          {top.map((item, i) => (
            <button
              key={i}
              onClick={openHistory}
              style={{
                display: 'flex', alignItems: 'center', gap: 9, width: '100%', textAlign: 'left',
                background: 'none', border: 'none', borderRadius: 8, padding: '7px 8px',
                margin: '0 -8px', cursor: 'pointer', minWidth: 0,
              }}
              onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; }}
              onMouseLeave={e => { e.currentTarget.style.background = 'none'; }}
            >
              <span style={{ fontSize: 14, flexShrink: 0, lineHeight: 1 }}>{item.emoji}</span>
              <span style={{
                fontFamily: FONT.sans, fontSize: 13, color: C.textPrimary, flex: 1, minWidth: 0,
                whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
              }}>
                {item.title}
              </span>
              {/* Хиты (те, что на странице «Что нового» идут hero-карточками) помечаем
                  тем же бейджем с пузырем-репликой Claude; остальные — просто областью */}
              {item.score >= HERO_SCORE ? (
                <ScoreBadge badge={scoreBadge(item.score)} reason={item.scoreReason} />
              ) : (
                <span style={{
                  fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted, flexShrink: 0,
                  maxWidth: 110, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                }}>
                  {item.area}
                </span>
              )}
            </button>
          ))}
        </div>
      )}
    </WidgetCard>
  );
}
