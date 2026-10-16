// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import Clutter from 'gi://Clutter';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Soup from 'gi://Soup?version=3.0';
import St from 'gi://St';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

import {PROVIDERS, fetchClaude, fetchCodex, peak, relative} from './usage.js';

function label(text, styleClass = '') {
    return new St.Label({
        text, style_class: styleClass, y_align: Clutter.ActorAlign.CENTER,
        opacity: styleClass === 'ai-meter-muted' ? 180 : 255,
    });
}

function meter(percent) {
    const area = new St.DrawingArea({style_class: 'ai-meter-bar', x_expand: true});
    area.connect('repaint', actor => {
        const [width, height] = actor.get_surface_size();
        const context = actor.get_context();
        const capsule = length => {
            const radius = Math.min(height, length) / 2;
            context.newSubPath();
            context.arc(length - radius, height / 2, radius, -Math.PI / 2, Math.PI / 2);
            context.arc(radius, height / 2, radius, Math.PI / 2, 3 * Math.PI / 2);
            context.closePath();
            context.fill();
        };
        const color = actor.get_theme_node().get_foreground_color();
        context.setSourceRGBA(color.red / 255, color.green / 255, color.blue / 255, 0.12);
        capsule(width);
        if (percent > 0) {
            const tint = percent >= 90 ? [0.90, 0.25, 0.27] : percent >= 70 ? [0.96, 0.60, 0.16] : [0.21, 0.52, 0.95];
            context.setSourceRGB(...tint);
            capsule(Math.min(width, Math.max(height, width * percent / 100)));
        }
        context.$dispose();
    });
    return area;
}

export default class AIMeterExtension extends Extension {
    enable() {
        this._cancellable = new Gio.Cancellable();
        this._session = new Soup.Session({timeout: 20});
        this._providers = PROVIDERS.map(provider => ({...provider, usage: null, error: null}));
        this._button = new PanelMenu.Button(0.0, 'AI Meter');
        this._label = label('C –  X –');
        this._button.add_child(this._label);
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._button.menu.connect('open-state-changed', (_menu, open) => {
            if (open)
                this._render();
        });
        this._render();
        this._refresh();
        this._refreshTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 300, () => {
            this._refresh();
            return GLib.SOURCE_CONTINUE;
        });
        this._clockTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 60, () => {
            if (this._button.menu.isOpen)
                this._render();
            return GLib.SOURCE_CONTINUE;
        });
    }

    async _refresh() {
        if (this._busy)
            return;
        this._busy = true;
        const cancellable = this._cancellable;
        const session = this._session;
        await Promise.all(this._providers.map(async provider => {
            try {
                const usage = await (provider.name === 'Claude' ? fetchClaude(session, cancellable) : fetchCodex(cancellable));
                if (!cancellable.is_cancelled()) {
                    provider.usage = usage;
                    provider.error = null;
                }
            } catch (error) {
                if (!cancellable.is_cancelled())
                    provider.error = error.message;
            }
        }));
        if (cancellable.is_cancelled())
            return;
        this._busy = false;
        this._updated = new Date();
        this._render();
    }

    _render() {
        this._label.text = this._providers.map(provider => {
            const value = peak(provider.usage);
            return `${provider.initial} ${value === null ? '–' : `${Math.round(value)}%`}${provider.error ? ' !' : ''}`;
        }).join('  ');
        this._button.menu.removeAll();
        for (const [index, provider] of this._providers.entries()) {
            if (index)
                this._button.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
            const item = new PopupMenu.PopupMenuSection();
            const section = new St.BoxLayout({vertical: true, style_class: 'ai-meter-section', x_expand: true});
            const header = new St.BoxLayout({style_class: 'ai-meter-header'});
            header.add_child(label(provider.name, 'ai-meter-provider'));
            if (provider.usage?.plan)
                header.add_child(label(provider.usage.plan[0].toUpperCase() + provider.usage.plan.slice(1), 'ai-meter-plan'));
            header.add_child(new St.Widget({x_expand: true}));
            const link = new St.Button({
                style_class: 'ai-meter-link', can_focus: true,
                accessible_name: `Open ${provider.name} usage page`,
                child: new St.Icon({icon_name: 'external-link-symbolic', icon_size: 16}),
            });
            link.connect('clicked', () => {
                this._button.menu.close();
                Gio.AppInfo.launch_default_for_uri_async(provider.dashboard, global.create_app_launch_context(0, -1),
                    null, (source, result) => {
                        try {
                            Gio.AppInfo.launch_default_for_uri_finish(result);
                        } catch {
                            Main.notify('AI Meter', 'Cannot open the usage page. Check your default browser.');
                        }
                    });
            });
            header.add_child(link);
            section.add_child(header);
            if (!provider.usage && !provider.error)
                section.add_child(label('Loading…', 'ai-meter-muted'));
            for (const window of provider.usage?.windows ?? []) {
                const row = new St.BoxLayout({vertical: true, style_class: 'ai-meter-window'});
                const heading = new St.BoxLayout();
                const title = label(window.title);
                title.x_expand = true;
                heading.add_child(title);
                heading.add_child(label(`${Math.round(window.percent)}%`, 'ai-meter-percent'));
                row.add_child(heading);
                row.add_child(meter(window.percent));
                if (window.resetsAt)
                    row.add_child(label(`Resets ${relative(window.resetsAt)}`, 'ai-meter-muted'));
                section.add_child(row);
            }
            if (provider.usage && !provider.usage.windows.length)
                section.add_child(label('No usage limits reported.', 'ai-meter-muted'));
            if (provider.error) {
                const error = label(`${provider.usage ? 'Saved usage · ' : ''}${provider.error}`, 'ai-meter-error');
                error.clutter_text.line_wrap = true;
                error.clutter_text.ellipsize = 0;
                section.add_child(error);
            }
            item.box.add_child(section);
            this._button.menu.addMenuItem(item);
        }
        this._button.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        const footer = new PopupMenu.PopupMenuSection();
        footer.box.add_style_class_name('ai-meter-footer');
        footer.box.add_child(label(this._updated ? `Checked ${this._updated.toLocaleTimeString([], {hour: '2-digit', minute: '2-digit'})}` : 'Checking usage…', 'ai-meter-muted'));
        this._button.menu.addMenuItem(footer);
    }

    disable() {
        this._cancellable.cancel();
        this._session.abort();
        GLib.source_remove(this._refreshTimer);
        GLib.source_remove(this._clockTimer);
        this._button.destroy();
        this._button = null;
        this._label = null;
        this._session = null;
        this._cancellable = null;
        this._providers = null;
        this._updated = null;
        this._busy = false;
    }
}
