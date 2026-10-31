import { Routes } from '@angular/router';
import { AccountPage } from './account/account-page';
import { SetupPage } from './account/setup-page';
import { SignInPage } from './account/sign-in-page';
import { ADMIN_SECTIONS, AdminPage } from './admin/admin-page';
import { GroupsSection } from './admin/groups-section';
import { LibrariesSection } from './admin/libraries-section';
import { SecuritySection } from './admin/security-section';
import { SettingsSection } from './admin/settings-section';
import { TenantsSection } from './admin/tenants-section';
import { UsersSection } from './admin/users-section';
import { HomePage } from './home/home-page';
import { NotFoundPage } from './home/not-found-page';
import { ADMIN_RIGHTS } from './core/rights';
import { needsRight, signedIn } from './core/session.service';
import { Shell } from './shell/shell';

const sectionRights = (path: string) => needsRight(...ADMIN_SECTIONS.find(section => section.path === path)!.rights);

export const routes: Routes = [
    { path: 'setup', component: SetupPage, title: 'Librarion' },
    { path: 'login', component: SignInPage, title: 'Librarion' },
    {
        path: '',
        component: Shell,
        canActivate: [signedIn],
        children: [
            { path: '', component: HomePage, title: 'Librarion' },
            { path: 'account', component: AccountPage, title: 'Librarion' },
            {
                path: 'admin',
                component: AdminPage,
                canActivate: [needsRight(...ADMIN_RIGHTS)],
                title: 'Librarion',
                children: [
                    { path: 'users', component: UsersSection, canActivate: [sectionRights('users')] },
                    { path: 'groups', component: GroupsSection, canActivate: [sectionRights('groups')] },
                    { path: 'libraries', component: LibrariesSection, canActivate: [sectionRights('libraries')] },
                    { path: 'tenants', component: TenantsSection, canActivate: [sectionRights('tenants')] },
                    { path: 'settings', component: SettingsSection, canActivate: [sectionRights('settings')] },
                    { path: 'security', component: SecuritySection, canActivate: [sectionRights('security')] },
                ],
            },
            { path: '**', component: NotFoundPage, title: 'Librarion' },
        ],
    },
];
