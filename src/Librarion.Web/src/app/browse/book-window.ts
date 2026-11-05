/*
 * Which books of a long list are drawn (agents/docs/features/browse.md): only the rows in view and a few around them,
 * the rest kept as empty space above and below, so a library of millions keeps a page of a few hundred elements.
 */

export interface BookWindow {
    /** The first book drawn (index in the loaded list). */
    start: number;
    /** One past the last book drawn. */
    end: number;
    /** Empty space for the rows above and below the drawn ones. */
    before: number;
    after: number;
}

/**
 * The books to draw: `top` is how far the list's top is above the viewport's (negative while it is below it),
 * `viewport` the window's height, `rowHeight` a row with its gap, `columns` books per row, `count` the books loaded.
 * `buffer` rows are drawn beyond each edge so a quick scroll doesn't show empty space.
 */
export function bookWindow(top: number, viewport: number, rowHeight: number, columns: number, count: number, buffer = 4): BookWindow {
    const rows = Math.ceil(count / Math.max(1, columns));
    if (rows === 0 || rowHeight <= 0)
        return { start: 0, end: count, before: 0, after: 0 };
    const firstRow = Math.max(0, Math.min(rows - 1, Math.floor(top / rowHeight) - buffer));
    const lastRow = Math.max(firstRow, Math.min(rows - 1, Math.ceil((top + viewport) / rowHeight) + buffer));
    return {
        start: firstRow * columns,
        end: Math.min(count, (lastRow + 1) * columns),
        before: firstRow * rowHeight,
        after: (rows - lastRow - 1) * rowHeight,
    };
}

/** Books per row of a grid `width` wide, cards at least `minCard` wide with `gap` between them. */
export function gridColumns(width: number, minCard: number, gap: number): number {
    return Math.max(1, Math.floor((width + gap) / (minCard + gap)));
}
