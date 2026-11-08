// S2d-6c-F2: пилюля «История решений» в настройках нового чата — пятое место той же
// настройки (дефект 29f45c4b). Путь: новый проектный чат делают приватным (до первого
// сообщения это можно), затем в пустом состоянии выключают «Не сохранять решения» →
// бэк отказывает 409 { code: "private_chat_dossiers" } (SessionsController.cs:126).
// Раньше catch в persist (NewChatSetup.tsx:55) молчал: тумблер не двигался без объяснения.
// Теперь — тот же текст, что в четырёх закрытых местах (общая точка разбора —
// chatFieldsErrorText, api.ts:1064; в компоненте нет второго литерала отказа); прочие
// ошибки — нейтральный текст, как у соседних контролов той же настройки
// (DossierOptOutButton.tsx:35-39, ExpiryButton.tsx:37-38). Отказ не меняет локальное
// состояние: тумблер остаётся включённым, onSessionUpdated не зовётся.
// Мутация «не разбирать код» (в NewChatSetup вместо chatFieldsErrorText — нейтральный
// литерал) → кейс 409 красный, два прогона; откат — зелёный.
import { createElement } from 'react';
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Project, Session } from '../../../types';
import { installFakeDom, mount, reactProps, findNodeByAttr, textNodes } from './fakeDom';

installFakeDom();

// Частичное обновление полей и выбор эндпоинта по projectId — в updateChatFields.
// chatUpdate: подменён только updateChatFields (разбор кода — НАСТОЯЩИЙ api:
// тексты отказов живут в api и проверяются как есть). Тип мока — реальная сигнатура
// вызова (chatUpdate.ts:38)
const updateMock = vi.hoisted(() => vi.fn<(s: Session, p: { excludeFromDossiers?: boolean | null }) => Promise<Session>>());
const toastMock = vi.hoisted(() => vi.fn());
vi.mock('../../../lib/chatUpdate', () => ({ updateChatFields: updateMock }));
vi.mock('../../../lib/toast', () => ({ showToast: toastMock }));

import { NewChatSetup } from '../NewChatSetup';
import { OWNER_SETTINGS_DENIED } from '../../../features/projects/members/participantGate';

// Пилюля «История решений» есть только у проектного чата (личные в историю не пишутся)
const project = { id: 'p1', name: 'Проект' } as unknown as Project;
// Общий проект по роли (QA-P1G): myRole авторитетна (types/index.ts:318)
const sharedProject = (myRole: 'editor' | 'reader' | 'owner'): Project => ({ ...project, myRole }) as Project;

const chat = (over: Partial<Session> = {}): Session => ({
  id: 'c1', projectId: 'p1', mode: 'auto', status: 'active', messageCount: 0,
  createdAt: '2026-11-01T10:00:00Z', updatedAt: '2026-11-01T10:00:00Z', origin: 'manual',
  ...over,
} as Session);

// Ошибка как её бросает request() (offline.ts:1664): status и тело целиком висят на Error
const httpError = (status: number, body: { error?: string; code?: string }): Error => {
  const e = new Error(body.error ?? 'error') as Error & { status?: number; body?: unknown };
  e.status = status;
  e.body = body;
  return e;
};

// Монтаж одного экземпляра; afterEach снимает root — панель живёт внутри контейнера,
// без снятия её DOM остался бы в следующем кейсе
let current: { root: ReturnType<typeof mount>['root'] } | null = null;
// proj — проект для рендера; null — чат вне проекта (проп project не передаётся вовсе)
function render(session: Session, proj: Project | null = project) {
  const onSessionUpdated = vi.fn();
  const m = mount(createElement(NewChatSetup, {
    session,
    ...(proj === null ? {} : { project: proj }),
    onSessionUpdated,
    isMobile: false,
  }));
  current = m;
  return { ...m, onSessionUpdated };
}

// Пилюля по её подписи: кнопок в экране три (срок/история/теги), различаются только
// текстом подписи — это и есть то, что человек видит на экране
function pillByLabel(n: any, label: string): any | null {
  const buttons: any[] = [];
  const walk = (x: any): void => {
    if (x.nodeType === 1 && x.nodeName === 'BUTTON') buttons.push(x);
    for (const c of x.childNodes ?? []) walk(c);
  };
  walk(n);
  return buttons.find(b => textNodes(b).join('').includes(label)) ?? null;
}

