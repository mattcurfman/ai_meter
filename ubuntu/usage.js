// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Soup from 'gi://Soup?version=3.0';

export const PROVIDERS = [
    {name: 'Claude', initial: 'C', dashboard: 'https://claude.ai/settings/usage'},
    {name: 'Codex', initial: 'X', dashboard: 'https://chatgpt.com/codex/settings/usage'},
];

export function peak(usage) {
    return usage?.windows.length ? Math.max(...usage.windows.map(window => window.percent)) : null;
}

export function relative(timestamp, now = Date.now()) {
    const minutes = Math.floor((timestamp - now) / 60000);
    if (minutes < 1)
        return 'now';
    if (minutes < 60)
        return `in ${minutes} min`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24)
        return `in ${hours} hr${minutes % 60 ? ` ${minutes % 60} min` : ''}`;
    const days = Math.round(hours / 24);
    return `in ${days} day${days === 1 ? '' : 's'}`;
}

function percent(value) {
    if (typeof value !== 'number' || !Number.isFinite(value))
        throw new Error('Usage API returned an invalid percentage.');
    return Math.min(100, Math.max(0, value));
}

export function parseClaude(body, plan) {
    const titles = {
        five_hour: 'Session (5 hr)', seven_day: 'Weekly',
        seven_day_opus: 'Weekly · Opus', seven_day_sonnet: 'Weekly · Sonnet',
    };
    return {
        plan,
        windows: Object.entries(titles).filter(([key]) => body[key]).map(([key, title]) => ({
            title, percent: percent(body[key].utilization),
            resetsAt: Date.parse(body[key].resets_at) || null,
        })),
    };
}

export function parseCodex(limits) {
    return {
        plan: limits.planType,
        windows: [limits.primary, limits.secondary].filter(Boolean).map(window => {
            const minutes = window.windowDurationMins;
            let title = 'Limit';
            if (minutes === 10080)
                title = 'Weekly';
            else if (minutes === 1440)
                title = 'Daily';
            else if (minutes != null)
                title = minutes < 1440 ? `Session (${minutes / 60} hr)` : `${minutes / 1440}-day`;
            return {
                title, percent: percent(window.usedPercent),
                resetsAt: window.resetsAt ? window.resetsAt * 1000 : null,
            };
        }),
    };
}

export async function fetchClaude(session, cancellable) {
    const directory = GLib.getenv('CLAUDE_CONFIG_DIR') || GLib.build_filenamev([GLib.get_home_dir(), '.claude']);
    const file = Gio.File.new_for_path(GLib.build_filenamev([directory, '.credentials.json']));
    const contents = await new Promise((resolve, reject) => {
        file.load_contents_async(cancellable, (source, result) => {
            try {
                resolve(source.load_contents_finish(result)[1]);
            } catch (error) {
                reject(error);
            }
        });
    }).catch(error => {
        if (error.matches?.(Gio.IOErrorEnum, Gio.IOErrorEnum.NOT_FOUND))
            throw new Error('Claude credentials not found. Check CLAUDE_CONFIG_DIR and the CLI account.');
        throw error;
    });
    let oauth;
    try {
        oauth = JSON.parse(new TextDecoder().decode(contents)).claudeAiOauth;
    } catch {
        throw new Error('Cannot read the Claude credential file.');
    }
    if (!oauth?.accessToken)
        throw new Error('Claude usage token is empty. Check the CLI account and CLAUDE_CONFIG_DIR.');

    const message = Soup.Message.new('GET', 'https://api.anthropic.com/api/oauth/usage');
    message.request_headers.append('Authorization', `Bearer ${oauth.accessToken}`);
    message.request_headers.append('anthropic-beta', 'oauth-2025-04-20');
    const bytes = await new Promise((resolve, reject) => {
        session.send_and_read_async(message, GLib.PRIORITY_DEFAULT, cancellable, (source, result) => {
            try {
                resolve(source.send_and_read_finish(result));
            } catch (error) {
                reject(error);
            }
        });
    });
    if (message.status_code === 401)
        throw new Error('Claude usage API rejected the saved token. Open Claude Code to refresh it.');
    if (message.status_code === 403)
        throw new Error('Claude usage API denied access (HTTP 403). CLI access can still work.');
    if (message.status_code !== 200)
        throw new Error(`Claude usage API returned HTTP ${message.status_code}.`);
    return parseClaude(JSON.parse(new TextDecoder().decode(bytes.get_data())), oauth.subscriptionType);
}

