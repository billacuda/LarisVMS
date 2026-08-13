// M8 pass 8: event tag rule editor — list + form panels against /api/cameras/{id}/event-tag-rules
// and /api/event-tag-rules/{id}, plus a read-only observed-topics panel. Framework-free, same
// convention as zones-editor.js/timeline.js. No canvas here — a rule is a topic match, not a shape.
window.nidusvmsEventTagRulesEditor = (function () {
    'use strict';

    function escHtml(s) {
        return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function init(o) {
        var opts = o;
        var rules = [];
        var selectedId = null;

        function renderList() {
            var panel = document.getElementById(opts.listPanelId);
            if (!rules.length) {
                panel.innerHTML = '<p class="text-muted small">No event tag rules yet.</p>';
                return;
            }
            var html = '<div class="list-group">';
            rules.forEach(function (r) {
                var topics = escHtml(r.startTopic) + (r.stopTopic ? ' &rarr; ' + escHtml(r.stopTopic) : ' (toggle)');
                html += '<button type="button" class="list-group-item list-group-item-action py-2' +
                    (r.id === selectedId ? ' active' : '') + '" data-rule-id="' + r.id + '">' +
                    '<span class="d-inline-block rounded-circle me-2" style="width:10px;height:10px;background:' + escHtml(r.colorHex) + ';"></span>' +
                    escHtml(r.name) +
                    (r.isEnabled ? '' : ' <span class="text-muted small">(disabled)</span>') +
                    (r.drivesRecording ? ' <span class="badge text-bg-primary">drives recording</span>' : '') +
                    '<div class="text-muted small">' + topics + '</div>' +
                    '</button>';
            });
            html += '</div>';
            panel.innerHTML = html;

            Array.prototype.forEach.call(panel.querySelectorAll('[data-rule-id]'), function (el) {
                el.addEventListener('click', function () { selectRule(el.getAttribute('data-rule-id')); });
            });
        }

        async function loadRules() {
            var resp = await fetch('/api/cameras/' + opts.cameraId + '/event-tag-rules');
            rules = resp.ok ? await resp.json() : [];
            renderList();
        }

        function showForm(rule) {
            selectedId = rule ? rule.id : null;
            document.getElementById(opts.formPanelId).style.display = '';
            document.getElementById(opts.formTitleId).textContent = rule ? ('Edit rule: ' + rule.name) : 'New rule';
            document.getElementById(opts.formIdInputId).value = rule ? rule.id : '';
            document.getElementById(opts.formNameId).value = rule ? rule.name : '';
            document.getElementById(opts.formStartTopicId).value = rule ? rule.startTopic : '';
            document.getElementById(opts.formStopTopicId).value = rule ? (rule.stopTopic || '') : '';
            document.getElementById(opts.formColorId).value = rule ? rule.colorHex : '#ff9800';
            document.getElementById(opts.formDrivesRecordingId).checked = rule ? rule.drivesRecording : false;
            document.getElementById(opts.formEnabledId).checked = rule ? rule.isEnabled : true;
            document.getElementById(opts.formDeleteBtnId).style.display = rule ? '' : 'none';
            renderList();
        }

        function hideForm() {
            selectedId = null;
            document.getElementById(opts.formPanelId).style.display = 'none';
            renderList();
        }

        function selectRule(id) {
            var rule = rules.find(function (r) { return r.id === id; });
            if (rule) showForm(rule);
        }

        document.getElementById(opts.newBtnId).addEventListener('click', function () { showForm(null); });
        document.getElementById(opts.formCancelBtnId).addEventListener('click', hideForm);

        document.getElementById(opts.formSaveBtnId).addEventListener('click', async function () {
            var id = document.getElementById(opts.formIdInputId).value;
            var body = {
                name: document.getElementById(opts.formNameId).value.trim(),
                startTopic: document.getElementById(opts.formStartTopicId).value.trim(),
                stopTopic: document.getElementById(opts.formStopTopicId).value.trim() || null,
                colorHex: document.getElementById(opts.formColorId).value,
                drivesRecording: document.getElementById(opts.formDrivesRecordingId).checked,
                isEnabled: document.getElementById(opts.formEnabledId).checked
            };
            if (!body.name || !body.startTopic) { alert('Name and Start topic are required.'); return; }

            var url = id ? '/api/event-tag-rules/' + id : '/api/cameras/' + opts.cameraId + '/event-tag-rules';
            var method = id ? 'PUT' : 'POST';
            var resp = await fetch(url, { method: method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            if (!resp.ok) { alert('Could not save this rule.'); return; }

            hideForm();
            await loadRules();
        });

        document.getElementById(opts.formDeleteBtnId).addEventListener('click', async function () {
            var id = document.getElementById(opts.formIdInputId).value;
            if (!id || !confirm('Delete this rule? Timeline history already tagged by it is kept, just no longer attributed to it.')) return;
            var resp = await fetch('/api/event-tag-rules/' + id, { method: 'DELETE' });
            if (!resp.ok) { alert('Could not delete this rule.'); return; }
            hideForm();
            await loadRules();
        });

        // ── Observed topics ─────────────────────────────────────────────────
        async function loadObservedTopics() {
            var panel = document.getElementById(opts.observedTopicsPanelId);
            var datalist = document.getElementById(opts.observedTopicsListId);
            var resp = await fetch('/api/cameras/' + opts.cameraId + '/event-tag-rules/observed-topics');
            var topics = resp.ok ? await resp.json() : [];

            datalist.innerHTML = topics.map(function (t) {
                return '<option value="' + escHtml(t.topic) + '">';
            }).join('');

            if (!topics.length) {
                panel.innerHTML = '<p class="text-muted small">No ONVIF events observed from this camera yet — once it sends some, they\'ll show up here.</p>';
                return;
            }
            var html = '<div class="list-group">';
            topics.forEach(function (t) {
                html += '<button type="button" class="list-group-item list-group-item-action py-2" data-topic="' + escHtml(t.topic) + '">' +
                    '<div class="small font-monospace">' + escHtml(t.topic) + '</div>' +
                    '<div class="text-muted small">' + t.count + ' seen, last ' + new Date(t.lastSeenUtc).toLocaleString() + '</div>' +
                    '</button>';
            });
            html += '</div>';
            panel.innerHTML = html;

            Array.prototype.forEach.call(panel.querySelectorAll('[data-topic]'), function (el) {
                el.addEventListener('click', function () {
                    if (document.getElementById(opts.formPanelId).style.display === 'none') showForm(null);
                    document.getElementById(opts.formStartTopicId).value = el.getAttribute('data-topic');
                });
            });
        }

        loadRules();
        loadObservedTopics();
    }

    return { init: init };
})();
