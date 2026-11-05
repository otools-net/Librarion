import { TestBed } from '@angular/core/testing';
import { ICON_NAMES, IconSprite } from './icon';

describe('IconSprite', () => {
    it('has a symbol for every icon name, and none without a name', async () => {
        const fixture = TestBed.createComponent(IconSprite);
        fixture.detectChanges();
        await fixture.whenStable();
        const ids = [...(fixture.nativeElement as HTMLElement).querySelectorAll('symbol')].map(symbol => symbol.id);

        expect([...ids].sort()).toEqual(ICON_NAMES.map(name => `icon-${name}`).sort());
        expect(new Set(ids).size).toBe(ids.length);
    });

    it('draws every symbol on the 24 × 24 grid', async () => {
        const fixture = TestBed.createComponent(IconSprite);
        fixture.detectChanges();
        await fixture.whenStable();
        const boxes = [...(fixture.nativeElement as HTMLElement).querySelectorAll('symbol')].map(symbol => symbol.getAttribute('viewBox'));

        expect(boxes.every(box => box === '0 0 24 24')).toBe(true);
    });
});
