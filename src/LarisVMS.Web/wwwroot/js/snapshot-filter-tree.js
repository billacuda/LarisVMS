// Snapshots filter tree. Two jobs:
//
//   1. The standard checkbox-tree parent/child sync, plus building the excludedLabels hidden inputs
//      the form actually submits — moved verbatim out of Snapshots/Index.cshtml's inline @section
//      Scripts. Child (label) checkboxes deliberately carry no `name`: a plain checked-box name/value
//      pair only submits *checked* items, but the server wants which specific labels are UNchecked.
//      So children stay "checked = included" for display, and the submit handler converts the ones
//      left unchecked into excludedLabels inputs.
//
//   2. Per-user memory of the tree's state through larisvmsPreferences, for the toggles the user
//      makes without clicking Filter:
//        - Expand/collapse of each category <details> (pref `snapshots.tree.collapsed`, a JSON array
//          of collapsed category names). Always restored; independent of the query string.
//        - Checkbox selection (pref `snapshots.tree.selection`, `{uncheckedKinds, excludedLabels}`).
//          Restored ONLY when the URL carries no filter and remember-filters.js also has nothing
//          saved — the one case where the server rendered the plain all-checked default and nothing
//          else will touch it. On any load with a query string, the server already rendered the
//          boxes from it and remember-filters.js already saved it, so this leaves it alone. The
//          restore does NOT auto-submit: the grid keeps showing the server default until the user
//          clicks Filter, so first paint is never silently changed out from under them.
//
// Server-backed via larisvmsPreferences, so the tree follows the user across devices.
(function () {
    'use strict';

    var form = document.getElementById('snapshotFilterForm');
    var tree = document.querySelector('.snapshot-filter-tree');
    if (!form || !tree) return;

    var COLLAPSED_KEY = 'snapshots.tree.collapsed';
    var SELECTION_KEY = 'snapshots.tree.selection';
    var prefs = window.larisvmsPreferences;

    function childrenOf(parentName) {
        return Array.prototype.slice.call(
            tree.querySelectorAll('.snapshot-tree-child[data-parent-name="' + CSS.escape(parentName) + '"]'));
    }

    // A category's parent checkbox reflects all-checked / all-unchecked / mixed from its children.
    function reconcileParent(parentName) {
        var parent = tree.querySelector('.snapshot-tree-parent[data-children-of="' + CSS.escape(parentName) + '"]');
        if (!parent) return;
        var siblings = childrenOf(parentName);
        var checkedCount = siblings.filter(function (s) { return s.checked; }).length;
        parent.checked = checkedCount > 0;
        parent.indeterminate = checkedCount > 0 && checkedCount < siblings.length;
    }

    // --- parent/child sync (unchanged behavior) ---

    tree.querySelectorAll('.snapshot-tree-parent').forEach(function (parent) {
        parent.addEventListener('change', function () {
            childrenOf(parent.dataset.childrenOf).forEach(function (child) { child.checked = parent.checked; });
        });
    });

    tree.querySelectorAll('.snapshot-tree-child').forEach(function (child) {
        child.addEventListener('change', function () {
            reconcileParent(child.dataset.parentName);
        });
    });

    form.addEventListener('submit', function () {
        form.querySelectorAll('input[name="excludedLabels"]').forEach(function (el) { el.remove(); });
        tree.querySelectorAll('.snapshot-tree-child').forEach(function (child) {
            if (child.checked) return;
            var hidden = document.createElement('input');
            hidden.type = 'hidden';
            hidden.name = 'excludedLabels';
            hidden.value = child.dataset.labelToken;
            form.appendChild(hidden);
        });
    });

    // --- state memory ---

    // Node identity is the category name (the parent checkbox's value / data-children-of) — stable
    // across reloads even though the DOM order of categories is not (they come from a live query).
    function categoryOf(details) {
        var parent = details.querySelector('.snapshot-tree-parent');
        return parent ? parent.value : null;
    }

    function readJson(key) {
        var raw = prefs.get(key, null);
        if (!raw) return null;
        try { return JSON.parse(raw); } catch (e) { return null; }
    }

    function saveCollapsed() {
        var collapsed = [];
        tree.querySelectorAll('details.snapshot-filter-tree-node').forEach(function (d) {
            var name = categoryOf(d);
            if (name && !d.open) collapsed.push(name);
        });
        prefs.set(COLLAPSED_KEY, JSON.stringify(collapsed));
    }

    function saveSelection() {
        var uncheckedKinds = [];
        tree.querySelectorAll('input[name="kinds"]').forEach(function (cb) {
            if (!cb.checked) uncheckedKinds.push(cb.value);
        });
        var excludedLabels = [];
        tree.querySelectorAll('.snapshot-tree-child').forEach(function (cb) {
            if (!cb.checked) excludedLabels.push(cb.dataset.labelToken);
        });
        prefs.set(SELECTION_KEY, JSON.stringify({ uncheckedKinds: uncheckedKinds, excludedLabels: excludedLabels }));
    }

    // One delegated listener, registered after the per-input sync listeners above so it always
    // reads the DOM they have already reconciled (a parent change unchecks its children first,
    // then this fires and records the settled state).
    tree.addEventListener('change', saveSelection);

    document.querySelectorAll('[data-clear-filters]').forEach(function (el) {
        el.addEventListener('click', function () {
            prefs.set(SELECTION_KEY, '');
            prefs.set(COLLAPSED_KEY, '');
        });
    });

    // --- restore on load ---

    prefs.whenReady().then(function () {
        var collapsed = readJson(COLLAPSED_KEY);
        if (Array.isArray(collapsed)) {
            var collapsedSet = {};
            collapsed.forEach(function (n) { collapsedSet[n] = true; });
            tree.querySelectorAll('details.snapshot-filter-tree-node').forEach(function (d) {
                var name = categoryOf(d);
                d.open = !(name && collapsedSet[name]); // not in the set (e.g. a brand-new category) => open
            });
        }
        // Attached only after the restore loop above, so those programmatic .open writes don't
        // fire straight back into the pref.
        tree.querySelectorAll('details.snapshot-filter-tree-node').forEach(function (d) {
            d.addEventListener('toggle', saveCollapsed);
        });

        var searchEmpty = window.location.search === '' || window.location.search === '?';
        var rememberEmpty = prefs.get('filters.snapshots', '') === '';
        if (!searchEmpty || !rememberEmpty) return;

        var selection = readJson(SELECTION_KEY);
        if (!selection) return;
        var uncheckedKinds = Array.isArray(selection.uncheckedKinds) ? selection.uncheckedKinds : [];
        var excludedLabels = Array.isArray(selection.excludedLabels) ? selection.excludedLabels : [];

        uncheckedKinds.forEach(function (val) {
            tree.querySelectorAll('input[name="kinds"]').forEach(function (cb) {
                if (cb.value === val) cb.checked = false;
            });
        });
        var touchedParents = {};
        excludedLabels.forEach(function (token) {
            var child = tree.querySelector('.snapshot-tree-child[data-label-token="' + CSS.escape(token) + '"]');
            if (!child) return;
            child.checked = false;
            touchedParents[child.dataset.parentName] = true;
        });
        Object.keys(touchedParents).forEach(reconcileParent);
    });
})();
