// Classic script loaded in <head>, before the page is drawn, so that it never flashes the wrong theme (as SFE). It
// uses this browser's pick, else the device's mode; ThemeService (app/core/theme.service.ts) takes over once the app
// runs: keep the key and values in step with it.
//
// Not part of the Angular bundle (which only runs after the page is drawn): `tsc -p tsconfig.boot.json` compiles it
// on its own into out-tsc/boot, copied next to index.html (see angular.json). No import or export: it stays a script.
(() => {
    let theme: string | null = null;
    try {
        theme = localStorage.getItem('librarion.theme');
    } catch {
        // Storage unavailable (private mode): follow the device.
    }
    if (theme !== 'light' && theme !== 'dark')
        theme = matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    document.documentElement.dataset['theme'] = theme;
})();
