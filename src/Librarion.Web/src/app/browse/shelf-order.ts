import { signal } from '@angular/core';

/*
 * A shelf's own order on its page (agents/docs/features/shelves.md): where books go when moved, the list as it shows
 * them after a move (before the server has answered), and dragging them by their grip (mouse, pen or finger alike).
 */

/** How a book (or the books selected with it) moves: to the start, one place up or down, to the end. */
export type ShelfMove = 'start' | 'up' | 'down' | 'end';

/**
 * The list after `ids` (in the list's order) are moved to just before the book `before` (null: the end). When the
 * list doesn't hold the whole shelf yet (`complete` false), books moved to the end leave it: they come again with the
 * last page.
 */
export function movedLocally<T extends { id: number }>(items: readonly T[], ids: readonly number[], before: number | null, complete: boolean): T[] {
    const moving = new Set(ids);
    const moved = items.filter(item => moving.has(item.id));
    const rest = items.filter(item => !moving.has(item.id));
    if (before === null)
        return complete ? [...rest, ...moved] : rest;
    const at = rest.findIndex(item => item.id === before);
    return at < 0 ? [...items] : [...rest.slice(0, at), ...moved, ...rest.slice(at)];
}

/** `ids` in the order the list has them (a selection is moved in its order on the shelf). */
export function inListOrder(items: readonly { id: number }[], ids: Iterable<number>): number[] {
    const wanted = new Set(ids);
    return items.filter(item => wanted.has(item.id)).map(item => item.id);
}

/**
 * Where `ids` go for a move: the place among the other books (`index`: before the `index`-th of them; their count or
 * more: after the last loaded), or null when they are where it would put them already.
 */
export function moveIndex(items: readonly { id: number }[], ids: readonly number[], move: ShelfMove): number | null {
    const moving = new Set(ids);
    const first = items.findIndex(item => moving.has(item.id));
    if (first < 0)
        return null;
    const rest = items.length - ids.length;
    // The place of the first moved among the others: how many of them come before it.
    const place = items.slice(0, first).filter(item => !moving.has(item.id)).length;
    // Down from the last loaded: past the first of the next page (`index` beyond the others loaded).
    const index = move === 'start' ? 0 : move === 'up' ? Math.max(0, place - 1) : move === 'down' ? place + 1 : rest;
    const together = items.slice(first, first + ids.length).every(item => moving.has(item.id));
    return together && index === place && move !== 'end' ? null : index;
}

/** The drop place while dragging: before the book `id`, or after it. */
export interface DropTarget {
    id: number;
    after: boolean;
}

export interface ShelfDragOptions {
    /** The list's element (the `ul`). */
    list: () => HTMLElement | undefined;
    grid: () => boolean;
    /** The books a drag of `book` moves: the selection when the book is in it, else the book. */
    moving: (book: number) => number[];
    drop: (ids: number[], target: DropTarget) => void;
}

/** Past this many pixels a press on the grip is a drag, not a click (which opens the Move dialog). */
const DRAG_START = 6;
/** Near the window's top or bottom while dragging, the page scrolls (px; the bottom keeps clear of the selection bar). */
const EDGE_TOP = 64;
const EDGE_BOTTOM = 96;

/**
 * Dragging books by their grip with pointer events (the same for a mouse, a pen and a finger: the grip has
 * `touch-action: none`). The window listens while a drag lasts, so a grip drawn out of the list's window ends nothing;
 * near the window's edges the page scrolls. Escape cancels.
 */
export class ShelfDrag {
    /** The books being dragged; null when none. */
    readonly dragging = signal<ReadonlySet<number> | null>(null);
    readonly target = signal<DropTarget | null>(null);

    private press: { id: number; pointer: number; x: number; y: number } | null = null;
    private last = { x: 0, y: 0 };
    private frame = 0;
    private dragged = false;

    constructor(private readonly options: ShelfDragOptions) {}

    start(event: PointerEvent, book: number): void {
        if (event.button !== 0 || this.press)
            return;
        this.press = { id: book, pointer: event.pointerId, x: event.clientX, y: event.clientY };
        this.dragged = false;
        addEventListener('pointermove', this.move);
        addEventListener('pointerup', this.up);
        addEventListener('pointercancel', this.cancel);
        addEventListener('keydown', this.key);
    }

    /** True once after a drag: the grip's click that ends it opens nothing. */
    consumeClick(): boolean {
        const dragged = this.dragged;
        this.dragged = false;
        return dragged;
    }

    private readonly move = (event: PointerEvent): void => {
        const press = this.press;
        if (!press || event.pointerId !== press.pointer)
            return;
        this.last = { x: event.clientX, y: event.clientY };
        if (!this.dragging()) {
            if (Math.hypot(event.clientX - press.x, event.clientY - press.y) < DRAG_START)
                return;
            this.dragging.set(new Set(this.options.moving(press.id)));
            this.frame = requestAnimationFrame(this.scroll);
        }
        event.preventDefault();
        this.aim();
    };

    private readonly up = (event: PointerEvent): void => {
        if (event.pointerId !== this.press?.pointer)
            return;
        const dragging = this.dragging();
        const target = this.target();
        this.dragged = !!dragging;
        this.end();
        if (dragging && target)
            this.options.drop([...dragging], target);
    };

    private readonly cancel = (): void => this.end();

    private readonly key = (event: KeyboardEvent): void => {
        if (event.key === 'Escape' && this.dragging()) {
            event.preventDefault();
            event.stopPropagation();
            this.dragged = true;
            this.end();
        }
    };

    /** The book under the pointer, and which half of it: the drop place. */
    private aim(): void {
        const dragging = this.dragging();
        const list = this.options.list();
        const element = document.elementFromPoint(this.last.x, this.last.y)?.closest<HTMLElement>('li.book');
        const id = Number(element?.dataset['book']);
        if (!dragging || !element || !list?.contains(element) || !id || dragging.has(id)) {
            this.target.set(null);
            return;
        }
        const box = element.getBoundingClientRect();
        const after = this.options.grid() ? this.last.x > box.left + box.width / 2 : this.last.y > box.top + box.height / 2;
        const current = this.target();
        if (current?.id !== id || current.after !== after)
            this.target.set({ id, after });
    }

    private readonly scroll = (): void => {
        if (!this.dragging())
            return;
        const { y } = this.last;
        const step = y < EDGE_TOP ? -Math.ceil((EDGE_TOP - y) / 4) : y > innerHeight - EDGE_BOTTOM ? Math.ceil((y - innerHeight + EDGE_BOTTOM) / 4) : 0;
        if (step) {
            scrollBy(0, step);
            this.aim();
        }
        this.frame = requestAnimationFrame(this.scroll);
    };

    private end(): void {
        cancelAnimationFrame(this.frame);
        removeEventListener('pointermove', this.move);
        removeEventListener('pointerup', this.up);
        removeEventListener('pointercancel', this.cancel);
        removeEventListener('keydown', this.key);
        this.press = null;
        this.dragging.set(null);
        this.target.set(null);
    }

    /** The page is left mid-drag. */
    destroy(): void {
        this.end();
    }
}
