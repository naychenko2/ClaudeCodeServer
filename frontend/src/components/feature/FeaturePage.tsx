import { useEffect, useState } from 'react';
import { ArrowLeft } from 'lucide-react';
import { C, R, FS, FONT, SHADOW } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { useFeaturePage } from '../../lib/featurePage';
import { getBackNav, clearBackNav } from '../../lib/featureBackNav';
import { useFeaturesIndex } from '../../lib/featuresIndex';
import { api } from '../../lib/api';
import { FLAGS, useFeature } from '../../lib/featureFlags';

// Единый focus-visible ring (клавиатура) — инжектим один раз (паттерн IconButton, §0).
const FOCUS_CLASS = 'cc-feature-crumb';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-crumb-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-crumb-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// Куда ведёт крошка «← …»:
//   chat — в исходный чат (указатель back-nav «чат → фича»);
//   key  — в ключевой чат фичи (указателя нет, но ключевой чат есть);
//   list — в список (указателя и ключевого чата нет).
// WorkspacePage исполняет выбор: закрывает страницу и ведёт в target.
export type FeaturePageBackTarget =
  | { kind: 'chat'; chatId: string }
  | { kind: 'key'; chatId: string }
  | { kind: 'list' };

export interface FeaturePageProps {
  // Id эпика (маршрут #/project/{pid}/feature/{fid}).
  featureId: string;
  // Проект чата (область запроса индекса); null — чат вне проектов.
  projectId: string | null;
  isMobile?: boolean;
  // Исполнитель «← назад»: закрывает страницу и ведёт в target.
  onBack: (target: FeaturePageBackTarget) => void;
}

// Страница фичи (волна 2 «Обзор фич», шаг Ф2.3): пока шапка-заглушка и крошка.
// Крошка читает указатель возврата: пришли из чата — «← К чату «…»», иначе
// «← К ключевому чату» (есть ключевой) или «← К списку».
//
// Компонент самодостаточен: данные — из стора useFeaturePage (Ф2.2), название и
// ключевой чат — из индекса фич. Крошка — единственная интерактивная точка шапки.
export function FeaturePage({ featureId, projectId, isMobile = false, onBack }: FeaturePageProps) {
  // Данные страницы (run + events) — из стора; здесь только для фолбэка заголовка.
  const page = useFeaturePage(featureId);
  const featuresOn = useFeature(FLAGS.featuresOverview);
  const index = useFeaturesIndex(projectId, featuresOn);
  const feature = index.features.find(f => f.featureId === featureId) ?? null;

  // Наведение на крошку (m3): фон bgSelected + цвет textPrimary (как у других ссылок-кнопок).
  const [hover, setHover] = useState(false);

  // Указатель возврата: «← назад» ведёт в чат, из которого перешли, только если
  // записанный переход ведёт именно в ЭТУ страницу фичи.
  const back = getBackNav();
  const fromChat = back && back.to.kind === 'feature' && back.to.id === featureId ? back.from : null;

  // Название исходного чата (для «← К чату «…»»): один запрос по id, пока его нет.
  const [fromTitle, setFromTitle] = useState<string | null>(null);
  useEffect(() => {
    if (!fromChat) { setFromTitle(null); return; }
    let cancelled = false;
    void api.chats.get(fromChat)
      .then(s => { if (!cancelled) setFromTitle(s.name ?? null); })
      .catch(() => { if (!cancelled) setFromTitle(null); });
    return () => { cancelled = true; };
  }, [fromChat]);

  // Крошка: куда ведёт и как подписана (указатель → ключевой чат → список).
  let target: FeaturePageBackTarget;
  let label: string;
  if (fromChat) {
    target = { kind: 'chat', chatId: fromChat };
    label = fromTitle ? `К чату «${fromTitle}»` : 'К чату';
  } else if (feature?.keySessionId) {
    target = { kind: 'key', chatId: feature.keySessionId };
    label = 'К ключевому чату';
  } else {
    target = { kind: 'list' };
    label = 'К списку';
  }

  const goBack = () => { onBack(target); clearBackNav(); };

  // Заголовок: из индекса (title), фолбэк — из эпика страницы, затем нейтральное.
  const title = feature?.title ?? (page.status === 'ok' ? page.page.run.epic.title : 'Фича');

  return (
    <div
      data-testid="feature-page"
      style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden', background: C.bgMain }}
    >
      {/* Крошка «← …» — единственная интерактивная точка шапки.
          Таблица §5.1/§6: 12 textSecondary, поля 4 8 4 4, R 8, gap 5, maxWidth 260 +
          многоточие; на 360 minHeight 40 (M2). Наведение bgSelected/textPrimary (m3),
          фокус SHADOW.focus (m3, §0). */}
      <div style={{ padding: isMobile ? '8px 8px 0' : '10px 14px 0' }}>
        <button
          type="button"
          data-testid="feature-page-back"
          className={FOCUS_CLASS}
          onClick={goBack}
          onMouseEnter={() => setHover(true)}
          onMouseLeave={() => setHover(false)}
          style={{
            display: 'inline-flex', alignItems: 'center', gap: 5,
            border: 'none', cursor: 'pointer',
            color: hover ? C.textPrimary : C.textSecondary,
            background: hover ? C.bgSelected : 'transparent',
            fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 400,
            padding: '4px 8px 4px 4px', borderRadius: R.md,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            maxWidth: 260, boxSizing: 'border-box',
            minHeight: isMobile ? 40 : undefined,
          }}
        >
          <ArrowLeft size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          {label}
        </button>
      </div>
      {/* Шапка-заглушка: название фичи + подпись, что тело появится в следующем шаге. */}
      <div style={{ flex: 1, overflow: 'auto', padding: isMobile ? '8px 8px' : '12px 14px' }}>
        <h2 style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.xl, color: C.textHeading }}>{title}</h2>
        <p style={{ margin: '8px 0 0', fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary }}>
          Содержимое страницы появится в следующем шаге.
        </p>
      </div>
    </div>
  );
}
