import Gio from 'gi://Gio';
import Soup from 'gi://Soup?version=3.0';
import {fetchClaude, fetchCodex} from './usage.js';

const session = new Soup.Session({timeout: 20});
const cancellable = new Gio.Cancellable();
for (const [name, fetch] of [
    ['Claude', () => fetchClaude(session, cancellable)],
    ['Codex', () => fetchCodex(cancellable)],
]) {
    try {
        print(`${name}: ${JSON.stringify(await fetch())}`);
    } catch (error) {
        printerr(`${name}: ${error.message}`);
    }
}
session.abort();
