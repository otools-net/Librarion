import { addressName } from '../tags/tag-link';

/** The commands of a shelf's page (`/shelves/<id>/<name>`; the name only for reading, the id finds it). */
export function shelfLink(shelf: { id: number; name: string }): (string | number)[] {
    const name = addressName(shelf.name);
    return name ? ['/shelves', shelf.id, name] : ['/shelves', shelf.id];
}
