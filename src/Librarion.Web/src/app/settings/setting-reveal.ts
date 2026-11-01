import { Injectable, signal } from '@angular/core';
import { SettingLevel } from '../core/models';

/**
 * A setting picked in the administration's search: the settings panel of its level, once its settings are shown,
 * scrolls to it, focuses it and flashes it (SFE's revealSetting), then forgets the request.
 */
@Injectable({ providedIn: 'root' })
export class SettingReveal {
    readonly pending = signal<{ key: string; level: SettingLevel } | null>(null);

    request(key: string, level: SettingLevel): void {
        // A new object: the same setting picked again flashes again.
        this.pending.set({ key, level });
    }
}
