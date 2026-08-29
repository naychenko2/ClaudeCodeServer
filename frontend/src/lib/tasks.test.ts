// TS-зеркало DefectRulesTests (backend/.../DefectRulesTests.cs): те же кейсы, тот же
// язык сообщений. Плюс чисто UI-часть (KIND_LABEL, OUTCOME_LABEL, isOpenDefect).

import { describe, it, expect } from 'vitest';
import {
  DefectRuleError, KIND_LABEL, OUTCOME_LABEL, REVIEW_COLUMN_ROLE,
  isOpenDefect, ensureNotClosedAtCreate, ensureVerificationOnClose,
  ensureReproOnReview, computeClosedWithoutCheck,
  type DefectOutcome, type DefectRepro, type TaskKind, type TaskVerification,
} from './tasks';
import type { TaskStatus } from '../types';

// ─── Хелперы в стиле DefectRulesTests (C# → TS) ────────────────────────────────

interface DefectLike {
  kind?: TaskKind;
  status?: TaskStatus;
  repro?: DefectRepro | null;
  verification?: TaskVerification | null;
  outcome?: DefectOutcome | null;
}

function defectIn(status: TaskStatus, overrides: {
  repro?: DefectRepro | null;
  verification?: TaskVerification | null;
  outcome?: DefectOutcome | null;
} = {}): DefectLike {
  return {
    kind: 'defect',
    status,
    repro: overrides.repro ?? null,
    verification: overrides.verification ?? null,
    outcome: overrides.outcome ?? null,
  };
}

function taskIn(status: TaskStatus): DefectLike {
  return { kind: 'task', status };
}

function reviewCol(role: string | null = 'review'): { role: string | null } {
  return { role };
}

// ─── Подписи KIND_LABEL и OUTCOME_LABEL ────────────────────────────────────────

describe('KIND_LABEL — подписи видов задач', () => {
  it('task = "Задача", defect = "Дефект"', () => {
    expect(KIND_LABEL.task).toBe('Задача');
    expect(KIND_LABEL.defect).toBe('Дефект');
  });
});

describe('OUTCOME_LABEL — подписи исходов дефекта', () => {
  it('closedWithoutCheck = "Снято без проверки"', () => {
    expect(OUTCOME_LABEL.closedWithoutCheck).toBe('Снято без проверки');
  });
});

describe('REVIEW_COLUMN_ROLE — триггер правила 3', () => {
  it('значение равно "review" (wire-токен бэковой BoardColumn.Role)', () => {
    expect(REVIEW_COLUMN_ROLE).toBe('review');
  });
});

// ─── isOpenDefect: предикат открытого дефекта ─────────────────────────────────

describe('isOpenDefect — Kind == Defect И Status != Done', () => {
  it('defect в todo = открытый дефект', () => {
    expect(isOpenDefect({ kind: 'defect', status: 'todo' })).toBe(true);
  });

  it('defect в inProgress = открытый дефект', () => {
    expect(isOpenDefect({ kind: 'defect', status: 'inProgress' })).toBe(true);
  });

  it('defect в done — НЕ открытый дефект', () => {
    expect(isOpenDefect({ kind: 'defect', status: 'done' })).toBe(false);
  });

  it('обычная задача в любом статусе — никогда не открытый дефект', () => {
    expect(isOpenDefect({ kind: 'task', status: 'todo' })).toBe(false);
    expect(isOpenDefect({ kind: 'task', status: 'inProgress' })).toBe(false);
    expect(isOpenDefect({ kind: 'task', status: 'done' })).toBe(false);
  });

  it('задача без kind — не открытый дефект', () => {
    expect(isOpenDefect({ status: 'todo' })).toBe(false);
  });
});

// ─── 1) ensureNotClosedAtCreate ────────────────────────────────────────────────

describe('ensureNotClosedAtCreate — TS-зеркало EnsureNotClosedAtCreate', () => {
  it('дефект в Done бросает DefectRuleError', () => {
    expect(() => ensureNotClosedAtCreate(defectIn('done'))).toThrow(DefectRuleError);
    expect(() => ensureNotClosedAtCreate(defectIn('done')))
      .toThrow(/нельзя создавать сразу в Done/);
  });

  it('дефект в Todo проходит', () => {
    expect(() => ensureNotClosedAtCreate(defectIn('todo'))).not.toThrow();
  });

  it('дефект в InProgress проходит', () => {
    expect(() => ensureNotClosedAtCreate(defectIn('inProgress'))).not.toThrow();
  });

  it('обычная задача в Done — no-op (Task не подчиняется правилам Defect)', () => {
    expect(() => ensureNotClosedAtCreate(taskIn('done'))).not.toThrow();
  });
});