const openDossiers = (m: { container: ReturnType<typeof mount>['container'] }) => {
  const btn = pillByLabel(m.container, 'История решений');
  expect(btn).not.toBeNull();
  act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
};
const switchNode = (m: { container: ReturnType<typeof mount>['container'] }) =>
  findNodeByAttr(m.container as never, 'aria-label', 'Не сохранять решения из этого чата');

beforeEach(() => {
  updateMock.mockReset();
  toastMock.mockReset();
});
afterEach(() => {
  const c = current;
  current = null;
  if (c) act(() => { c.root.unmount(); });
});

describe('NewChatSetup: выключение opt-out в приватном чате — человек читает текст, не молчание', () => {
  // Приватный чат с уже включённым opt-out — то самое состояние из шагов дефекта:
  // его человек пытается выключить, и бэк это запрещает
  const privateChat = () => chat({ privateToUserId: 'me-1', excludeFromDossiers: true });

  it('409 private_chat_dossiers → причина и действие; тумблер остаётся включённым, без onSessionUpdated', async () => {
    const s = privateChat();
    updateMock.mockRejectedValue(httpError(409, {
      error: 'Пока чат приватный, «Не сохранять решения» нельзя выключить',
      code: 'private_chat_dossiers',
    }));
    const m = render(s);
    openDossiers(m);
    const sw = switchNode(m);
    expect(sw).not.toBeNull();
    expect(sw!.getAttribute('aria-checked')).toBe('true');
    await act(async () => { (reactProps(sw as never)!.onClick as () => void)(); });
    // Запрос ушёл именно с выключением opt-out (не с включением)
    expect(updateMock).toHaveBeenCalledWith(s, { excludeFromDossiers: false });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [title, text] = toastMock.mock.calls[0] as [string, string];
    // Заголовок — подпись той пилюли, которую человек трогал
    expect(title).toBe('История решений');
    expect(text).toBe('Пока чат приватный, решения из него не сохраняются — это нельзя выключить. Сначала сделайте чат общим.');
    expect(text).not.toContain('409');
    expect(text).not.toContain('private_chat_dossiers');
    // Отказ: значение не оптимистичное — тумблер остался включённым, наверх не ушло ничего
    expect(switchNode(m)!.getAttribute('aria-checked')).toBe('true');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('409 с незнакомым кодом → нейтральный текст (код человеку не показывается), тумблер прежний', async () => {
    updateMock.mockRejectedValue(httpError(409, { error: 'что-то другое', code: 'other_code' }));
    const m = render(privateChat());
    openDossiers(m);
    await act(async () => { (reactProps(switchNode(m) as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Не удалось изменить настройку чата');
    expect(switchNode(m)!.getAttribute('aria-checked')).toBe('true');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('ошибка без тела (сетевой сбой) → тот же нейтральный текст', async () => {
    updateMock.mockRejectedValue(new Error('сеть недоступна'));
    const m = render(privateChat());
    openDossiers(m);
    await act(async () => { (reactProps(switchNode(m) as never)!.onClick as () => void)(); });
    expect(toastMock).toHaveBeenCalledTimes(1);
    const [, text] = toastMock.mock.calls[0] as [string, string];
    expect(text).toBe('Не удалось изменить настройку чата');
    expect(m.onSessionUpdated).not.toHaveBeenCalled();
  });

  it('успех (общий чат): ответ бэка уходит наверх, тоста нет', async () => {
    const s = chat({ excludeFromDossiers: true });
    const updated = { ...s, excludeFromDossiers: false } as Session;
    updateMock.mockResolvedValue(updated);
    const m = render(s);
    openDossiers(m);
    await act(async () => { (reactProps(switchNode(m) as never)!.onClick as () => void)(); });
    expect(updateMock).toHaveBeenCalledWith(s, { excludeFromDossiers: false });
    expect(m.onSessionUpdated).toHaveBeenCalledWith(updated);
    expect(toastMock).not.toHaveBeenCalled();
  });
});

// QA-P1G (находка Глеба на дельта-ревью QA-P1F): пилюли «Время жизни» и «История решений»
// в пустом чате не гейтились по роли: редактор общего проекта выбирал срок и получал
// 403 owner_settings (бэковый OwnerSettingsGate — настройки закрыты и редактору, и читателю).
// Теперь тот же гейт, что у кнопок шапки (ExpiryButton.tsx:36): пилюля неактивна с причиной,
// панель не открывается, поля не уходят на PUT. Владелец, обычный проект (без myRole) и чат
// вне проекта — как раньше; «Теги» вне owner-гейта (в PUT редактору открыты name/tags,
// participantGate.ts:69). Приватный переключатель — отдельная дверь автора, не трогается.
// Мутация «пилюли не гейтятся» (в NewChatSetup settingsDenied = null) → оба ролевых кейса
// красные: клик открывает панель и поле уходит в updateChatFields; откат — зелёный.
describe('NewChatSetup: участник общего проекта (QA-P1G)', () => {
  const pillBtn = (m: { container: ReturnType<typeof mount>['container'] }, label: string): any => {
    const b = pillByLabel(m.container, label);
    expect(b, `пилюля не найдена: ${label}`).not.toBeNull();
    return b;
  };
  const click = (b: any): void => { act(() => { (reactProps(b as never)!.onClick as () => void)(); }); };

  for (const role of ['editor', 'reader'] as const) {
    it(`${role}: «Время жизни» и «История решений» выключены с причиной; клик не открывает панель и не шлёт поле`, () => {
      const m = render(chat(), sharedProject(role));
      const exp = pillBtn(m, 'Время жизни');
      const dos = pillBtn(m, 'История решений');
      expect(exp.getAttribute('disabled')).not.toBeNull();
      expect(exp.getAttribute('title')).toContain(OWNER_SETTINGS_DENIED);
      expect(dos.getAttribute('disabled')).not.toBeNull();
      expect(dos.getAttribute('title')).toContain(OWNER_SETTINGS_DENIED);
      // «неактивен» проверяется поведением: даже прямой клик не открывает панель и
      // не выпускает поле на updateChatFields — бэковый отказ не обещается запросом
      click(exp);
      click(dos);
      expect(updateMock).not.toHaveBeenCalled();
      expect(m.onSessionUpdated).not.toHaveBeenCalled();
      // панель не открылась: ни тумблера opt-out, ни описания ExpiryPicker в DOM нет
      expect(switchNode(m)).toBeNull();
      expect(textNodes(m.container).join('')).not.toContain('Временный чат удалится сам');
    });
  }

  it('редактору «Теги» можно (в PUT открыты name/tags) — пилюля активна, без причины', () => {
    const m = render(chat(), sharedProject('editor'));
    const tags = pillBtn(m, 'Теги');
    expect(tags.getAttribute('disabled')).toBeNull();
    expect(tags.getAttribute('title')).toBeNull();
  });

  it('владельцу (myRole: owner) — активны без причины, панель открывается и работает как раньше', async () => {
    const s = chat({ excludeFromDossiers: true });
    const updated = { ...s, excludeFromDossiers: false } as Session;
    updateMock.mockResolvedValue(updated);
    const m = render(s, sharedProject('owner'));
    const exp = pillBtn(m, 'Время жизни');
    expect(exp.getAttribute('disabled')).toBeNull();
    expect(exp.getAttribute('title')).toBeNull();
    openDossiers(m);
    await act(async () => { (reactProps(switchNode(m) as never)!.onClick as () => void)(); });
    expect(updateMock).toHaveBeenCalledWith(s, { excludeFromDossiers: false });
    expect(m.onSessionUpdated).toHaveBeenCalledWith(updated);
  });

  it('обычный проект (без myRole) — активны без причины, как раньше', () => {
    const m = render(chat());
    for (const label of ['Время жизни', 'История решений', 'Теги']) {
      const b = pillBtn(m, label);
      expect(b.getAttribute('disabled'), label).toBeNull();
      expect(b.getAttribute('title'), label).toBeNull();
    }
  });

  it('чат вне проекта — «Время жизни» активно, «История решений» не показывается (как раньше)', () => {
    const m = render(chat(), null);
    const exp = pillBtn(m, 'Время жизни');
    expect(exp.getAttribute('disabled')).toBeNull();
    expect(exp.getAttribute('title')).toBeNull();
    expect(pillByLabel(m.container, 'История решений')).toBeNull();
  });
});
