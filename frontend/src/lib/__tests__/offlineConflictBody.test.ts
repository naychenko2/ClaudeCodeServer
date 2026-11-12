// Контракт конфликта сохранения (К8): тело 409 разбирается в одном месте — общий для
// файлов и заметок, отличается только code. Проверяем сам предикат: чужой code, чужой
// статус или пустота не должны молча становиться конфликтом — вызывающий обязан
// пробросить ошибку дальше как обычную.
import { describe, it, expect } from 'vitest';
import { conflictBodyOf } from '../offline';

const BODY = {
  code: 'file_changed',
  sha256: 'bbb222',
  content: 'чужой текст',
  author: null,
  changedAt: '2026-11-01T10:00:00Z',
  source: null,
};

const errOf = (status: number, body: unknown) => Object.assign(new Error(`HTTP ${status}`), { status, body });

describe('conflictBodyOf', () => {
  it('409 со своим code — тело конфликта как есть', () => {
    const body = conflictBodyOf(errOf(409, BODY), 'file_changed');
    expect(body).not.toBeNull();
    expect(body?.sha256).toBe('bbb222');
    expect(body?.content).toBe('чужой текст');
    expect(body?.changedAt).toBe('2026-11-01T10:00:00Z');
  });

  it('409 с чужим code — null (ошибка остаётся обычной)', () => {
    expect(conflictBodyOf(errOf(409, BODY), 'note_changed')).toBeNull();
  });

  it('не 409 — null', () => {
    expect(conflictBodyOf(errOf(500, BODY), 'file_changed')).toBeNull();
  });

  it('null — null', () => {
    expect(conflictBodyOf(null, 'file_changed')).toBeNull();
  });
});
