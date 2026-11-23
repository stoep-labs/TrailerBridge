define(['emby-input', 'emby-button', 'emby-checkbox'], function () {
    'use strict';

    // The Emby Forum thread for TrailerBridge (the footer's Emby Forum link).
    var FORUM_URL = 'https://emby.media/community/forum/46-plugins/';
    var GITHUB_REPO = 'stoep-labs/TrailerBridge';

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

    // Footer: the installed version, and a newer release on GitHub (nothing shown when GitHub can't be read).
    function loadFooter(view) {
        view.querySelector('#tbForumLink').href = FORUM_URL;
        window.ApiClient.getInstalledPlugins().then(function (plugins) {
            var me = (plugins || []).filter(function (p) { return (p.Id || '').toLowerCase() === pluginId; })[0];
            var ver = (me && me.Version) || '';
            if (!ver) return;
            view.querySelector('.tb-version').textContent = 'TrailerBridge v' + ver;
            view.querySelector('#tbFooterVersion').innerHTML = '<a href="https://github.com/' + GITHUB_REPO + '/releases/tag/v' + esc(ver) + '" target="_blank" style="color:inherit;text-decoration:none;">v' + esc(ver) + '</a>';
            fetch('https://api.github.com/repos/' + GITHUB_REPO + '/releases/latest')
                .then(function (r) { if (!r.ok) throw new Error(); return r.json(); })
                .then(function (release) {
                    var latest = (release.tag_name || '').replace(/^v/i, '');
                    if (!latest) return;
                    var a = latest.split('.').map(Number), b = ver.split('.').map(Number), newer = false;
                    for (var i = 0; i < Math.max(a.length, b.length); i++) {
                        if ((a[i] || 0) > (b[i] || 0)) { newer = true; break; }
                        if ((a[i] || 0) < (b[i] || 0)) break;
                    }
                    if (!newer) return;
                    view.querySelector('#tbFooterUpdateSep').style.display = '';
                    view.querySelector('#tbFooterUpdateInfo').innerHTML = '<a class="footer-update-link" href="' + esc(release.html_url || 'https://github.com/' + GITHUB_REPO + '/releases') + '" target="_blank">Update available: v' + esc(latest) + '</a>';
                }).catch(function () { });
        }).catch(function () { });
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
            var q = c.Quality >= 1080 ? '1080' : c.Quality >= 720 ? '720' : '360';
            view.querySelector('#tbQuality').value = q;
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
                c.Quality = parseInt(view.querySelector('#tbQuality').value, 10);
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
                window.addEventListener('resize', function () { if (view.isConnected && view.offsetParent !== null) applyTheme(view); });
            }
            loadFooter(view);
            loadConfig(view).then(function () { return loadStatus(view); });
        });
    };
});
