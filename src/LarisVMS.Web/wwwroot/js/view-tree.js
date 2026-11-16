// The view tree on Views/Index: Private views, a folder per role, and roles' views an admin let
// this person open. Right-click (or the keyboard's menu key, Shift+F10, or a long press) for actions;
// drag a view or folder onto a folder to move it, or use Cut and Paste. Everything goes through
// /api/views (ViewTreeApi), which decides what's allowed; this only draws and asks.
//
// Moves inside one tree ask first, with "Don't ask again until I sign out" (kept on the sign-in, so
// another browser or a new sign-in asks again). Moves between trees always ask, since people may
// lose sight of the view.
window.larisvmsViewTree = (function () {
    'use strict';

    var PREF_KEY = 'viewTree.expanded';
    var LONG_PRESS_MS = 550;

    var o, treeEl, menuEl, filterEl, alertEl, dialogEl;
    var data = null;          // { trees: [...], askBeforeMove: bool }
    var expanded = null;      // Set of 'tree:<name>' and 'folder:<id>'
    var clipboard = null;     // { mode: 'copy'|'cut', kind: 'view'|'folder', id, tree, name }
    var dragging = null;      // { kind, id, tree, name }
    var menuFor = null;       // the row the menu was opened on, to return focus to

    function t(key, args) { return larisvmsI18n.t(key, args); }

    // ── Server ──────────────────────────────────────────────────────────────

    function api(method, url, body) {
        var init = { method: method, headers: { 'Accept': 'application/json' } };
        if (body !== undefined) {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }
        return fetch(url, init).then(function (resp) {
            if (resp.status === 204) return null;
            return resp.json().catch(function () { return null; }).then(function (json) {
                if (!resp.ok) throw new Error((json && json.error) || t('js.viewTree.failed'));
                return json;
            });
        });
    }

    function load() {
        return api('GET', '/api/views/tree').then(function (d) {
            data = d;
            render();
        });
    }

    function run(promise) {
        return promise.then(function (result) {
            hideAlert();
            return load().then(function () { return result; });
        }, function (err) {
            showAlert(err && err.message ? err.message : t('js.viewTree.failed'));
            throw err;
        });
    }

    function showAlert(text, info) {
        alertEl.textContent = text;
        alertEl.classList.toggle('alert-danger', !info);
        alertEl.classList.toggle('alert-info', !!info);
        alertEl.classList.remove('d-none');
    }

    function hideAlert() { alertEl.classList.add('d-none'); }

    // ── Model helpers ───────────────────────────────────────────────────────

    function rootOf(treeName) {
        return data.trees.filter(function (t) { return t.tree === treeName; })[0] || null;
    }

    function folderOf(root, id) {
        return root.folders.filter(function (f) { return f.id === id; })[0] || null;
    }

    function folderName(treeName, folderId) {
        var root = rootOf(treeName);
        if (!root) return '';
        if (!folderId) return root.name;
        var f = folderOf(root, folderId);
        return f ? f.name : root.name;
    }

    // A folder and every folder under it, so a folder can't be dropped into its own branch.
    function branchIds(root, folderId) {
        var ids = {};
        ids[folderId] = true;
        var grew = true;
        while (grew) {
            grew = false;
            root.folders.forEach(function (f) {
                if (f.parentId && ids[f.parentId] && !ids[f.id]) { ids[f.id] = true; grew = true; }
            });
        }
        return ids;
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    function loadExpanded() {
        var raw = window.larisvmsPreferences ? window.larisvmsPreferences.get(PREF_KEY, null) : null;
        var list = null;
        try { list = raw ? JSON.parse(raw) : null; } catch (e) { list = null; }
        expanded = new Set(Array.isArray(list) ? list : []);
        // First visit: every tree open, folders closed.
        if (!Array.isArray(list)) data.trees.forEach(function (t) { expanded.add('tree:' + t.tree); });
    }

    function saveExpanded() {
        if (window.larisvmsPreferences) window.larisvmsPreferences.set(PREF_KEY, JSON.stringify(Array.from(expanded)));
    }

    function filterText() { return (filterEl.value || '').trim().toLowerCase(); }

    function render() {
        if (!expanded) loadExpanded();
        var focusedKey = document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.key : null;
        treeEl.textContent = '';
        var term = filterText();
        var list = document.createElement('ul');
        list.className = 'vt-list vt-top';
        data.trees.forEach(function (root) {
            var item = renderRoot(root, term);
            if (item) list.appendChild(item);
        });
        if (!list.children.length) {
            var empty = document.createElement('p');
            empty.className = 'text-muted small';
            empty.textContent = term ? t('js.viewTree.noMatches') : t('js.viewTree.empty');
            treeEl.appendChild(empty);
            return;
        }
        treeEl.appendChild(list);
        var rows = visibleRows();
        if (rows.length) {
            var again = focusedKey && treeEl.querySelector('.vt-row[data-key="' + cssEscape(focusedKey) + '"]');
            (again || rows[0]).tabIndex = 0;
            if (again) again.focus();
        }
    }

    function cssEscape(s) { return window.CSS && CSS.escape ? CSS.escape(s) : s.replace(/"/g, '\\"'); }

    function makeRow(kind, key, label, opts) {
        var row = document.createElement('div');
        row.className = 'vt-row';
        row.setAttribute('role', 'treeitem');
        row.tabIndex = -1;
        row.dataset.kind = kind;
        row.dataset.key = key;
        row.dataset.tree = opts.tree;
        if (opts.id) row.dataset.id = opts.id;
        if (opts.canEdit) row.dataset.canEdit = 'true';

        var caret = document.createElement('span');
        caret.className = 'vt-caret';
        caret.setAttribute('aria-hidden', 'true');
        if (opts.expandable) caret.textContent = opts.open ? '▾' : '▸';
        row.appendChild(caret);
        if (opts.expandable) row.setAttribute('aria-expanded', opts.open ? 'true' : 'false');

        var icon = document.createElement('span');
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = kind === 'view' ? '▦' : (kind === 'root' ? (opts.isPrivate ? '🔒' : '👥') : '📁');
        row.appendChild(icon);

        if (kind === 'view') {
            var link = document.createElement('a');
            link.href = '/Views/Play/' + encodeURIComponent(opts.id);
            link.textContent = label;
            link.tabIndex = -1;
            row.appendChild(link);
        } else {
            var name = document.createElement('span');
            name.className = 'vt-name';
            name.textContent = label;
            row.appendChild(name);
        }

        if (opts.note) {
            var note = document.createElement('span');
            note.className = 'text-muted small';
            note.textContent = opts.note;
            row.appendChild(note);
        }
        if (opts.badge) {
            var badge = document.createElement('span');
            badge.className = 'badge text-bg-light border';
            badge.textContent = opts.badge;
            row.appendChild(badge);
        }

        if (opts.draggable) row.draggable = true;
        return row;
    }

    function renderRoot(root, term) {
        var key = 'tree:' + root.tree;
        var open = term ? true : expanded.has(key);
        var children = renderLevel(root, null, term);
        if (term && !children) return null;

        var li = document.createElement('li');
        li.className = 'vt-root';
        li.appendChild(makeRow('root', key, root.name, {
            tree: root.tree, canEdit: root.canEdit, expandable: true, open: open,
            isPrivate: root.kind === 'private', badge: root.kind === 'granted' ? t('js.viewTree.openOnly') : null
        }));
        if (open) li.appendChild(children || emptyLevel());
        return li;
    }

    function emptyLevel() {
        var ul = document.createElement('ul');
        ul.className = 'vt-list';
        ul.setAttribute('role', 'group');
        var li = document.createElement('li');
        li.className = 'text-muted small vt-empty';
        li.textContent = t('js.viewTree.emptyFolder');
        ul.appendChild(li);
        return ul;
    }

    // One level of a tree: its folders (each with what's inside), then its views. Null when
    // filtering and nothing in it matches.
    function renderLevel(root, parentId, term) {
        var ul = document.createElement('ul');
        ul.className = 'vt-list';
        ul.setAttribute('role', 'group');

        root.folders.filter(function (f) { return (f.parentId || null) === parentId; }).forEach(function (f) {
            var key = 'folder:' + f.id;
            var inner = renderLevel(root, f.id, term);
            if (term && !inner) return;
            var open = term ? true : expanded.has(key);
            var li = document.createElement('li');
            li.appendChild(makeRow('folder', key, f.name, {
                tree: root.tree, id: f.id, canEdit: root.canEdit, expandable: true, open: open, draggable: root.canEdit
            }));
            if (open) li.appendChild(inner || emptyLevel());
            ul.appendChild(li);
        });

        root.views.filter(function (v) { return (v.folderId || null) === parentId; }).forEach(function (v) {
            if (term && v.name.toLowerCase().indexOf(term) === -1) return;
            var li = document.createElement('li');
            li.appendChild(makeRow('view', 'view:' + v.id, v.name, {
                tree: root.tree, id: v.id, canEdit: root.canEdit, draggable: root.canEdit,
                note: larisvmsI18n.plural('js.viewTree.cameras', v.cells)
            }));
            ul.appendChild(li);
        });

        return ul.children.length ? ul : null;
    }

    function visibleRows() { return Array.prototype.slice.call(treeEl.querySelectorAll('.vt-row')); }

    function rowInfo(row) {
        var root = rootOf(row.dataset.tree);
        var kind = row.dataset.kind;
        var id = row.dataset.id || null;
        var name = kind === 'root' ? root.name
            : kind === 'folder' ? (folderOf(root, id) || {}).name
            : (root.views.filter(function (v) { return v.id === id; })[0] || {}).name;
        return { kind: kind, id: id, tree: row.dataset.tree, root: root, name: name || '', canEdit: root.canEdit };
    }

    function toggle(row, open) {
        var key = row.dataset.key;
        var isOpen = expanded.has(key);
        if (open === undefined) open = !isOpen;
        if (open === isOpen) return;
        if (open) expanded.add(key); else expanded.delete(key);
        saveExpanded();
        render();
    }

    // ── Dialog ──────────────────────────────────────────────────────────────

    // { title, message, value (shows a text box), check (shows a checkbox with this label), ok, danger }
    // → Promise of { ok, value, checked }.
    function ask(opts) {
        var title = dialogEl.querySelector('#vtDialogTitle');
        var message = dialogEl.querySelector('#vtDialogMessage');
        var input = dialogEl.querySelector('#vtDialogInput');
        var checkWrap = dialogEl.querySelector('#vtDialogCheckWrap');
        var check = dialogEl.querySelector('#vtDialogCheck');
        var checkLabel = dialogEl.querySelector('#vtDialogCheckLabel');
        var okBtn = dialogEl.querySelector('#vtDialogOk');
        var form = dialogEl.querySelector('#vtDialogForm');

        title.textContent = opts.title;
        message.textContent = opts.message || '';
        message.classList.toggle('d-none', !opts.message);
        var hasInput = typeof opts.value === 'string';
        input.classList.toggle('d-none', !hasInput);
        input.value = hasInput ? opts.value : '';
        input.required = hasInput;
        checkWrap.classList.toggle('d-none', !opts.check);
        check.checked = false;
        checkLabel.textContent = opts.check || '';
        okBtn.textContent = opts.ok || t('js.viewTree.ok');
        okBtn.className = 'btn ' + (opts.danger ? 'btn-danger' : 'btn-primary');

        var modal = bootstrap.Modal.getOrCreateInstance(dialogEl);
        return new Promise(function (resolve) {
            var answered = false;
            function onSubmit(e) {
                e.preventDefault();
                if (hasInput && !input.value.trim()) { input.focus(); return; }
                answered = true;
                resolve({ ok: true, value: input.value.trim(), checked: check.checked });
                modal.hide();
            }
            function onShown() { (hasInput ? input : okBtn).focus(); if (hasInput) input.select(); }
            function onHidden() {
                form.removeEventListener('submit', onSubmit);
                dialogEl.removeEventListener('shown.bs.modal', onShown);
                dialogEl.removeEventListener('hidden.bs.modal', onHidden);
                if (!answered) resolve({ ok: false });
                if (menuFor && document.body.contains(menuFor)) menuFor.focus();
            }
            form.addEventListener('submit', onSubmit);
            dialogEl.addEventListener('shown.bs.modal', onShown);
            dialogEl.addEventListener('hidden.bs.modal', onHidden);
            modal.show();
        });
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    function newView(tree, folderId) {
        ask({ title: t('js.viewTree.newView'), value: t('js.viewTree.defaultViewName'), ok: t('js.viewTree.create') }).then(function (a) {
            if (!a.ok) return;
            api('POST', '/api/views', { tree: tree, folderId: folderId, name: a.value }).then(function (created) {
                window.location.href = '/Views/Editor/' + encodeURIComponent(created.id);
            }, function (err) { showAlert(err.message); });
        });
    }

    function newFolder(tree, parentId) {
        ask({ title: t('js.viewTree.newFolder'), value: t('js.viewTree.defaultFolderName'), ok: t('js.viewTree.create') }).then(function (a) {
            if (!a.ok) return;
            expanded.add(parentId ? 'folder:' + parentId : 'tree:' + tree);
            saveExpanded();
            run(api('POST', '/api/views/folders', { tree: tree, parentId: parentId, name: a.value })).catch(function () { });
        });
    }

    function rename(info) {
        ask({ title: t('js.viewTree.rename'), value: info.name, ok: t('js.viewTree.rename') }).then(function (a) {
            if (!a.ok || a.value === info.name) return;
            var url = info.kind === 'view' ? '/api/views/' : '/api/views/folders/';
            run(api('PATCH', url + encodeURIComponent(info.id), { name: a.value })).catch(function () { });
        });
    }

    function remove(info) {
        var isView = info.kind === 'view';
        ask({
            title: isView ? t('js.viewTree.deleteView') : t('js.viewTree.deleteFolder'),
            message: isView ? t('js.viewTree.deleteViewConfirm', { name: info.name }) : t('js.viewTree.deleteFolderConfirm', { name: info.name }),
            ok: t('js.viewTree.delete'), danger: true
        }).then(function (a) {
            if (!a.ok) return;
            var url = isView ? '/api/views/' : '/api/views/folders/';
            if (clipboard && clipboard.id === info.id) clipboard = null;
            run(api('DELETE', url + encodeURIComponent(info.id))).catch(function () { });
        });
    }

    function remember(info, mode) {
        clipboard = { mode: mode, kind: info.kind, id: info.id, tree: info.tree, name: info.name };
        showAlert(mode === 'cut' ? t('js.viewTree.cutHint', { name: info.name }) : t('js.viewTree.copiedHint', { name: info.name }), true);
    }

    // The target is a tree's top level (folderId null) or one of its folders.
    function paste(target) {
        if (!clipboard) return;
        var item = clipboard;
        if (item.mode === 'copy') {
            run(api('POST', '/api/views/' + encodeURIComponent(item.id) + '/copy', { tree: target.tree, folderId: target.folderId }))
                .then(function () { expandTarget(target); }, function () { });
        } else {
            move(item, target).then(function (moved) { if (moved) clipboard = null; });
        }
    }

    function expandTarget(target) {
        expanded.add(target.folderId ? 'folder:' + target.folderId : 'tree:' + target.tree);
        saveExpanded();
        render();
    }

    function currentParent(item) {
        var root = rootOf(item.tree);
        if (!root) return null;
        if (item.kind === 'view') {
            var v = root.views.filter(function (x) { return x.id === item.id; })[0];
            return v ? (v.folderId || null) : null;
        }
        var f = folderOf(root, item.id);
        return f ? (f.parentId || null) : null;
    }

    // What moving to another tree means for who sees it — always said before such a move.
    function crossTreeWarning(fromTree, toTree) {
        var from = rootOf(fromTree), to = rootOf(toTree);
        if (to.kind === 'private') return t('js.viewTree.moveToPrivateWarning');
        if (from.kind === 'private') return t('js.viewTree.moveToRoleWarning', { to: to.name });
        return t('js.viewTree.moveBetweenRolesWarning', { from: from.name, to: to.name });
    }

    // Resolves true when it moved.
    function move(item, target) {
        if (item.tree === target.tree && currentParent(item) === (target.folderId || null)) return Promise.resolve(false);
        if (item.kind === 'folder' && item.tree === target.tree && target.folderId
            && branchIds(rootOf(item.tree), item.id)[target.folderId]) {
            showAlert(t('js.viewTree.folderIntoItself'));
            return Promise.resolve(false);
        }

        var where = folderName(target.tree, target.folderId);
        var sameTree = item.tree === target.tree;
        var question = sameTree
            ? (data.askBeforeMove ? ask({ title: t('js.viewTree.moveTitle', { name: item.name }), message: t('js.viewTree.moveConfirm', { name: item.name, folder: where }),
                check: t('js.viewTree.dontAskAgain'), ok: t('js.viewTree.move') }) : Promise.resolve({ ok: true }))
            : ask({ title: t('js.viewTree.moveTitle', { name: item.name }),
                message: t('js.viewTree.moveConfirm', { name: item.name, folder: where }) + ' ' + crossTreeWarning(item.tree, target.tree),
                ok: t('js.viewTree.move') });

        return question.then(function (a) {
            if (!a.ok) return false;
            var stopAsking = sameTree && a.checked
                ? api('POST', '/api/views/move-confirm-off').then(function () { data.askBeforeMove = false; }, function () { })
                : Promise.resolve();
            var url = (item.kind === 'view' ? '/api/views/' : '/api/views/folders/') + encodeURIComponent(item.id) + '/move';
            return stopAsking.then(function () {
                return run(api('POST', url, { tree: target.tree, folderId: target.folderId || null }));
            }).then(function () { expandTarget(target); return true; }, function () { return false; });
        });
    }

    // ── Context menu ────────────────────────────────────────────────────────

    function menuItems(info) {
        var items = [];
        var place = { tree: info.tree, folderId: info.kind === 'folder' ? info.id : null };
        if (info.kind === 'view') {
            items.push({ label: t('js.viewTree.open'), act: function () { window.location.href = '/Views/Play/' + encodeURIComponent(info.id); } });
            if (info.canEdit) {
                items.push({ label: t('js.viewTree.edit'), act: function () { window.location.href = '/Views/Editor/' + encodeURIComponent(info.id); } });
                items.push({ label: t('js.viewTree.rename'), act: function () { rename(info); } });
            }
            items.push({ label: t('js.viewTree.copy'), act: function () { remember(info, 'copy'); } });
            if (info.canEdit) {
                items.push({ label: t('js.viewTree.cut'), act: function () { remember(info, 'cut'); } });
                items.push('-');
                items.push({ label: t('js.viewTree.delete'), danger: true, act: function () { remove(info); } });
            }
            return items;
        }
        if (!info.canEdit) return [];
        items.push({ label: t('js.viewTree.newView'), act: function () { newView(info.tree, place.folderId); } });
        items.push({ label: t('js.viewTree.newFolder'), act: function () { newFolder(info.tree, place.folderId); } });
        if (clipboard) items.push({ label: t('js.viewTree.paste', { name: clipboard.name }), act: function () { paste(place); } });
        if (info.kind === 'folder') {
            items.push({ label: t('js.viewTree.rename'), act: function () { rename(info); } });
            items.push({ label: t('js.viewTree.cut'), act: function () { remember(info, 'cut'); } });
            items.push('-');
            items.push({ label: t('js.viewTree.delete'), danger: true, act: function () { remove(info); } });
        }
        return items;
    }

    function openMenu(row, x, y) {
        var items = menuItems(rowInfo(row));
        if (!items.length) return;
        menuFor = row;
        menuEl.textContent = '';
        items.forEach(function (item) {
            if (item === '-') {
                var hr = document.createElement('hr');
                hr.className = 'dropdown-divider';
                menuEl.appendChild(hr);
                return;
            }
            var b = document.createElement('button');
            b.type = 'button';
            b.className = 'dropdown-item' + (item.danger ? ' text-danger' : '');
            b.setAttribute('role', 'menuitem');
            b.textContent = item.label;
            b.addEventListener('click', function () { closeMenu(false); item.act(); });
            menuEl.appendChild(b);
        });
        menuEl.classList.add('show');
        var w = menuEl.offsetWidth, h = menuEl.offsetHeight;
        menuEl.style.left = Math.max(4, Math.min(x, window.innerWidth - w - 4)) + 'px';
        menuEl.style.top = Math.max(4, Math.min(y, window.innerHeight - h - 4)) + 'px';
        var first = menuEl.querySelector('.dropdown-item');
        if (first) first.focus();
    }

    function closeMenu(refocus) {
        if (!menuEl.classList.contains('show')) return;
        menuEl.classList.remove('show');
        if (refocus !== false && menuFor && document.body.contains(menuFor)) menuFor.focus();
    }

    function wireMenu() {
        treeEl.addEventListener('contextmenu', function (e) {
            var row = e.target.closest('.vt-row');
            if (!row) return;
            e.preventDefault();
            openMenu(row, e.clientX, e.clientY);
        });
        menuEl.addEventListener('keydown', function (e) {
            var buttons = Array.prototype.slice.call(menuEl.querySelectorAll('.dropdown-item'));
            var i = buttons.indexOf(document.activeElement);
            if (e.key === 'ArrowDown') { e.preventDefault(); buttons[(i + 1) % buttons.length].focus(); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); buttons[(i - 1 + buttons.length) % buttons.length].focus(); }
            else if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); closeMenu(); }
        });
        document.addEventListener('mousedown', function (e) { if (!menuEl.contains(e.target)) closeMenu(false); });
        window.addEventListener('resize', function () { closeMenu(false); });
        window.addEventListener('scroll', function () { closeMenu(false); }, true);

        // Long press on a touch screen opens the menu too.
        var pressTimer = null, pressed = false;
        treeEl.addEventListener('touchstart', function (e) {
            var row = e.target.closest('.vt-row');
            if (!row || e.touches.length !== 1) return;
            var t = e.touches[0];
            pressed = false;
            pressTimer = setTimeout(function () { pressed = true; openMenu(row, t.clientX, t.clientY); }, LONG_PRESS_MS);
        }, { passive: true });
        ['touchend', 'touchmove', 'touchcancel'].forEach(function (type) {
            treeEl.addEventListener(type, function () { clearTimeout(pressTimer); }, { passive: true });
        });
        treeEl.addEventListener('click', function (e) {
            if (pressed) { pressed = false; e.preventDefault(); e.stopPropagation(); }
        }, true);
    }

    // ── Keyboard and clicks ─────────────────────────────────────────────────

    function focusRow(row) {
        visibleRows().forEach(function (r) { r.tabIndex = -1; });
        row.tabIndex = 0;
        row.focus();
    }

    function wireRows() {
        treeEl.addEventListener('click', function (e) {
            var row = e.target.closest('.vt-row');
            if (!row) return;
            if (row.dataset.kind !== 'view') { toggle(row); return; }
            focusRow(row);
        });
        treeEl.addEventListener('keydown', function (e) {
            var row = e.target.closest('.vt-row');
            if (!row) return;
            var rows = visibleRows();
            var i = rows.indexOf(row);
            if (e.key === 'ContextMenu' || (e.key === 'F10' && e.shiftKey)) {
                e.preventDefault();
                var r = row.getBoundingClientRect();
                openMenu(row, r.left + 24, r.bottom);
            } else if (e.key === 'ArrowDown' && i < rows.length - 1) { e.preventDefault(); focusRow(rows[i + 1]); }
            else if (e.key === 'ArrowUp' && i > 0) { e.preventDefault(); focusRow(rows[i - 1]); }
            else if (e.key === 'ArrowRight' && row.dataset.kind !== 'view') { e.preventDefault(); toggle(row, true); }
            else if (e.key === 'ArrowLeft' && row.dataset.kind !== 'view') { e.preventDefault(); toggle(row, false); }
            else if (e.key === 'Enter') {
                e.preventDefault();
                if (row.dataset.kind === 'view') window.location.href = '/Views/Play/' + encodeURIComponent(row.dataset.id);
                else toggle(row);
            }
        });
    }

    // ── Drag and drop ───────────────────────────────────────────────────────

    // Folders and tree tops someone can change; a folder can't go into its own branch.
    function dropTarget(row) {
        if (!dragging || !row || row.dataset.kind === 'view' || row.dataset.canEdit !== 'true') return null;
        var target = { tree: row.dataset.tree, folderId: row.dataset.kind === 'folder' ? row.dataset.id : null };
        if (dragging.kind === 'folder' && dragging.tree === target.tree && target.folderId
            && branchIds(rootOf(dragging.tree), dragging.id)[target.folderId]) return null;
        return target;
    }

    function wireDrag() {
        treeEl.addEventListener('dragstart', function (e) {
            var row = e.target.closest && e.target.closest('.vt-row');
            if (!row || !row.draggable) return;
            var info = rowInfo(row);
            dragging = { kind: info.kind, id: info.id, tree: info.tree, name: info.name };
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', info.name);
            row.classList.add('vt-dragging');
        });
        treeEl.addEventListener('dragend', function () {
            dragging = null;
            treeEl.querySelectorAll('.vt-dragging, .vt-drop').forEach(function (el) { el.classList.remove('vt-dragging', 'vt-drop'); });
        });
        treeEl.addEventListener('dragover', function (e) {
            var row = e.target.closest && e.target.closest('.vt-row');
            if (!dropTarget(row)) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            treeEl.querySelectorAll('.vt-drop').forEach(function (el) { if (el !== row) el.classList.remove('vt-drop'); });
            row.classList.add('vt-drop');
        });
        treeEl.addEventListener('dragleave', function (e) {
            var row = e.target.closest && e.target.closest('.vt-row');
            if (row && !row.contains(e.relatedTarget)) row.classList.remove('vt-drop');
        });
        treeEl.addEventListener('drop', function (e) {
            var row = e.target.closest && e.target.closest('.vt-row');
            var target = dropTarget(row);
            if (!target) return;
            e.preventDefault();
            row.classList.remove('vt-drop');
            var item = dragging;
            dragging = null;
            move(item, target);
        });
    }

    // ── Start ───────────────────────────────────────────────────────────────

    function init(options) {
        o = options;
        treeEl = document.getElementById(o.treeId);
        menuEl = document.getElementById(o.menuId);
        filterEl = document.getElementById(o.filterId);
        alertEl = document.getElementById(o.alertId);
        dialogEl = document.getElementById(o.dialogId);
        if (!treeEl) return;

        wireMenu();
        wireRows();
        wireDrag();
        filterEl.addEventListener('input', function () { if (data) render(); });
        document.getElementById(o.newViewId).addEventListener('click', function () { newView('private', null); });
        document.getElementById(o.newFolderId).addEventListener('click', function () { newFolder('private', null); });

        var prefs = window.larisvmsPreferences ? window.larisvmsPreferences.whenReady() : Promise.resolve();
        prefs.then(load).catch(function (err) { showAlert(err && err.message ? err.message : t('js.viewTree.failed')); });
    }

    return { init: init };
})();
