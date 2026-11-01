import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { SystemInfo } from './models';

/** Header every request changing something carries (the server's RequestGuard refuses it otherwise). */
export const REQUEST_HEADER = 'X-Librarion-Request';

/**
 * The only class that talks to the server (agents/docs/development/frontend.md). Promises; URLs stay relative, so
 * the app works under a virtual directory.
 */
@Injectable({ providedIn: 'root' })
export class Api {
    private readonly http = inject(HttpClient);

    systemInfo(): Promise<SystemInfo> {
        return firstValueFrom(this.http.get<SystemInfo>('api/system/info'));
    }
}
