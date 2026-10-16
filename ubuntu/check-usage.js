// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import Gio from 'gi://Gio';
import Soup from 'gi://Soup?version=3.0';
import System from 'system';
import {fetchClaude, fetchCodex} from './usage.js';

const session = new Soup.Session({timeout: 20});
const cancellable = new Gio.Cancellable();
let failed = false;
for (const [name, fetch] of [
    ['Claude', () => fetchClaude(session, cancellable)],
    ['Codex', () => fetchCodex(cancellable)],
]) {
    try {
        print(`${name}: ${JSON.stringify(await fetch())}`);
    } catch (error) {
        failed = true;
        printerr(`${name}: ${error.message}`);
    }
}
session.abort();
System.exit(failed ? 1 : 0);
