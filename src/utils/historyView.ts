export interface ScrollBookmark {
  top: number;
  count: number;
  anchorId?: string;
  anchorOffset?: number;
}

// Entry entrance animations translate the card without changing its layout position.
function entryTop(node: HTMLElement): number {
  const transform = getComputedStyle(node).transform;
  const translation = transform === "none" ? 0 : new DOMMatrixReadOnly(transform).m42;
  return node.getBoundingClientRect().top - translation;
}

export function captureBookmark(el: HTMLElement | null, count: number): ScrollBookmark {
  if (!el) return { top: 0, count };
  const top = el.getBoundingClientRect().top;
  const anchor = Array.from(el.querySelectorAll<HTMLElement>("[data-id]"))
    .find((node) => entryTop(node) + node.offsetHeight > top);
  return {
    top: el.scrollTop, count,
    anchorId: anchor?.dataset.id,
    anchorOffset: anchor ? entryTop(anchor) - top : undefined,
  };
}

export function restoreBookmark(el: HTMLElement | null, saved: ScrollBookmark) {
  if (!el) return;
  const anchor = saved.anchorId
    ? el.querySelector<HTMLElement>(`[data-id="${saved.anchorId}"]`) : null;
  el.scrollTop = anchor && saved.anchorOffset != null
    ? el.scrollTop + entryTop(anchor) - el.getBoundingClientRect().top - saved.anchorOffset
    : saved.top;
}
