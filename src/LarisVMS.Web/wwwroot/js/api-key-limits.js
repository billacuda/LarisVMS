// The API key form: shows the permission checkboxes while "Limit this key" is ticked, offering only
// what the chosen role has. A key can't exceed its role, so anything else would be misleading. The
// server checks this too.
(function () {
    'use strict';

    var form = document.getElementById('apiKeyForm');
    if (!form) return;

    var byRole = JSON.parse(form.dataset.permissionsByRole || '{}');
    var roleSelect = document.getElementById('apiKeyRole');
    var limit = document.getElementById('apiKeyLimit');
    var list = document.getElementById('apiKeyLimits');
    var none = document.getElementById('apiKeyNoPermissions');

    function refresh() {
        list.classList.toggle('d-none', !limit.checked);

        var held = byRole[roleSelect ? roleSelect.value : form.dataset.roleId] || [];
        list.querySelectorAll('[data-permission]').forEach(function (row) {
            var available = held.indexOf(row.dataset.permission) !== -1;
            row.classList.toggle('d-none', !available);
            if (!available) row.querySelector('input').checked = false;
        });
        none.classList.toggle('d-none', held.length > 0);
    }

    limit.addEventListener('change', refresh);
    if (roleSelect) roleSelect.addEventListener('change', refresh);
    refresh();
})();
