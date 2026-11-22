define(['emby-input', 'emby-button', 'emby-checkbox'], function () {
    'use strict';

    var pluginId = '7c1d4b9e-2f63-4a85-b0e7-91d3c5a6f248';

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function api(path, method) {
        return fetch(window.ApiClient.getUrl(path), {
            method: method || 'GET',
            headers: { 'X-MediaBrowser-Token': window.ApiClient.accessToken() }
        }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }

    function applyTheme(view) {
        var bg = null;
        for (var el = view; el && el.nodeType === 1; el = el.parentElement) {
            var c = getComputedStyle(el).backgroundColor;
            if (c && c !== 'transparent' && c !== 'rgba(0, 0, 0, 0)') { bg = c; break; }
        }
        if (!bg) bg = getComputedStyle(document.body).backgroundColor;
        var m = (bg || '').match(/\d+/g);
        view.setAttribute('data-tb-theme', m && (parseInt(m[0]) * 0.299 + parseInt(m[1]) * 0.587 + parseInt(m[2]) * 0.114) >= 128 ? 'light' : 'dark');
        var drawer = document.querySelector('.mainDrawer');
        var right = drawer ? drawer.getBoundingClientRect().right : 0;
        view.style.setProperty('--tb-footer-left', (right > 0 && right < window.innerWidth * 0.5 ? right : 0) + 'px');
    }

    function msg(view, id, text, error) {
        var el = view.querySelector('#' + id);
        el.textContent = text || '';
        el.classList.toggle('error', !!error);
    }

    function loadStatus(view) {
        return api('TrailerBridge/Status').then(function (s) {
            view.querySelector('#tbLinked').textContent = s.Linked;
            view.querySelector('#tbVersion').innerHTML = s.YtDlpVersion
                ? esc(s.YtDlpVersion) + (s.YtDlpOwn ? ' <span class="tb-pill ok">auto-updated</span>' : '')
                : '<span class="tb-pill warn">not installed yet</span>';
            view.querySelector('#tbInUse').textContent = s.YtDlpPath || '–';
            view.querySelector('#tbJs').textContent = s.JsRuntime || '–';
            if (s.LastMessage) msg(view, 'tbLinksMsg', (s.LastRun ? new Date(s.LastRun).toLocaleString() + ': ' : '') + s.LastMessage);
        }).catch(function (e) { msg(view, 'tbLinksMsg', e.message, true); });
    }

    function loadConfig(view) {
        return window.ApiClient.getPluginConfiguration(pluginId).then(function (c) {
            view.querySelector('#tbEnabled').checked = !!c.Enabled;
            view.querySelector('#tbYtDlpPath').value = c.YtDlpPath || '';
            view.querySelector('#tbLinkHost').value = c.LinkHost || '';
        });
    }

    function run(view, button, path, msgId) {
        button.disabled = true;
        msg(view, msgId, '…');
        return api(path, 'POST').then(function (r) {
            msg(view, msgId, r.Message, r.Success === false);
            return loadStatus(view);
        }).catch(function (e) {
            msg(view, msgId, e.message, true);
        }).then(function () { button.disabled = false; });
    }

    function bind(view) {
        view.querySelector('#tbCreate').addEventListener('click', function (e) {
            run(view, e.currentTarget, 'TrailerBridge/Links/Create', 'tbLinksMsg');
        });
        view.querySelector('#tbRemove').addEventListener('click', function (e) {
            run(view, e.currentTarget, 'TrailerBridge/Links/Remove', 'tbLinksMsg');
        });
        view.querySelector('#tbUpdate').addEventListener('click', function (e) {
            run(view, e.currentTarget, 'TrailerBridge/YtDlp/Update', 'tbYtMsg');
        });
        view.querySelector('#tbSave').addEventListener('click', function (e) {
            var button = e.currentTarget;
            button.disabled = true;
            window.ApiClient.getPluginConfiguration(pluginId).then(function (c) {
                c.Enabled = view.querySelector('#tbEnabled').checked;
                c.YtDlpPath = view.querySelector('#tbYtDlpPath').value.trim();
                c.LinkHost = view.querySelector('#tbLinkHost').value.trim();
                return window.ApiClient.updatePluginConfiguration(pluginId, c);
            }).then(function () {
                msg(view, 'tbSaveMsg', 'Saved.');
                return loadStatus(view);
            }).catch(function (err) {
                msg(view, 'tbSaveMsg', err.message, true);
            }).then(function () { button.disabled = false; });
        });
    }

    return function (view) {
        view.addEventListener('viewshow', function () {
            applyTheme(view);
            setTimeout(function () { applyTheme(view); }, 500);
            if (!view.dataset.tbInit) {
                view.dataset.tbInit = '1';
                bind(view);
            }
            window.ApiClient.getInstalledPlugins().then(function (plugins) {
                var me = (plugins || []).filter(function (p) { return (p.Id || '').toLowerCase() === pluginId; })[0];
                view.querySelector('#tbFooterVersion').textContent = me && me.Version ? 'v' + me.Version : '';
            }).catch(function () { });
            loadConfig(view).then(function () { return loadStatus(view); });
        });
    };
});
