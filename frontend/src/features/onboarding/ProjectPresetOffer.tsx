// Мост в каталог каркасов (знакомство v2, п.4 плана): руководитель проекта предлагает
// пресет каркаса маркером <project-preset key="…"/> в тексте ответа (протокол —
// OnboardingPrompts на бэке). Здесь — парсер/стрижка маркера (только вне код-блоков;
// незакрытый префикс в хвосте стрима прячется) и карточка с кнопками «Создать» /
// «Не нужно». Состояние кнопок берётся из `project.presetKey`, а не из ленты:
// "pending" — можно применить/отказаться; "<ключ>" — каркас уже создан;
// "none" — человек отказался; null — проект создан до фичи, карточка не нужна.

import { C, FONT, FS, R, SP, SHADOW } from '../../lib/design';
import { Button } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { AlertCircle, Check, X } from 'lucide-react';

// Каталог пресетов — параллель PresetCatalog на бэке (claude-папка может добавить ещё,
// тогда карточка покажет «неизвестный пресет» и каркас применится молча). UI знает только
// три ключа, которые мы сейчас предлагаем; новые ключи не критичны для рендера «уже
// применён» — там хватает общего итога.
export interface PresetMeta {
  key: string;
  title: string;
}

export const PRESETS: ReadonlyArray<PresetMeta> = [
  { key: 'docs', title: 'Документный проект' },
  { key: 'dev', title: 'Разработка' },
  { key: 'personal', title: 'Личное дело' },
];

// Применённый ключ → название для подписи «Каркас создан: …»
export function presetTitle(key: string | null | undefined): string | null {
  if (!key || key === 'pending' || key === 'none') return null;
  return PRESETS.find(p => p.key === key)?.title ?? key;
}

// Полный маркер предложения пресета
const MARKER_RE = /<project-preset\b[^>]*?\/>/g;
const MARKER_TAG = '<project-preset';