// ─── 2) ensureVerificationOnClose ──────────────────────────────────────────────

describe('ensureVerificationOnClose — TS-зеркало EnsureVerificationOnClose', () => {
  it('дефект в Done без вердикта и без исхода бросает', () => {
    expect(() => ensureVerificationOnClose(defectIn('done')))
      .toThrow(DefectRuleError);
    expect(() => ensureVerificationOnClose(defectIn('done')))
      .toThrow(/заполните Verification.*ClosedWithoutCheck/);
  });

  it('дефект в Done с Verification проходит', () => {
    expect(() => ensureVerificationOnClose(defectIn('done', {
      verification: { verifiedAt: new Date().toISOString() },
    }))).not.toThrow();
  });

  it('дефект в Done с ClosedWithoutCheck проходит (внутренний путь)', () => {
    expect(() => ensureVerificationOnClose(defectIn('done', {
      outcome: 'closedWithoutCheck',
    }))).not.toThrow();
  });

  it('дефект в Done и с Verification, и с ClosedWithoutCheck проходит (дизъюнкция)', () => {
    expect(() => ensureVerificationOnClose(defectIn('done', {
      verification: { verifiedAt: new Date().toISOString() },
      outcome: 'closedWithoutCheck',
    }))).not.toThrow();
  });

  it('дефект не в Done — no-op (todo и inProgress пропускаются)', () => {
    expect(() => ensureVerificationOnClose(defectIn('todo'))).not.toThrow();
    expect(() => ensureVerificationOnClose(defectIn('inProgress'))).not.toThrow();
  });

  it('обычная задача в Done — no-op', () => {
    expect(() => ensureVerificationOnClose(taskIn('done'))).not.toThrow();
  });
});

// ─── 3) ensureReproOnReview ────────────────────────────────────────────────────

describe('ensureReproOnReview — TS-зеркало EnsureReproOnReview', () => {
  it('дефект в review-колонку без Repro.Steps бросает', () => {
    expect(() => ensureReproOnReview(defectIn('inProgress'), reviewCol()))
      .toThrow(DefectRuleError);
    expect(() => ensureReproOnReview(defectIn('inProgress'), reviewCol()))
      .toThrow(/Repro\.Steps/);
  });

  it('дефект в review с пустыми Steps бросает', () => {
    expect(() => ensureReproOnReview(defectIn('inProgress', { repro: { steps: '' } }), reviewCol()))
      .toThrow(DefectRuleError);
  });

  it('дефект в review с пробельными Steps бросает', () => {
    expect(() => ensureReproOnReview(defectIn('inProgress', { repro: { steps: '   ' } }), reviewCol()))
      .toThrow(DefectRuleError);
  });

  it('дефект в review с заполненными Steps проходит', () => {
    expect(() => ensureReproOnReview(defectIn('inProgress', {
      repro: { steps: '1. Открыть X\n2. Нажать Y' },
    }), reviewCol())).not.toThrow();
  });

  it('дефект в неревью-колонку — no-op (другая Role)', () => {
    expect(() => ensureReproOnReview(defectIn('inProgress'), { role: 'todo' }))
      .not.toThrow();
    expect(() => ensureReproOnReview(defectIn('inProgress'), { role: null }))
      .not.toThrow();
  });

  it('дефект без целевой колонки — no-op (targetColumn=null)', () => {
    expect(() => ensureReproOnReview(defectIn('inProgress'), null))
      .not.toThrow();
    expect(() => ensureReproOnReview(defectIn('inProgress'), undefined))
      .not.toThrow();
  });

  it('обычная задача даже с review-колонкой — no-op', () => {
    expect(() => ensureReproOnReview(taskIn('inProgress'), reviewCol()))
      .not.toThrow();
  });

  it('дефект с заполненным Repro но пустыми Steps в review — бросает', () => {
    expect(() => ensureReproOnReview(defectIn('inProgress', {
      repro: { steps: '', expected: 'ожидалось X' },
    }), reviewCol())).toThrow(DefectRuleError);
  });
});

// ─── 4) computeClosedWithoutCheck ─────────────────────────────────────────────

describe('computeClosedWithoutCheck — TS-зеркало ComputeClosedWithoutCheck', () => {
  it('возвращает "closedWithoutCheck"', () => {
    expect(computeClosedWithoutCheck()).toBe('closedWithoutCheck');
  });
});
