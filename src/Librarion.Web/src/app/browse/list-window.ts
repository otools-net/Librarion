import { DestroyRef, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { bookWindow, gridColumns } from './book-window';

export interface ListWindowOptions {
    /** The list element (its rows are its children matching `item`). */
    element: () => HTMLElement | undefined;
    /** How many rows are loaded. */
    count: () => number;
    /** Laid out in columns (else one per row). */
    grid: () => boolean;
    /** The CSS variable holding a column's least width, and its value when unset. */
    minVar: string;
    minDefault: number;
    /** The selector of a row's element. */
    item: string;
}

/**
 * The rows of a long list drawn only around the viewport (agents/docs/features/browse.md): where the list is, how many
 * columns fit, a row's pitch (measured between the drawn rows' tops), and the window of rows to draw from them. Made
 * in a component's injection context: it follows the page's scroll and size until the component goes.
 */
export class ListWindow {
    private readonly injector = inject(Injector);
    private readonly top = signal(0);
    private readonly viewport = signal(typeof innerHeight === 'number' ? innerHeight : 800);
    readonly columns = signal(4);
    private readonly rowHeight = signal(0);
    /** Where to scroll back to once the restored list can be that tall. */
    private restoreTo: number | null = null;
    private frame = 0;

    readonly range = computed(() =>
        bookWindow(this.top(), this.viewport(), this.rowHeight(), this.options.grid() ? this.columns() : 1, this.options.count()));
    /** How far down the list is scrolled (its top above the viewport's). */
    readonly offset = computed(() => Math.max(0, this.top()));
    /** Rows of the current layout from the end of what is loaded: when to ask for more. */
    readonly perRow = computed(() => (this.options.grid() ? this.columns() : 1));

    constructor(private readonly options: ListWindowOptions) {
        const onScroll = () => this.schedule();
        addEventListener('scroll', onScroll, { passive: true });
        addEventListener('resize', onScroll, { passive: true });
        inject(DestroyRef).onDestroy(() => {
            removeEventListener('scroll', onScroll);
            removeEventListener('resize', onScroll);
            cancelAnimationFrame(this.frame);
        });
    }

    /** Rows change their height (another view): measured again from scratch. */
    reset(): void {
        this.rowHeight.set(0);
    }

    /** Scroll to `offset` once a row is measured (null: nothing to restore). */
    restore(offset: number | null): void {
        this.restoreTo = offset;
    }

    private schedule(): void {
        if (this.frame)
            return;
        this.frame = requestAnimationFrame(() => {
            this.frame = 0;
            this.measure();
        });
    }

    /** Where the list is and how its rows are: what the window needs. Called after each render and on scroll. */
    measure(): void {
        const list = this.options.element();
        if (!list)
            return;
        const style = getComputedStyle(list);
        const rect = list.getBoundingClientRect();
        this.top.set(-rect.top);
        this.viewport.set(innerHeight);
        const grid = this.options.grid();
        if (grid) {
            const gap = parseFloat(style.columnGap) || 0;
            const width = rect.width - (parseFloat(style.paddingLeft) || 0) - (parseFloat(style.paddingRight) || 0);
            const columns = gridColumns(width, parseFloat(style.getPropertyValue(this.options.minVar)) || this.options.minDefault, gap);
            // The rows drawn have the old count: measured again once drawn with the new one.
            if (columns !== this.columns()) {
                this.columns.set(columns);
                return;
            }
        }
        // A row's pitch, measured between the drawn rows' tops (the gap and any rounding included); with one row drawn,
        // its height and the gap.
        const drawn = list.querySelectorAll<HTMLElement>(this.options.item);
        const columns = grid ? this.columns() : 1;
        const rows = Math.floor((drawn.length - 1) / columns);
        let height = 0;
        if (rows >= 1)
            height = (drawn[rows * columns].getBoundingClientRect().top - drawn[0].getBoundingClientRect().top) / rows;
        else if (drawn[0]?.offsetHeight)
            height = drawn[0].offsetHeight + (parseFloat(style.rowGap) || 0);
        if (height > 0 && Math.abs(height - this.rowHeight()) > 0.5)
            this.rowHeight.set(height);
        if (this.restoreTo !== null && this.rowHeight() > 0) {
            const offset = this.restoreTo;
            this.restoreTo = null;
            afterNextRender(() => scrollTo({ top: list.getBoundingClientRect().top + scrollY + offset }), { injector: this.injector });
        }
    }
}
