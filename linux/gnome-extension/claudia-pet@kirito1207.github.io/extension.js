// claudia-pet 的 GNOME Shell 扩展：只负责「看」Claude 桌面端的窗口，把看到的告诉桌宠。
//
// Wayland 下普通程序读不到别的窗口的位置，桌宠（XWayland 里的一个窗口）自己找不到 Claude；GNOME Shell 是窗口管理器，看得到。
// 这里盯着 Claude 的主窗口：位置、大小、最小化、在不在当前工作区，以及叠在它上面的那些窗口的范围，
// 一有变化就在会话总线上广播 io.github.kirito1207.ClaudiaPet.StateChanged(JSON)。桌宠收到后自己挪、自己藏。
// 不改 Claude、不动任何窗口、不拦输入。
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const BUS_NAME = 'io.github.kirito1207.ClaudiaPet';
const OBJECT_PATH = '/io/github/kirito1207/ClaudiaPet';
const PROTOCOL = 1;
const IFACE_XML = `<node>
  <interface name="io.github.kirito1207.ClaudiaPet">
    <method name="GetState"><arg type="s" direction="out" name="state"/></method>
    <signal name="StateChanged"><arg type="s" name="state"/></signal>
    <property name="Protocol" type="u" access="read"/>
  </interface>
</node>`;

// Claude 桌面端：.desktop 叫 com.anthropic.Claude.desktop，窗口类名 com.anthropic.Claude
const CLAUDE_APP_IDS = ['com.anthropic.Claude.desktop', 'claude-desktop.desktop'];
const CLAUDE_CLASSES = ['com.anthropic.Claude', 'claude-desktop', 'Claude'];
const CLAUDE_EXES = ['claude-desktop'];   // 进程的可执行文件名：类名对不上时的兜底
const PET_CLASS = 'claudia-pet';
// 会把 Claude 盖住的窗口类型（菜单、提示、通知这些一闪而过的不算）
const COVER_TYPES = new Set([
    Meta.WindowType.NORMAL, Meta.WindowType.DIALOG, Meta.WindowType.MODAL_DIALOG,
    Meta.WindowType.UTILITY, Meta.WindowType.TOOLBAR, Meta.WindowType.SPLASHSCREEN,
]);

function rectOf(r) {
    return [r.x, r.y, r.width, r.height];
}

export default class ClaudiaPetExtension extends Extension {
    enable() {
        this._handlers = [];           // [对象, 信号 id]：关掉扩展时一个个断开
        this._windowHandlers = new Map();   // MetaWindow → [信号 id]
        this._last = '';
        this._pending = 0;

        this._tracker = Shell.WindowTracker.get_default();
        this._dbus = Gio.DBusExportedObject.wrapJSObject(IFACE_XML, this);
        this._dbus.export(Gio.DBus.session, OBJECT_PATH);
        this._nameId = Gio.bus_own_name(Gio.BusType.SESSION, BUS_NAME, Gio.BusNameOwnerFlags.NONE, null, null, null);

        const display = global.display;
        this._connect(display, 'window-created', (_d, w) => { this._watchWindow(w); this._queue(); });
        this._connect(display, 'restacked', () => this._queue());
        this._connect(display, 'notify::focus-window', () => this._queue());
        this._connect(display, 'in-fullscreen-changed', () => this._queue());
        this._connect(global.workspace_manager, 'active-workspace-changed', () => this._queue());
        for (const actor of global.get_window_actors())
            this._watchWindow(actor.meta_window);
        this._emit();
    }

    disable() {
        if (this._pending) {
            GLib.source_remove(this._pending);
            this._pending = 0;
        }
        for (const [w, ids] of this._windowHandlers)
            for (const id of ids) w.disconnect(id);
        this._windowHandlers.clear();
        for (const [obj, id] of this._handlers) obj.disconnect(id);
        this._handlers = [];
        if (this._nameId) {
            Gio.bus_unown_name(this._nameId);
            this._nameId = 0;
        }
        this._dbus?.unexport();
        this._dbus = null;
        this._exeCache = null;
        this._tracker = null;
    }

    // ---- D-Bus ----
    GetState() {
        return this._state();
    }

    get Protocol() {
        return PROTOCOL;
    }

    // ---- 盯窗口 ----
    _connect(obj, signal, fn) {
        this._handlers.push([obj, obj.connect(signal, fn)]);
    }