function readLine(stream, cancellable) {
    return new Promise((resolve, reject) => {
        stream.read_line_async(GLib.PRIORITY_DEFAULT, cancellable, (source, result) => {
            try {
                const [line] = source.read_line_finish_utf8(result);
                if (line === null)
                    throw new Error('Codex app-server closed before it returned usage.');
                resolve(line);
            } catch (error) {
                reject(error);
            }
        });
    });
}

export async function fetchCodex(parentCancellable) {
    const executable = GLib.find_program_in_path('codex') ||
        GLib.build_filenamev([GLib.get_home_dir(), '.local', 'bin', 'codex']);
    if (!GLib.file_test(executable, GLib.FileTest.IS_EXECUTABLE))
        throw new Error('Codex CLI not found. Install Codex and sign in.');
    const cancellable = new Gio.Cancellable();
    const connection = parentCancellable.connect(() => cancellable.cancel());
    let process;
    let timedOut = false;
    const timer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 45, () => {
        timedOut = true;
        cancellable.cancel();
        return GLib.SOURCE_REMOVE;
    });
    try {
        if (cancellable.is_cancelled())
            throw new Error('Usage request cancelled.');
        process = Gio.Subprocess.new([executable, 'app-server'],
            Gio.SubprocessFlags.STDIN_PIPE | Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
        const input = new Gio.DataInputStream({base_stream: process.get_stdout_pipe()});
        const output = process.get_stdin_pipe();
        const send = message => new Promise((resolve, reject) => {
            output.write_all_async(new TextEncoder().encode(`${JSON.stringify(message)}\n`),
                GLib.PRIORITY_DEFAULT, cancellable, (source, result) => {
                    try {
                        source.write_all_finish(result);
                        resolve();
                    } catch (error) {
                        reject(error);
                    }
                });
        });
        const request = async (id, method, params) => {
            await send({id, method, params});
            while (true) {
                const message = JSON.parse(await readLine(input, cancellable));
                if (message.id !== id)
                    continue;
                if (message.error)
                    throw new Error(`Codex: ${message.error.message}`);
                return message.result;
            }
        };
        await request(1, 'initialize', {clientInfo: {name: 'ai_meter', title: 'AI Meter', version: '1.0'}});
        await send({method: 'initialized'});
        const {account} = await request(2, 'account/read', {refreshToken: false});
        if (!account)
            throw new Error('Codex CLI has no account. Run codex login.');
        if (account.type === 'apiKey')
            throw new Error('Codex uses an API key, which has no plan usage limits.');
        const result = await request(3, 'account/rateLimits/read', {excludeResetCreditDetails: true});
        return parseCodex(result.rateLimits);
    } catch (error) {
        if (timedOut)
            throw new Error('Codex usage request timed out.');
        throw error;
    } finally {
        if (!timedOut)
            GLib.source_remove(timer);
        if (connection)
            parentCancellable.disconnect(connection);
        if (process) {
            process.get_stdin_pipe().close(null);
            let shutdownExpired = false;
            const shutdownTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 5, () => {
                shutdownExpired = true;
                process.force_exit();
                return GLib.SOURCE_REMOVE;
            });
            await new Promise(resolve => process.wait_async(null, (source, result) => {
                try {
                    source.wait_finish(result);
                } finally {
                    resolve();
                }
            }));
            if (!shutdownExpired)
                GLib.source_remove(shutdownTimer);
            process.get_stdout_pipe().close(null);
        }
    }
}
