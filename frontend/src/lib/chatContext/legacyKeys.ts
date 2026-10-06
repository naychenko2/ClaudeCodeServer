// Ключи localStorage прежних полос и режимов поля ввода (до ADR-023): ручной выбор полосы чата
// `cc-composer-strip:{chatId}` и свёрнутость `cc-composer-strip-collapsed:{chatId}:{полоса}`. Полос больше
// нет, ключи никому не нужны — один раз на старте убираем остатки, чтобы они не копились на устройстве.

const PREFIX = 'cc-composer-strip';

export function purgeLegacyComposerKeys(storage: Pick<Storage, 'length' | 'key' | 'removeItem'>): number {
  const stale: string[] = [];
  for (let i = 0; i < storage.length; i++) {
    const k = storage.key(i);
    if (k && k.startsWith(PREFIX)) stale.push(k);
  }
  stale.forEach(k => storage.removeItem(k));
  return stale.length;
}
