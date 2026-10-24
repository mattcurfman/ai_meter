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

import {PROVIDERS, RefreshSchedule, fetchClaude, fetchCodex, peak, relative} from './usage.js';

function label(text, styleClass = '') {
    return new St.Label({
        text, style_class: styleClass, y_align: Clutter.ActorAlign.CENTER,
        opacity: styleClass === 'ai-meter-muted' ? 180 : 255,
    });
}

function beerGlass(percent, monochrome = false) {
    const area = new St.DrawingArea({
        style_class: monochrome ? 'ai-meter-panel-glass' : 'ai-meter-glass',
        y_align: Clutter.ActorAlign.CENTER,
    });
    area.connect('repaint', actor => {
        const [width, height] = actor.get_surface_size();
        const context = actor.get_context();
        const glass = () => {
            context.newSubPath();
            context.moveTo(1, 1);
            context.lineTo(width - 1, 1);
            context.lineTo(width * 0.85 - 1, height - 1);
            context.lineTo(width * 0.15 + 1, height - 1);
            context.closePath();
        };
        const color = actor.get_theme_node().get_foreground_color();
        const foreground = [color.red / 255, color.green / 255, color.blue / 255];
        const fraction = Math.min(1, Math.max(0, (100 - percent) / 100));
        const fillHeight = (height - 2) * fraction;
        const fillTop = height - 1 - fillHeight;
        context.save();
        glass();
        context.clip();
        context.setSourceRGBA(...foreground, 0.04);
        context.paint();
        if (fraction > 0) {
            context.setSourceRGB(...(monochrome ? foreground : [0.92, 0.57, 0.08]));
            context.rectangle(0, fillTop, width, fillHeight);
            context.fill();
            if (!monochrome) {
                context.setSourceRGB(1, 0.94, 0.76);
                context.rectangle(0, fillTop, width, Math.min(5, fillHeight));
                context.fill();
            }
        }
        context.restore();
        glass();
        context.setSourceRGBA(...foreground, 0.6);
        context.setLineWidth(1.2);
        context.stroke();
        context.$dispose();
    });
    return area;
}

export default class AIMeterExtension extends Extension {
    enable() {
        this._cancellable = new Gio.Cancellable();
        this._session = new Soup.Session({timeout: 20});
        this._providers = PROVIDERS.map(provider => ({
            ...provider, usage: null, error: null, schedule: new RefreshSchedule(),
        }));
        this._button = new PanelMenu.Button(0.0, 'AI Meter');
        this._summary = new St.BoxLayout({style_class: 'ai-meter-summary'});
        this._button.add_child(this._summary);
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._button.menu.connect('open-state-changed', (_menu, open) => {
            if (open)
                this._render();
        });
        this._render();
        this._refresh();
        this._refreshTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 60, () => {
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
        const due = this._providers.filter(provider => provider.schedule.isDue());
        if (!due.length)
            return;
        this._busy = true;
        const cancellable = this._cancellable;
        const session = this._session;
        await Promise.all(due.map(async provider => {
            try {
                const usage = await (provider.name === 'Claude' ? fetchClaude(session, cancellable) : fetchCodex(cancellable));
                if (!cancellable.is_cancelled()) {
                    provider.usage = usage;
                    provider.error = null;
                    provider.schedule.succeeded();
                }
            } catch (error) {
                if (!cancellable.is_cancelled()) {
                    provider.error = error.message;
                    provider.schedule.failed(error.retryAfterSeconds);
                }
            }
        }));
        if (cancellable.is_cancelled())
            return;
        this._busy = false;
        this._updated = new Date();
        this._render();
    }

    _render() {
        this._summary.destroy_all_children();
        const descriptions = [];
        for (const provider of this._providers) {
            const value = peak(provider.usage);
            this._summary.add_child(label(provider.initial));
            if (value !== null)
                this._summary.add_child(beerGlass(value, true));
            this._summary.add_child(label(`${value === null ? '–' : `${Math.round(100 - value)}%`}${provider.error ? ' !' : ''}`));
            descriptions.push(`${provider.name} ${value === null ? 'usage unavailable' : `${Math.round(100 - value)}% remaining`}${provider.error ? ', check failed; saved usage if available' : ''}`);
        }
        this._button.accessible_name = descriptions.join(', ');
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
                const row = new St.BoxLayout({style_class: 'ai-meter-window'});
                row.add_child(beerGlass(window.percent));
                const details = new St.BoxLayout({vertical: true, style_class: 'ai-meter-window-details', x_expand: true});
                const heading = new St.BoxLayout();
                const title = label(window.title);
                title.x_expand = true;
                heading.add_child(title);
                heading.add_child(label(`${Math.round(100 - window.percent)}% left`, 'ai-meter-percent'));
                details.add_child(heading);
                if (window.resetsAt)
                    details.add_child(label(`Resets ${relative(window.resetsAt)}`, 'ai-meter-muted'));
                row.add_child(details);
                section.add_child(row);
            }
            if (!provider.error && peak(provider.usage) === 100) {
                const cutoff = new St.BoxLayout({style_class: 'ai-meter-cutoff'});
                const bartender = label('🤵', 'ai-meter-bartender');
                bartender.accessible_name = 'Bartender';
                cutoff.add_child(bartender);
                const message = new St.BoxLayout({vertical: true, x_expand: true});
                message.add_child(label("You've been cut off", 'ai-meter-provider'));
                const detail = label('A usage limit is empty. Check its reset time.', 'ai-meter-muted');
                detail.clutter_text.line_wrap = true;
                detail.clutter_text.ellipsize = 0;
                message.add_child(detail);
                cutoff.add_child(message);
                section.add_child(cutoff);
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
        this._summary = null;
        this._session = null;
        this._cancellable = null;
        this._providers = null;
        this._updated = null;
        this._busy = false;
    }
}
