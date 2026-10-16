// Settings → Encryption: master key create / backup download / restore, against
// /api/encryption/*. The backup arrives as a JSON file and is saved through a blob link.
(function () {
    'use strict';

    var statusEl = document.getElementById('encryptionKeyStatus');
    if (!statusEl) return;

    function say(kind, text) {
        statusEl.innerHTML = '';
        var div = document.createElement('div');
        div.className = 'alert py-2 mb-0 alert-' + kind;
        div.textContent = text;
        statusEl.appendChild(div);
    }

    function post(url, body) {
        return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) });
    }

    var create = document.getElementById('encCreateKey');
    if (create) create.addEventListener('click', function () {
        create.disabled = true;
        post('/api/encryption/master-key').then(function (r) {
            if (!r.ok) throw new Error();
            location.reload();
        }).catch(function () { create.disabled = false; say('danger', 'Couldn\'t create the master key. Try again.'); });
    });

    var backup = document.getElementById('encBackup');
    if (backup) backup.addEventListener('click', function () {
        var p1 = document.getElementById('encPass1').value, p2 = document.getElementById('encPass2').value;
        if (p1.length < 12) { say('warning', 'Use a passphrase of at least 12 characters.'); return; }
        if (p1 !== p2) { say('warning', 'The two passphrases don\'t match.'); return; }
        backup.disabled = true;
        say('info', 'Preparing the backup…');
        post('/api/encryption/backup', { passphrase: p1 }).then(function (r) {
            if (!r.ok) return r.text().then(function (t) { throw new Error(t || 'Couldn\'t make the backup.'); });
            return r.blob().then(function (blob) {
                var a = document.createElement('a');
                a.href = URL.createObjectURL(blob);
                a.download = 'larisvms-master-key-backup-' + new Date().toISOString().slice(0, 10) + '.json';
                document.body.appendChild(a);
                a.click();
                a.remove();
                setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
                say('success', 'Backup downloaded. Keep it, and the passphrase, somewhere safe and away from this server.');
                setTimeout(function () { location.reload(); }, 2500);
            });
        }).catch(function (e) { say('danger', e.message); })
          .then(function () { backup.disabled = false; });
    });

    var restore = document.getElementById('encRestore');
    if (restore) restore.addEventListener('click', function () {
        var file = document.getElementById('encRestoreFile').files[0];
        var pass = document.getElementById('encRestorePass').value;
        if (!file || !pass) { say('warning', 'Choose the backup file and enter its passphrase.'); return; }
        restore.disabled = true;
        file.text().then(function (text) {
            return post('/api/encryption/restore', { backup: text, passphrase: pass });
        }).then(function (r) { return r.json(); })
          .then(function (res) {
              say(res.ok ? 'success' : 'danger', res.message);
              if (res.ok) setTimeout(function () { location.reload(); }, 2000);
          })
          .catch(function () { say('danger', 'Couldn\'t read that backup.'); })
          .then(function () { restore.disabled = false; });
    });
})();
