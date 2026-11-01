import { Routes } from '@angular/router';
import { SetupPage } from './account/setup-page';
import { SignInPage } from './account/sign-in-page';
import { AdminPage, adminMatcher } from './admin/admin-page';
import { HomePage } from './home/home-page';
import { NotFoundPage } from './home/not-found-page';
import { signedIn } from './core/session.service';
import { leaveWithUnsaved } from './core/unsaved-changes';
import { Shell } from './shell/shell';

export const routes: Routes = [
    { path: 'setup', component: SetupPage, title: 'Librarion' },
    { path: 'login', component: SignInPage, title: 'Librarion' },
    {
        path: '',
        component: Shell,
        canActivate: [signedIn],
        children: [
            { path: '', component: HomePage, title: 'Librarion' },
            // My account is a section of the administration (as SFE's); the old address still opens it.
            { path: 'account', redirectTo: 'admin/account' },
            // Each section shows only to the rights of its sub-pages (admin-sections.ts); the API checks them again.
            { matcher: adminMatcher, component: AdminPage, canDeactivate: [leaveWithUnsaved], title: 'Librarion' },
            { path: '**', component: NotFoundPage, title: 'Librarion' },
        ],
    },
];
