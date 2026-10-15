import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const input = new Gio.DataInputStream({base_stream: new Gio.UnixInputStream({fd: 0, close_fd: false})});
function read(method) {
    const message = JSON.parse(input.read_line_utf8(null)[0]);
    if (message.method !== method)
        throw new Error(`Expected ${method}`);
    return message;
}
function reply(message, result) {
    print(JSON.stringify({id: message.id, result}));
}
reply(read('initialize'), {});
read('initialized');
const account = read('account/read');
if (!account.params.refreshToken)
    throw new Error('Token refresh must be requested.');
const mode = GLib.getenv('AI_METER_TEST_CASE');
if (mode === 'cancel')
    new GLib.MainLoop(null, false).run();
reply(account, {account: mode === 'signed-out' ? null : {type: mode === 'api-key' ? 'apiKey' : 'chatgpt'}});
const limits = read('account/rateLimits/read');
print(JSON.stringify({method: 'account/updated', params: {}}));
reply(limits, {rateLimits: {
    planType: 'pro',
    primary: {usedPercent: 21, windowDurationMins: 10080, resetsAt: 1792542244},
    secondary: null,
}});
