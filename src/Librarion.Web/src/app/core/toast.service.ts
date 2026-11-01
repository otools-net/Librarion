import { Injectable, signal } from '@angular/core';

export interface Toast {
    id: number;
    message: string;
    error: boolean;
}

/**
 * Short messages in the corner of the page (`Toasts`; port of SFE's `ToastService`), each gone after its time. Errors
 * stay longer; a pointer or the focus on them holds them all (WCAG 2.2.1), and each has a Close button.
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
    static readonly timeout = 5000;
    static readonly errorTimeout = 10000;

    private nextId = 0;
    private readonly list = signal<Toast[]>([]);
    private readonly timers = new Map<number, ReturnType<typeof setTimeout>>();
    private held = false;
    readonly toasts = this.list.asReadonly();

    show(message: string): void {
        this.add(message, false);
    }

    error(message: string): void {
        this.add(message, true);
    }

    dismiss(id: number): void {
        clearTimeout(this.timers.get(id));
        this.timers.delete(id);
        this.list.update(list => list.filter(t => t.id !== id));
    }

    /** While read (pointer or focus on them), none goes away. */
    hold(): void {
        this.held = true;
        this.timers.forEach(timer => clearTimeout(timer));
        this.timers.clear();
    }

    /** After reading, each gets its time again. */
    release(): void {
        this.held = false;
        this.list().forEach(toast => this.schedule(toast));
    }

    private add(message: string, error: boolean): void {
        const toast: Toast = { id: ++this.nextId, message, error };
        this.list.update(list => [...list, toast]);
        if (!this.held)
            this.schedule(toast);
    }

    private schedule(toast: Toast): void {
        this.timers.set(toast.id, setTimeout(() => this.dismiss(toast.id), toast.error ? ToastService.errorTimeout : ToastService.timeout));
    }
}