    _watchWindow(w) {
        if (!w || this._windowHandlers.has(w))
            return;
        // Claude 的位置在拖动时每一帧都在变：马上报，不等空闲（桌宠跟得越紧越好）。别的窗口攒一下再报
        const now = () => (this._isClaude(w) ? this._emit() : this._queue());
        const ids = [
            w.connect('position-changed', now),
            w.connect('size-changed', now),
            w.connect('notify::minimized', () => this._queue()),
            w.connect('workspace-changed', () => this._queue()),
            w.connect('notify::fullscreen', () => this._queue()),
            w.connect('unmanaged', () => {
                const list = this._windowHandlers.get(w);
                if (list) for (const id of list) w.disconnect(id);
                this._windowHandlers.delete(w);
                this._queue();
            }),
        ];
        this._windowHandlers.set(w, ids);
    }

    _isClaude(w) {
        if (!w || w.is_override_redirect() || w.get_window_type() !== Meta.WindowType.NORMAL)
            return false;
        const app = this._tracker?.get_window_app(w);
        if (app && CLAUDE_APP_IDS.includes(app.get_id()))
            return true;
        if (CLAUDE_CLASSES.includes(w.get_wm_class()) || CLAUDE_CLASSES.includes(w.get_gtk_application_id()))
            return true;
        return this._claudeExe(w.get_pid());
    }

    _claudeExe(pid) {
        if (!(pid > 0))
            return false;
        this._exeCache ??= new Map();
        if (!this._exeCache.has(pid)) {
            let exe = '';
            try {
                exe = GLib.file_read_link(`/proc/${pid}/exe`);
            } catch (_e) {}
            this._exeCache.set(pid, CLAUDE_EXES.includes(GLib.path_get_basename(exe)));
        }
        return this._exeCache.get(pid);
    }

    _isPet(w) {
        return w.get_wm_class() === PET_CLASS || w.get_wm_class_instance() === PET_CLASS;
    }

    // ---- 报告 ----
    _queue() {
        if (this._pending)
            return;
        this._pending = GLib.idle_add(GLib.PRIORITY_HIGH_IDLE, () => {
            this._pending = 0;
            this._emit();
            return GLib.SOURCE_REMOVE;
        });
    }

    _emit() {
        if (!this._dbus)
            return;
        const json = this._state();
        if (json === this._last)
            return;
        this._last = json;
        this._dbus.emit_signal('StateChanged', new GLib.Variant('(s)', [json]));
    }

    _state() {
        const ws = global.workspace_manager.get_active_workspace();
        // 当前工作区里（包括所有工作区都显示的）按层叠顺序排好，最下面的在前
        const windows = global.display.sort_windows_by_stacking(
            global.get_window_actors().map(a => a.meta_window).filter(w => w && w.located_on_workspace(ws)));
        // Claude 主窗口：没最小化的里面挑最大的；都最小化了就报最大的那个（桌宠据此知道 Claude 还开着、只是收起来了）
        const claudes = windows.filter(w => this._isClaude(w));
        const all = global.get_window_actors().map(a => a.meta_window).filter(w => this._isClaude(w));
        const area = w => { const r = w.get_frame_rect(); return r.width * r.height; };
        const pick = list => list.reduce((a, b) => (area(b) > area(a) ? b : a), list[0]);
        const shown = claudes.filter(w => !w.minimized);
        const main = shown.length ? pick(shown) : all.length ? pick(all) : null;
        const state = {protocol: PROTOCOL, found: !!main};
        // 前台窗口全屏时报它的范围（桌面模式「全屏时让开」用；Wayland 原生窗口桌宠那边用 X11 看不到）
        const fw = global.display.focus_window;
        state.focusFullscreen = fw && fw.is_fullscreen() && !fw.minimized && !this._isPet(fw) ? rectOf(fw.get_frame_rect()) : null;
        if (main) {
            const onWorkspace = main.located_on_workspace(ws);
            state.rect = rectOf(main.get_frame_rect());
            state.minimized = main.minimized;
            state.onWorkspace = onWorkspace;
            state.visible = onWorkspace && !main.minimized;
            state.fullscreen = main.is_fullscreen();
            state.focused = global.display.focus_window === main;
            state.scale = global.display.get_monitor_scale(main.get_monitor());
            // 叠在 Claude 上面的窗口（桌宠和一闪而过的菜单、提示不算）：桌宠落在这些范围里就藏起来
            const covers = [];
            const i = windows.indexOf(main);
            if (i >= 0) {
                for (const w of windows.slice(i + 1)) {
                    if (w.minimized || w.is_override_redirect() || this._isPet(w) || !COVER_TYPES.has(w.get_window_type()))
                        continue;
                    if (!w.get_compositor_private()?.visible)
                        continue;
                    covers.push(rectOf(w.get_frame_rect()));
                }
            }
            state.covers = covers;
        }
        return JSON.stringify(state);
    }
}
