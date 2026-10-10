// Group autocomplete on Settings › Active Directory. As the admin types, asks
// /api/admin/ad/groups for up to 10 security groups whose name starts with the text (alphabetical)
// and lists them under the box. Tab takes the first match; arrows + Enter or a click take any other.
// Picking a group fills the hidden SID field — links are stored by SID so a later rename in AD
// doesn't break them. Typing again clears the SID; the server then accepts an exact name match.
(function () {
    var input = document.getElementById('adGroupInput');
    var sidField = document.getElementById('adGroupSid');
    var list = document.getElementById('adGroupList');
    var status = document.getElementById('adGroupStatus');
    if (!input || !sidField || !list) return;

    var items = [];
    var active = -1;
    var timer = null;
    var requestSeq = 0;

    function setStatus(text) {
        if (status) status.textContent = text || '';
    }

    function render() {
        list.innerHTML = '';
        items.forEach(function (group, i) {
            var li = document.createElement('li');
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'dropdown-item' + (i === active ? ' active' : '');
            btn.setAttribute('role', 'option');
            btn.setAttribute('aria-selected', i === active ? 'true' : 'false');
            btn.textContent = group.name;
            // mousedown, not click: fires before the input's blur closes the list.
            btn.addEventListener('mousedown', function (e) {
                e.preventDefault();
                choose(i);
            });
            li.appendChild(btn);
            list.appendChild(li);
        });
        var open = items.length > 0;
        list.classList.toggle('show', open);
        input.setAttribute('aria-expanded', open ? 'true' : 'false');
    }

    function close() {
        items = [];
        active = -1;
        render();
    }

    function choose(i) {
        var group = items[i];
        if (!group) return;
        input.value = group.name;
        sidField.value = group.sid;
        setStatus('');
        close();
    }

    function search(q) {
        var seq = ++requestSeq;
        fetch('/api/admin/ad/groups?q=' + encodeURIComponent(q), { headers: { Accept: 'application/json' } })
            .then(function (r) {
                if (r.ok) return r.json();
                return r.json().catch(function () { return {}; }).then(function (problem) {
                    throw new Error(problem.detail || 'Group lookup failed.');
                });
            })
            .then(function (groups) {
                if (seq !== requestSeq) return; // a newer keystroke's results win
                items = Array.isArray(groups) ? groups : [];
                active = items.length ? 0 : -1;
                setStatus(items.length ? '' : 'No matching groups.');
                render();
            })
            .catch(function (err) {
                if (seq !== requestSeq) return;
                close();
                setStatus(err.message);
            });
    }

    input.addEventListener('input', function () {
        sidField.value = '';
        clearTimeout(timer);
        var q = input.value.trim();
        if (!q) {
            requestSeq++;
            close();
            setStatus('');
            return;
        }
        timer = setTimeout(function () { search(q); }, 200);
    });

    input.addEventListener('keydown', function (e) {
        if (e.key === 'Tab' && !e.shiftKey && items.length) {
            // Autocomplete to the top match; focus still moves on to the role picker.
            choose(0);
            return;
        }
        if (!items.length) return;
        if (e.key === 'ArrowDown') {
            e.preventDefault();
            active = (active + 1) % items.length;
            render();
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            active = (active - 1 + items.length) % items.length;
            render();
        } else if (e.key === 'Enter' && active >= 0) {
            e.preventDefault();
            choose(active);
        } else if (e.key === 'Escape') {
            close();
        }
    });

    input.addEventListener('blur', function () {
        setTimeout(close, 150);
    });
})();
