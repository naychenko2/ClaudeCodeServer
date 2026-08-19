// Нормализация DOMSnapshot.captureSnapshot в плоский список узлов.
// Формат Chrome 128+ (проверено на Chrome 151):
//   raw.strings — общий пул строк для всех документов;
//   documents[].nodes.attributes — массив по узлам, каждый элемент — флэт [nameIdx, valIdx, ...];
//   documents[].nodes.nodeValue — индексы строк: содержимое #text-узлов (собственный текст родителя);
//   documents[].layout.nodeIndex — по одной записи на layout-объект (узел может иметь несколько),
//     bounds — 4 числа на запись, text — индекс строки текстбокса записи (-1 если нет).
// Геометрия узла — первая его layout-запись; собственный текст — конкатенация nodeValue
// #text-детей; атрибуты — только id/class/role/data-testid/aria-label.

export function normalizeDomSnapshot(raw) {
  const nodes = [];
  const docs = [];
  const S = raw.strings;
  for (const doc of raw.documents) {
    docs.push({ documentURL: S[doc.documentURL] ?? String(doc.documentURL), frameId: doc.frameId ?? null, nodeCount: doc.nodes.nodeName ? doc.nodes.nodeName.length : 0 });
    const N = doc.nodes;
    const count = N.nodeName.length;
    if (!count) continue;

    // layout-записи по узлам: первая rect, все тексты.
    // bounds бывает вложенным ([[x,y,w,h],...], Chrome 128+) или флэтовым (старые версии).
    const layByNode = new Map();
    if (doc.layout) {
      const L = doc.layout;
      for (let li = 0; li < L.nodeIndex.length; li++) {
        const ni = L.nodeIndex[li];
        const rec = layByNode.get(ni) || { rect: null, texts: [] };
        if (!rec.rect) {
          const b = L.bounds[li];
          rec.rect = Array.isArray(b) ? b : [L.bounds[li * 4], L.bounds[li * 4 + 1], L.bounds[li * 4 + 2], L.bounds[li * 4 + 3]];
        }
        const ti = L.text ? L.text[li] : -1;
        if (ti >= 0 && S[ti]) rec.texts.push(S[ti]);
        layByNode.set(ni, rec);
      }
    }

    // Собственный текст: nodeValue #text-узлов приписываем родителю
    const ownText = new Map();
    for (let i = 0; i < count; i++) {
      if (S[N.nodeName[i]] !== '#text') continue;
      const parent = N.parentIndex ? N.parentIndex[i] : -1;
      if (parent < 0) continue;
      const v = N.nodeValue && N.nodeValue[i] >= 0 ? S[N.nodeValue[i]] : null;
      if (!v) continue;
      ownText.set(parent, ((ownText.get(parent) || '') + ' ' + v).trim());
    }

    for (let i = 0; i < count; i++) {
      const tag = S[N.nodeName[i]] || '';
      if (tag.startsWith('#') || tag === 'HTML' || tag === '!DOCTYPE') continue;
      const lay = layByNode.get(i);
      if (!lay || !lay.rect) continue;
      const [x, y, w, h] = lay.rect;
      if (w <= 0 && h <= 0) continue;
      const attrs = {};
      const pairs = (N.attributes && N.attributes[i]) || [];
      for (let p = 0; p + 1 < pairs.length; p += 2) {
        const name = S[pairs[p]];
        if (name === 'id' || name === 'class' || name === 'role' || name === 'data-testid' || name === 'aria-label') attrs[name] = S[pairs[p + 1]];
      }
      // Путь в дереве: tag:nth среди одноимённых соседей
      const parts = [];
      let cur = i, guard = 0;
      while (cur >= 0 && guard++ < 64) {
        const curTag = S[N.nodeName[cur]];
        if (!curTag.startsWith('#')) {
          let idx = 1, sib = cur - 1;
          while (sib >= 0 && S[N.nodeName[sib]] === curTag) { sib--; idx++; }
          parts.unshift(curTag.toLowerCase() + ':' + idx);
        }
        cur = N.parentIndex ? N.parentIndex[cur] : -1;
      }
      nodes.push({
        bid: N.backendNodeId ? N.backendNodeId[i] : -1,
        tag: tag.toLowerCase(),
        path: parts.join('/'),
        x: Math.round(x), y: Math.round(y), w: Math.round(w), h: Math.round(h),
        text: (ownText.get(i) || lay.texts.join(' ') || null)?.slice(0, 80) ?? null,
        attrs: Object.keys(attrs).length ? attrs : null,
      });
    }
  }
  return { nodes, docs };
}
