// Блок «Ревью» (F14): раунды, блокеры file:line, ожидание раунда 2, легаси, пусто, откат на старый сервер.
//
// Мутации (в отчёте): (1) не рисовать запись pending → тест «раунд 2 запустится сам» красный;
// (2) не склеивать file:line → тест «блокеры» красный; (3) не показывать подсказку легаси → тест «легаси» красный;
// (4) заметки обратно в plain pre-wrap → тест «заметки» красный; (5) снять markdownToPlain у блокера/замечания →
//     тест «блокеры и замечания» красный.

import { act, createElement } from 'react';
import { describe, it, expect, afterEach } from 'vitest';
import { FeatureStageReview } from './FeatureStageReview';
import { findNodeByAttr, installFakeDom, mount, textNodes, uninstallFakeDom } from '../chat/__tests__/fakeDom';
import type { StageReview, StageReviewRound } from '../../types/featureStage';

type FNode = Parameters<typeof findNodeByAttr>[0];
const roots: ReturnType<typeof mount>['root'][] = [];
const text = (c: FNode) => textNodes(c).join('');

const round = (over: Partial<StageReviewRound>): StageReviewRound => ({
  round: 1, reviewer: { personaId: 'p', name: 'Глеб' }, verdict: 'returned', returnReason: 'contract',
  at: null, blockers: [], remarks: [], structured: true, pending: false, ...over,
});
const rev = (rounds: StageReviewRound[], hint: string | null = null, items: Partial<StageReview> = {}): StageReview => ({
  own: null, reviews: [], awaitingHuman: null, reviewRounds: { rounds, structured: true, hint }, ...items,
});
function render(r: StageReview, executorName?: string) {
  installFakeDom();
  const m = mount(createElement(FeatureStageReview, { review: r, executorName }));
  roots.push(m.root);
  return m;
}
afterEach(() => { for (const r of roots) act(() => { r.unmount(); }); roots.length = 0; uninstallFakeDom(); });

describe('FeatureStageReview', () => {
  it('раунд: ревьюер, номер, причина, блокеры с file:line и замечания', () => {
    const { container } = render(rev([round({
      blockers: [{ tag: 'блокер', file: 'Svc.cs', line: 412, text: 'порог из конфига' }, { tag: 'блокер', file: null, line: null, text: 'нет сторожа' }],
      remarks: ['имя расходится'],
    })]));
    const t = text(container);
    expect(t).toContain('возвращено');
    expect(t).toContain('Глеб · раунд 1 · причина «контракт»');
    expect(t).toContain('Svc.cs:412 — порог из конфига');
    expect(t).toContain('нет сторожа');
    expect(t).toContain('имя расходится');
  });

  it('pending: «раунд 2 · ждёт доработку · запустится сам»', () => {
    const { container } = render(rev([round({}), round({ round: 2, reviewer: null, verdict: null, returnReason: null, pending: true })]), 'Тимур');
    expect(findNodeByAttr(container, 'data-testid', 'stage-review-auto')).not.toBeNull();
    expect(text(container)).toContain('ждёт доработку исполнителя (Тимур)');
  });

  it('легаси: «принято · Глеб · 1 раунд» и подсказка без блокеров', () => {
    const { container } = render(rev([round({ verdict: 'accepted', returnReason: null, structured: false, remarks: ['сырой текст'] })], 'legacy'));
    const t = text(container);
    expect(t).toContain('принято');
    expect(t).toContain('Глеб · 1 раунд');
    expect(t).not.toContain('сырой текст');
    expect(findNodeByAttr(container, 'data-testid', 'stage-review-legacy')).not.toBeNull();
  });

  it('structured=false без hint: подписи про старый формат нет', () => {
    const r = rev([round({ verdict: 'accepted', returnReason: null, structured: false })]);
    r.reviewRounds!.structured = false;
    const { container } = render(r);
    expect(findNodeByAttr(container, 'data-testid', 'stage-review-legacy')).toBeNull();
    expect(text(container)).not.toContain('Подробностей нет');
  });

  it('смешанный случай: блокер структурного раунда виден, подписи про старый формат нет', () => {
    const r = rev([
      round({ round: 1, verdict: 'returned', structured: true, blockers: [{ tag: 'блокер', file: 'Svc.cs', line: 412, text: 'порог из конфига' }] }),
      round({ round: 2, verdict: 'accepted', returnReason: null, structured: false, remarks: ['сырой текст'] }),
    ]);
    r.reviewRounds!.structured = false;
    const { container } = render(r);
    const t = text(container);
    expect(t).toContain('Svc.cs:412 — порог из конфига');
    expect(findNodeByAttr(container, 'data-testid', 'stage-review-blocker')).not.toBeNull();
    expect(findNodeByAttr(container, 'data-testid', 'stage-review-legacy')).toBeNull();
    expect(t).not.toContain('Подробностей нет');
  });

  it('пусто: «Ревью ещё не было»', () => {
    const { container } = render(rev([]));
    expect(findNodeByAttr(container, 'data-testid', 'stage-review-empty')).not.toBeNull();
    expect(text(container)).toContain('Ревью ещё не было');
  });

  it('старый сервер без reviewRounds: прежний список с бейджем «блокер»', () => {
    const { container } = render({
      own: null, awaitingHuman: null,
      reviews: [{ taskId: 'r1', title: 'ревью', role: 'review', status: 'done', verdict: 'returned', returnReason: null, notes: 'x', blocker: true, verifiedAt: null }],
    });
    expect(findNodeByAttr(container, 'data-testid', 'stage-review-r1')).not.toBeNull();
    expect(text(container)).toContain('блокер');
  });

  // === Markdown (Ф-2.46b): заметки — общий рендер (как в ленте), строки блокеров/замечаний — плоская версия ===
  it('заметки: markdown рендерится разметкой (список/strong/code), сырых «**» и кавычек нет', () => {
    const { container } = render(rev([], null, {
      reviews: [{
        taskId: 'r1', title: 'ревью', role: 'review', status: 'done',
        verdict: 'accepted_with_remarks', returnReason: null, blocker: false, verifiedAt: null,
        notes: '- пункт **один** `code`\n- второй',
      }],
    }));
    const notes = findNodeByAttr(container, 'data-testid', 'stage-review-notes');
    expect(notes).not.toBeNull();
    // Обёртка заметок обязана переносить длинные пути file:line (ревью 1abfa5e5): без этого
    // карточка разъезжается на 360.
    expect(notes!.style.overflowWrap, 'обёртка заметок не защищена от переполнения').toBe('anywhere');
    // Рендер даёт настоящие li/strong/code: обычный наивный вывод этих узлов не даст.
    expect(container.querySelectorAll('li,strong,code').length).toBeGreaterThanOrEqual(3);
    const t = text(container);
    expect(t).toContain('пункт один code');
    expect(t).not.toContain('**');
    expect(t).not.toContain('`');
  });

  it('блокеры и замечания: одиночные строки без символов разметки', () => {
    const { container } = render(rev([round({
      blockers: [{ tag: 'блокер', file: 'Svc.cs', line: 412, text: '**важно** `x`' }],
      remarks: ['**важно** `y`'],
    })]));
    const t = text(container);
    expect(t).toContain('Svc.cs:412 — важно x');
    expect(t).toContain('важно y');
    expect(t).not.toContain('**');
    expect(t).not.toContain('`');
  });
});
