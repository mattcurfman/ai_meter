// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {RefreshSchedule, fetchClaude} from '../usage.js';

function equal(actual, expected) {
    if (actual !== expected)
        throw new Error(`Expected ${expected}, got ${actual}`);
}

// Inject both time and randomness: no sleeps or network requests.
for (const jitter of [0, 0.25, 0.5, 0.999999, 1]) {
    let now = 100;
    const schedule = new RefreshSchedule(() => now, () => jitter);
    equal(schedule.isDue(), true);
    for (const ceiling of [600, 1200, 2400, 3600, 3600, 3600]) {
        schedule.failed();
        const deadline = now + ceiling / 2 * (1 + jitter);
        now = deadline - 0.001;
        equal(schedule.isDue(), false);
        now = deadline;
        equal(schedule.isDue(), true);
    }
    schedule.succeeded();
    now += 299;
    equal(schedule.isDue(), false);
    now += 1;
    equal(schedule.isDue(), true);
    schedule.failed();
    now += 300 * (1 + jitter);
    equal(schedule.isDue(), true);
}
print('PASS: exponential growth, equal jitter, cap, success interval and reset');

let now = 0;
const claude = new RefreshSchedule(() => now, () => 0);
const codex = new RefreshSchedule(() => now, () => 0);
claude.failed(7200);
codex.succeeded();
now = 300;
equal(claude.isDue(), false);
equal(codex.isDue(), true);
codex.failed();
now = 600;
equal(codex.isDue(), true);
equal(claude.isDue(), false);
now = 7199;
equal(claude.isDue(), false);
now = 7200;
equal(claude.isDue(), true);
claude.succeeded();
now += 300;
equal(claude.isDue(), true);
for (const hint of [0, -10, NaN, Infinity, undefined, 1]) {
    claude.succeeded();
    claude.failed(hint);
    now += 299;
    equal(claude.isDue(), false);
    now += 1;
    equal(claude.isDue(), true);
}
print('PASS: independent providers, Retry-After beyond cap, invalid/short hints');

const temporary = GLib.dir_make_tmp('ai-meter-backoff-XXXXXX');
const previousDirectory = GLib.getenv('CLAUDE_CONFIG_DIR');
GLib.setenv('CLAUDE_CONFIG_DIR', temporary, true);
GLib.file_set_contents(`${temporary}/.credentials.json`, JSON.stringify({
    claudeAiOauth: {accessToken: 'offline-test-token'},
}));

async function responseError(header, status = 429) {
    let calls = 0;
    const session = {
        send_and_read_async(message, _priority, _cancellable, callback) {
            calls++;
            // Soup's status property is read-only; shadow it on this test message.
            Object.defineProperty(message, 'status_code', {value: status});
            if (header !== null)
                message.response_headers.append('Retry-After', header);
            callback(this, null);
        },
        send_and_read_finish() {
            return new GLib.Bytes(new TextEncoder().encode('{}'));
        },
    };
    try {
        await fetchClaude(session, new Gio.Cancellable());
    } catch (error) {
        equal(calls, 1);
        return error;
    }
    throw new Error('Expected HTTP error');
}

try {
    const seconds = await responseError('7200');
    equal(seconds.message, 'Claude usage API returned HTTP 429.');
    equal(seconds.retryAfterSeconds, 7200);
    for (const header of [null, '', 'garbage', '-1', '1.5', 'Infinity', 'Wed, 21 Oct 2015 07:28:00 GMT'])
        equal((await responseError(header)).retryAfterSeconds, 0);
    equal((await responseError(' 120 ')).retryAfterSeconds, 120);
    equal((await responseError('0')).retryAfterSeconds, 0);
    const future = new Date(Date.now() + 7200000).toUTCString();
    const before = Date.now();
    const dated = await responseError(future);
    const after = Date.now();
    const target = Date.parse(future);
    equal(dated.retryAfterSeconds >= (target - after) / 1000, true);
    equal(dated.retryAfterSeconds <= (target - before) / 1000, true);
    equal((await responseError('9000', 503)).retryAfterSeconds, 9000);
    equal((await responseError(null, 401)).message,
        'Claude usage API rejected the saved token. Open Claude Code to refresh it.');
    equal((await responseError(null, 403)).message,
        'Claude usage API denied access (HTTP 403). CLI access can still work.');
    print('PASS: mocked Claude 429 seconds/date/missing/invalid headers and HTTP errors');
} finally {
    if (previousDirectory === null)
        GLib.unsetenv('CLAUDE_CONFIG_DIR');
    else
        GLib.setenv('CLAUDE_CONFIG_DIR', previousDirectory, true);
    Gio.File.new_for_path(`${temporary}/.credentials.json`).delete(null);
    Gio.File.new_for_path(temporary).delete(null);
}
