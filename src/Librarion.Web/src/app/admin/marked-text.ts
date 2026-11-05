import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { markWords } from './admin-search';

/** Text with the words searched for marked (`<mark>`), as SFE's search results. */
@Component({
    selector: 'lib-marked-text',
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `@for (part of parts(); track $index) {@if (part.match) {<mark>{{ part.text }}</mark>} @else {{{ part.text }}}}`,
})
export class MarkedText {
    readonly text = input.required<string>();
    /** Normalized words (`searchWords`). */
    readonly words = input.required<string[]>();

    protected readonly parts = computed(() => markWords(this.text(), this.words()));
}