// Разбивка текста на код/не-код: fenced-блоки ``` (включая незакрытый хвост) и
// inline-спаны `...` — маркер парсится и вырезается ТОЛЬКО вне кода. Близнец
// splitByCode из TeamMechanicOffer; вынесение в общий хелпер пока избыточно —
// разные маркеры могут расползтись по правилам.
function splitByCode(text: string): Array<{ code: boolean; s: string }> {
  const fenced: Array<{ code: boolean; s: string }> = [];
  const fence = /```[\s\S]*?(?:```|$)/g;
  let last = 0;
  for (let m = fence.exec(text); m; m = fence.exec(text)) {
    if (m.index > last) fenced.push({ code: false, s: text.slice(last, m.index) });
    fenced.push({ code: true, s: m[0] });
    last = m.index + m[0].length;
  }
  if (last < text.length) fenced.push({ code: false, s: text.slice(last) });

  const out: Array<{ code: boolean; s: string }> = [];
  for (const seg of fenced) {
    if (seg.code) { out.push(seg); continue; }
    const inline = /`[^`\n]*`/g;
    let l = 0;
    for (let m = inline.exec(seg.s); m; m = inline.exec(seg.s)) {
      if (m.index > l) out.push({ code: false, s: seg.s.slice(l, m.index) });
      out.push({ code: true, s: m[0] });
      l = m.index + m[0].length;
    }
    if (l < seg.s.length) out.push({ code: false, s: seg.s.slice(l) });
  }
  return out;
}

function parseAttrs(marker: string): { key: string } | null {
  const key = /\bkey="([^"]+)"/.exec(marker)?.[1];
  if (!key) return null;
  return { key };
}

// Первый валидный маркер в тексте (вне код-блоков); null — предложения нет
export function parseProjectPresetOffer(text: string): { key: string } | null {
  for (const seg of splitByCode(text)) {
    if (seg.code) continue;
    const m = seg.s.match(MARKER_RE);
    if (!m) continue;
    for (const raw of m) {
      const offer = parseAttrs(raw);
      if (offer) return offer;
    }
  }
  return null;
}

// Незакрытый маркер (или префикс тега) в самом хвосте стримящегося текста — спрятать,
// чтобы пользователь не видел сырой «<project-preset key="…» до конца дельты
function stripPartialTail(s: string): string {
  const idx = s.lastIndexOf(MARKER_TAG);
  if (idx !== -1 && !s.slice(idx).includes('/>')) return s.slice(0, idx);
  for (let n = Math.min(MARKER_TAG.length - 1, s.length); n > 0; n--) {
    if (MARKER_TAG.startsWith(s.slice(s.length - n))) return s.slice(0, s.length - n);
  }
  return s;
}

// Вырезать маркеры из отображаемого текста (вне код-блоков); при стриме — стричь
// и незакрытый префикс маркера в хвосте
export function stripProjectPresetMarkers(text: string, streaming?: boolean): string {
  if (!text.includes('<pr')) return text; // быстрый выход — маркеров заведомо нет
  const segs = splitByCode(text);
  let out = '';
  for (let i = 0; i < segs.length; i++) {
    const seg = segs[i];
    if (seg.code) { out += seg.s; continue; }
    let s = seg.s.replace(MARKER_RE, '');
    if (streaming && i === segs.length - 1) s = stripPartialTail(s);
    out += s;
  }
  return out;
}

// Минимальный срез text-элемента для сборщика карточек предложений: parentToolUseId
// отличает top-level текст от реплик сабагентов, а text — тело для парсера маркера.
export interface PresetOfferItem {
  kind: string;
  text?: string;
  parentToolUseId?: string;
}

// Карточку несёт ПОСЛЕДНЕЕ предложение каждого ключа в чате — при повторном маркере
// карточка «переезжает» к актуальной реплике. На практике у одного проекта только одна
// карточка на чат, но сборщик сохраняет тот же контракт, что у механик — последний по
// индексу оффер по каждому ключу.
export function buildProjectPresetOffer(items: readonly PresetOfferItem[]): Map<number, { key: string }> {
  const lastByKey = new Map<string, { index: number; offer: { key: string } }>();
  for (let i = 0; i < items.length; i++) {
    const it = items[i];
    if (it.kind !== 'text' || it.parentToolUseId) continue;
    const text = it.text;
    if (!text || !text.includes('<pr')) continue;
    const offer = parseProjectPresetOffer(text);
    if (!offer) continue;
    lastByKey.set(offer.key, { index: i, offer });
  }
  const map = new Map<number, { key: string }>();
  for (const { index, offer } of lastByKey.values()) map.set(index, offer);
  return map;
}

// Видимость карточки и её режим берём с сервера: на UI лента — только триггер «есть
// предложение», а решение «можно применять или уже применено/отклонено» — DTO проекта.
// Здесь — type-alias для пропсов ChatPanel, чтобы по месту не таскать условия.
export type PresetCardState =
  | { mode: 'pending' } // кнопки живые
  | { mode: 'applied'; key: string | null } // каркас создан
  | { mode: 'declined' } // человек отказался
  | { mode: 'hidden' }; // карточка не нужна (null / не наш ключ / устаревший оффер)

// Чистая деривация состояния карточки из серверного `presetKey`. Отдельно от
// ChatPanel — чтобы покрыть тестом: ключевое «presetKey === null» НЕ даёт активной
// кнопки ни при каком содержимом ленты (старые проекты, для которых каркас не
// предлагаем, или ещё не приехавшее DTO). Если в ленте нет ни одного маркера
// `<project-preset …/>` — карточка всё равно скрыта, чтобы пользователь не видел
// «кнопку до предложения».
export function resolvePresetCardState(
  presetKey: string | null | undefined,
  hasOffers: boolean,
): PresetCardState {
  if (presetKey == null) return { mode: 'hidden' };
  if (presetKey === 'pending') {
    return hasOffers ? { mode: 'pending' } : { mode: 'hidden' };
  }
  if (presetKey === 'none') return { mode: 'declined' };
  return { mode: 'applied', key: presetKey };
}

// «Не применилось»: локальная подсветка ошибки после клика. error === null — карточка
// в своём основном состоянии (pending/applied/declined). Не путать с card-styling.
export interface ProjectPresetOfferCardProps {
  state: PresetCardState;
  // Свой текст итога на случай «created»/«skipped» — карточка отрисует его под заголовком
  // вместо дефолтного «Каркас создан. …». Можно не передавать (тогда общий текст).
  appliedNote?: string | null;
  onApply: (key: string) => void;
  onDecline: () => void;
  // Inline-ошибка из последнего клика (409 и т.п.); под кнопкой показывается
  // текстом и НЕ перекрывает карточку — кнопка исчезает, остаётся только сообщение.
  error?: string | null;
  // Блокировка на время запроса (текст «Применяю…» на кнопке и disabled)
  busy?: boolean;
}

// Тексты — дословно из заметки «Тексты — Знакомство с проектом v2». Здесь берём
// «документный» вариант (он в плане выбран эталоном для текста «Разложить проект по
// полочкам»); на других ключах лента просто не покажет карточку — каркас уже применён.
const CARD_BODY = `
Создам папки под работу с документами: \`Исходники\` для первоисточников, \`Входящие\` для их текстовых копий, \`Встречи\`, \`Рабочие документы\` с актуальными версиями, \`Архив\` для старых. Плюс \`Статус.md\` — короткая сводка проекта, с которой открывается панель «Документы», и \`CLAUDE.md\` с правилами: первоисточник не трогаем, версия в имени файла, актуальная версия одна.
`.trim();
// Поля внутри backticks сохраняем как код (раз дизайн даёт один шрифт для inline-кода,
// нам хватает обычной строки — без тяжёлого markdown-парсера внутри карточки).
function renderBody(body: string): React.ReactNode {
  // Грубая подсветка `…` бэктиками внутри карточки (дизайн не обязывает полный markdown,
  // но имя файла `CLAUDE.md` хочется показать в моноширинном — так его проще выделить).
  const parts: React.ReactNode[] = [];
  const re = /`([^`\n]+)`/g;
  let last = 0;
  let m: RegExpExecArray | null;
  let idx = 0;
  while ((m = re.exec(body))) {
    if (m.index > last) parts.push(body.slice(last, m.index));
    parts.push(<code key={idx++} style={inlineCode}>{m[1]}</code>);
    last = m.index + m[0].length;
  }
  if (last < body.length) parts.push(body.slice(last));
  return parts;
}

const inlineCode: React.CSSProperties = {
  fontFamily: FONT.mono, fontSize: '0.95em', background: C.bgPanel,
  padding: '0 4px', borderRadius: 4,
};

export function ProjectPresetOfferCard({ state, appliedNote, onApply, onDecline, error, busy }: ProjectPresetOfferCardProps) {
  if (state.mode === 'hidden') return null;
  if (state.mode === 'applied' && error) {
    // «Уже применён, но локально поймали 409»: это не ошибка, а констатация факта —
    // пользователь пытался применить ещё раз, сервер сказал «уже». Один спокойный итог,
    // без success-шапки и error-строки под ней.
    return (
      <div style={cardShell}>
        <CardHeader icon={<Check size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.success }} />}
          title="Каркас для этого проекта уже разложен" />
        <BodyMuted>{appliedNote ?? 'Каркас применён — папки и правила в проекте.'}</BodyMuted>
      </div>
    );
  }
  if (state.mode === 'applied') {
    return (
      <div style={cardShell}>
        <CardHeader icon={<Check size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.success }} />}
          title={state.key ? `Каркас создан: ${presetTitle(state.key) ?? state.key}` : 'Каркас создан'} />
        <BodyMuted>{appliedNote ?? 'Всё это можно переименовать и переделать — правила лежат в CLAUDE.md проекта.'}</BodyMuted>
      </div>
    );
  }
  if (state.mode === 'declined') {
    return (
      <div style={cardShell}>
        <CardHeader icon={<X size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.textMuted }} />}
          title="Каркас не создавали" />
        <BodyMuted>Папки всегда можно завести руками.</BodyMuted>
        <ErrorLine message={error} />
      </div>
    );
  }
  // pending
  return (
    <div style={cardShell} aria-busy={busy || undefined}>
      <CardHeader icon={<AlertCircle size={ICON_SIZE.md} strokeWidth={ICON_STROKE} style={{ color: C.accent }} />}
        title="Разложить проект по полочкам" />
      <div style={bodyText}>{renderBody(CARD_BODY)}</div>
      <ErrorLine message={error} />
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, marginTop: SP.sm }}>
        <Button
          variant="primary" size="sm"
          disabled={busy}
          onClick={() => { if (!busy) onApply('docs'); }}
        >{busy ? 'Применяю…' : 'Создать'}</Button>
        <Button
          variant="ghost" size="sm"
          disabled={busy}
          onClick={() => { if (!busy) onDecline(); }}
        >Не нужно</Button>
      </div>
    </div>
  );
}

function CardHeader({ icon, title }: { icon: React.ReactNode; title: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
      <div style={{
        width: 34, height: 34, borderRadius: R.lg, background: C.accentLight,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}>{icon}</div>
      <div style={{ fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading }}>
        {title}
      </div>
    </div>
  );
}

function BodyMuted({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs, lineHeight: 1.5 }}>
      {children}
    </div>
  );
}

function ErrorLine({ message }: { message?: string | null }) {
  if (!message) return null;
  // role="alert" — скрин-ридер озвучит ошибку сразу при появлении, без фокуса
  return (
    <div role="alert" style={{
      marginTop: SP.sm, fontSize: FS.xs, color: C.danger,
      display: 'flex', alignItems: 'flex-start', gap: 6, lineHeight: 1.4,
    }}>
      <AlertCircle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 2 }} />
      <span>{message}</span>
    </div>
  );
}

const cardShell: React.CSSProperties = {
  border: `1px solid ${C.borderLight}`,
  borderLeft: `3px solid ${C.accent}`,
  borderRadius: 12,
  background: C.bgWhite,
  boxShadow: SHADOW.card,
  padding: `${SP.sm}px ${SP.md}px`,
  display: 'flex',
  flexDirection: 'column',
  gap: 0,
  maxWidth: '100%',
};

const bodyText: React.CSSProperties = {
  fontSize: FS.sm, color: C.textSecondary, marginTop: SP.xs, lineHeight: 1.5,
};
