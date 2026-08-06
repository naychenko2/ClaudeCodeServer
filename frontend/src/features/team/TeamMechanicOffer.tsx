// Мост в командные механики (фича default-personas-onboarding): руководитель проекта
// предлагает механику маркером <team-mechanic id="..." topic="..."/> в тексте ответа
// (протокол — TeamMechanicsPromptCatalog на бэке). Здесь — парсер/стрижка маркера
// (только вне код-блоков; незакрытый префикс в хвосте стрима прячется) и карточка
// с кнопкой «Запустить»: сам запуск — buildTeamTurnText по клику, автозапуска нет.

import { C, FONT, FS, R, SP, SHADOW } from '../../lib/design';
import { Button } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Check } from 'lucide-react';
import {
  TEAM_MECHANICS, DEFAULT_TEAM_SETTINGS, costEstimate, type TeamMechanicId,
} from './teamMechanics';

export interface TeamMechanicOffer {
  id: TeamMechanicId;
  topic: string;
}

// Полный маркер предложения механики
const MARKER_RE = /<team-mechanic\b[^>]*?\/>/g;
const MARKER_TAG = '<team-mechanic';

// Разбивка текста на код/не-код: fenced-блоки ``` (включая незакрытый хвост) и
// inline-спаны `...` — маркер парсится и вырезается ТОЛЬКО вне кода
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

function parseAttrs(marker: string): TeamMechanicOffer | null {
  const id = /\bid="([^"]+)"/.exec(marker)?.[1];
  const topic = /\btopic="([^"]*)"/.exec(marker)?.[1] ?? '';
  if (!id) return null;
  // id строго из реестра — незнакомый маркер не рендерим карточкой (текст всё равно стрижётся)
  if (!TEAM_MECHANICS.some(m => m.id === id)) return null;
  return { id: id as TeamMechanicId, topic };
}

// Первый валидный маркер в тексте (вне код-блоков); null — предложения нет
export function parseTeamMechanicOffer(text: string): TeamMechanicOffer | null {
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
// чтобы пользователь не видел сырой «<team-mechanic id="…» до конца дельты
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
export function stripTeamMechanicMarkers(text: string, streaming?: boolean): string {
  if (!text.includes('<te')) return text; // быстрый выход — маркеров заведомо нет
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

// Карточка предложения механики: имя/иконка/ориентир цены + «Запустить» (C.accent —
// главное действие). После запуска кнопка гаснет; одна механика — одна карточка на чат
// (дедуп по ленте делает ChatPanel).
export function TeamMechanicOfferCard({ offer, launched, onRun }: {
  offer: TeamMechanicOffer;
  launched: boolean;
  onRun: () => void;
}) {
  const mech = TEAM_MECHANICS.find(m => m.id === offer.id);
  if (!mech) return null;
  const Icon = mech.icon;
  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderLeft: `3px solid ${C.accent}`,
      borderRadius: 12, background: C.bgWhite, boxShadow: SHADOW.card,
      padding: `${SP.sm}px ${SP.md}px`, display: 'flex', alignItems: 'center', gap: SP.md,
      flexWrap: 'wrap', maxWidth: '100%',
    }}>
      <div style={{
        width: 34, height: 34, borderRadius: R.lg, background: C.accentLight, color: C.accent,
        display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}>
        <Icon size={ICON_SIZE.md} strokeWidth={ICON_STROKE} />
      </div>
      <div style={{ flex: 1, minWidth: 160 }}>
        <div style={{ fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading }}>
          {mech.name}
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 400, color: C.textMuted, marginLeft: 8 }}>
            {costEstimate(mech.id, DEFAULT_TEAM_SETTINGS)}
          </span>
        </div>
        {offer.topic && (
          <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 1, overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {offer.topic}
          </div>
        )}
      </div>
      {launched ? (
        <span style={{
          display: 'inline-flex', alignItems: 'center', gap: 5, flexShrink: 0,
          fontSize: FS.xs, fontWeight: 600, color: C.success,
        }}>
          <Check size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          Запущено
        </span>
      ) : (
        <Button variant="primary" size="sm" onClick={onRun}>Запустить</Button>
      )}
    </div>
  );
}
