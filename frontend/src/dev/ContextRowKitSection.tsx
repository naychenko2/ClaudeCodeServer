import { Island, IslandHeader } from '../components/ui';
import { C, FS, ISLAND, SP } from '../lib/design';
import { ContextRowView, rowFacts, type ContextRowViewProps, type RowExec, type RowGit } from '../components/chat/ContextRowView';
import { ladderNominal, ladderRungs } from '../lib/chatContext/ladder';
import type { ChatContextPrimary, ChatContextRef } from '../lib/chatContext/types';
import heroPng from '../assets/hero.png';
import aiHomePng from '../assets/ai-home.png';

// Витрина строки контекста хода (ADR-023, макет composer-context-row-v1): все ступени лестницы
// для типового состояния (объект, «Чем», два референса, проект) и для режима «Чат», плюс
// телефон, ✦ агента, плашка «Вернуть», личный чат. Ширина строки задана рамкой в CSS-пикселях;
// тема — общим переключателем страницы.

const noop = () => {};
const git: RowGit = {
  label: 'feat/video-editor', changes: 3, ahead: 1, publishN: 1,
  onCommitOwn: noop, onCommitAll: noop, onPublish: noop, onShowChanges: noop,
};
const exec: RowExec = {
  rows: [
    { id: 'auto', group: 'auto', name: 'Qwen-Image Edit', sub: 'локально', price: 'бесплатно · ~40 с', free: true, amount: null, unit: 'free', etaSeconds: 40, badges: [{ label: 'без RU', tone: 'warn' }] },
    { id: 'kontext', group: 'cloud', name: 'FLUX Kontext', sub: 'fal', price: '$0.04 / шт.', free: false, amount: 0.04, unit: 'usd', badges: [{ label: 'RU', tone: 'good' }] },
  ],
  value: 'auto', onChange: noop, title: 'Чем выполнить «Изменить»',
};
const item = { ref: {}, addedAt: '', missing: false };
const primary = (over: Partial<ChatContextPrimary> = {}): ChatContextPrimary =>
  ({ ...item, id: 'p', kind: 'image', by: 'human', label: 'hero.png', version: 'v2', thumb: heroPng, role: null, ...over });
const ref = (id: string, label: string, over: Partial<ChatContextRef> = {}): ChatContextRef =>
  ({ ...item, id, kind: 'image', by: 'human', label, version: null, thumb: aiHomePng, role: 'style', usedBy: ['edit'], ...over });
const REFS = [ref('anya', 'Аня', { role: 'char' }), ref('palette', 'palette.png')];

const view = (over: Partial<ContextRowViewProps>): ContextRowViewProps => ({
  width: 926, isMobile: false, git, primary: primary(), refs: REFS, exec, actionLabel: 'Изменить',
  iconOf: () => null, offer: null, onUndo: noop, onRelease: noop, onDetach: noop, onClear: noop, ...over,
});

function Frame({ caption, px, children }: { caption: string; px: number; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ fontSize: FS.sm, color: C.textMuted }}>{caption}</div>
      <div style={{ width: px, maxWidth: '100%' }}>{children}</div>
    </div>
  );
}

function Ladder({ title, base }: { title: string; base: Partial<ContextRowViewProps> }) {
  const p = view(base);
  const key = rowFacts(p);
  const rungs = ladderRungs(key);
  return (
    <>
      <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{title}</div>
      {rungs.map((f, i) => {
        const need = ladderNominal(key, f);
        return (
          <Frame key={i} px={need + 2} caption={`ступень ${i} · номинал ${need} px`}>
            <ContextRowView {...p} width={need} />
          </Frame>
        );
      })}
      <Frame px={360} caption="ниже последней ступени — строка прокручивается">
        <ContextRowView {...p} width={ladderNominal(key, rungs[rungs.length - 1]) - 1} />
      </Frame>
    </>
  );
}

export function ContextRowKitSection() {
  return (
    <Island>
      <IslandHeader title="Строка контекста" />
      <div style={{ display: 'flex', flexDirection: 'column', gap: ISLAND.gap, padding: SP.md }}>
        <Ladder title="Объект, «Чем», 2 референса, проект" base={{}} />
        <Ladder title="Тот же состав в «Чате»: «Чем» нет" base={{ exec: null, actionLabel: null }} />
        <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>Состояния</div>
        <Frame px={760} caption="объект от агента ✦, «Чат»">
          <ContextRowView {...view({ exec: null, actionLabel: null, primary: primary({ by: 'agent', label: 'кот.png', version: 'v1' }), refs: [] })} />
        </Frame>
        <Frame px={760} caption="серый референс: действие «Стемы» его не берёт">
          <ContextRowView {...view({ actionLabel: 'Стемы', refs: [ref('voice', 'Марина', { role: 'voice', usedBy: [] }), REFS[1]] })} />
        </Frame>
        <Frame px={760} caption="плашка «Вернуть» после ✕ на объекте">
          <ContextRowView {...view({ primary: null, exec: null, refs: [], offer: { text: 'hero.png' } })} />
        </Frame>
        <Frame px={760} caption="проект без объекта — только ветка">
          <ContextRowView {...view({ primary: null, exec: null, refs: [] })} />
        </Frame>
        <Frame px={760} caption="личный чат: ветки нет, строка начинается с объекта">
          <ContextRowView {...view({ git: null })} />
        </Frame>
        <Frame px={342} caption="телефон 360: строка прокручивается, ветка иконкой с бейджем">
          <ContextRowView {...view({ isMobile: true, width: 342 })} />
        </Frame>
      </div>
    </Island>
  );
}
