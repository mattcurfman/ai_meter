import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Soup from 'gi://Soup?version=3.0';
import {fetchClaude, fetchCodex, parseClaude, parseCodex, peak, relative} from '../usage.js';

function equal(actual, expected) {
    if (JSON.stringify(actual) !== JSON.stringify(expected))
        throw new Error(`Expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
}
async function rejects(action, text) {
    try {
        await action();
    } catch (error) {
        if (error.message.includes(text))
            return;
        throw error;
    }
    throw new Error(`Expected error: ${text}`);
}

const claude = parseClaude({five_hour: {utilization: 8, resets_at: '2026-10-15T12:00:00Z'}, seven_day: {utilization: 25}}, 'max');
equal(peak(claude), 25);
equal(claude.windows[0].resetsAt, Date.parse('2026-10-15T12:00:00Z'));
equal(peak(parseClaude({}, 'max')), null);
equal(parseCodex({primary: {usedPercent: 105, windowDurationMins: 300}}).windows[0],
    {title: 'Session (5 hr)', percent: 100, resetsAt: null});
equal(relative(3600000, 0), 'in 1 hr');
await rejects(() => parseClaude({five_hour: {utilization: 'bad'}}), 'invalid percentage');
print('PASS: provider usage and reset times');

const temporary = GLib.dir_make_tmp('ai-meter-test-XXXXXX');
const script = Gio.File.new_for_uri(import.meta.url).get_parent().get_child('fake-codex.js').get_path();
const executable = `${temporary}/codex`;
GLib.file_set_contents(executable, `#!/bin/sh\nexec /usr/bin/gjs -m ${GLib.shell_quote(script)}\n`);
Gio.File.new_for_path(executable).set_attribute_uint32('unix::mode', 0o700, Gio.FileQueryInfoFlags.NONE, null);
GLib.setenv('PATH', temporary, true);
GLib.setenv('CLAUDE_CONFIG_DIR', temporary, true);
const session = new Soup.Session({timeout: 1});
try {
    GLib.file_set_contents(`${temporary}/.credentials.json`, JSON.stringify({claudeAiOauth: {accessToken: ''}}));
    await rejects(() => fetchClaude(session, new Gio.Cancellable()), 'usage token is empty');
    print('PASS: empty Claude token rejected before network access');
    const usage = await fetchCodex(new Gio.Cancellable());
    equal(usage.plan, 'pro');
    equal(usage.windows[0], {title: 'Weekly', percent: 21, resetsAt: 1792542244000});
    print('PASS: Codex handshake, refresh, notifications, and usage');
    for (const [mode, message] of [['signed-out', 'no account'], ['api-key', 'API key']]) {
        GLib.setenv('AI_METER_TEST_CASE', mode, true);
        await rejects(() => fetchCodex(new Gio.Cancellable()), message);
    }
    print('PASS: Codex signed-out and API-key accounts');
    GLib.setenv('AI_METER_TEST_CASE', 'cancel', true);
    const cancellable = new Gio.Cancellable();
    GLib.timeout_add(GLib.PRIORITY_DEFAULT, 100, () => {
        cancellable.cancel();
        return GLib.SOURCE_REMOVE;
    });
    await rejects(() => fetchCodex(cancellable), 'cancel');
    print('PASS: cancellation stops a pending Codex request');
} finally {
    session.abort();
    for (const name of ['codex', '.credentials.json'])
        Gio.File.new_for_path(`${temporary}/${name}`).delete(null);
    Gio.File.new_for_path(temporary).delete(null);
}
