// Per-stream audio controls — a mute toggle plus a volume slider — shared by the live View cells
// (view-play.js) and the Playback page's tiles (playback-player.js), which previously each carried
// their own copy of the mute button and its icon/reapply logic.
//
// Two rules this module exists to enforce in one place:
//
//  1. **Every stream starts muted, always.** Autoplay policy aside, a wall of tiles that all start
//     talking at once is unusable, and the browser will refuse to autoplay an unmuted <video>
//     anyway. Unmuting is only ever a deliberate per-tile action, and it is never persisted across
//     a page load — reopening Live or Playback starts silent again.
//  2. **That choice survives a source reload.** Both players reload the same <video> element
//     repeatedly (live-view.js on every reconnect, playback-player.js at every segment boundary),
//     each time via removeAttribute('src') + load(). The load algorithm resets `muted` back to the
//     element's `muted` content attribute — so without reapplying it here, an unmuted tile silently
//     went muted again on the next reconnect/seek, with the button's icon left claiming otherwise.
//     `volume` has no content attribute and isn't reset by load(), but it's reapplied on the same
//     event anyway rather than relying on that distinction holding everywhere.
//
// Volume model matches what every video player on the web does, so it needs no explanation to a
// user: the slider shows *effective* volume (0 while muted), dragging it above 0 unmutes, dragging
// it to 0 mutes, and the mute button restores whatever the last non-zero level was.
window.larisvmsAudioControls = (function () {
    'use strict';

    // Full volume, not something conservative: the slider is right there to turn it down, and a
    // camera's own audio gain is usually low enough that a reduced default just reads as "unmute
    // is broken".
    var DEFAULT_VOLUME = 1;

    /// Markup for one tile's audio group. Call sites drop this inside their own hover-revealed
    /// controls container and then pass that container to wire(). Returns an empty string for a
    /// stream with no audio track (CameraStream.HasAudio false — the same flag that decides whether
    /// the MSE mime type declares an audio codec at all, so there is genuinely nothing to hear),
    /// which is what keeps a silent camera's tile from carrying a slider that can't do anything.
    function html(hasAudio) {
        if (!hasAudio) return '';
        return (
            '<span class="audio-controls d-inline-flex align-items-center gap-1">' +
                '<input type="range" class="audio-volume" min="0" max="100" step="1" value="0" ' +
                    'title="Volume" aria-label="Volume">' +
                '<button type="button" class="btn btn-sm btn-outline-light audio-mute" ' +
                    'style="padding:.1rem .35rem;" title="Unmute" aria-label="Unmute">🔇</button>' +
            '</span>'
        );
    }

    function wire(containerEl, videoEl) {
        if (!containerEl || !videoEl) return;
        var muteBtn = containerEl.querySelector('.audio-mute');
        var slider = containerEl.querySelector('.audio-volume');
        if (!muteBtn && !slider) return; // no-audio stream — html() rendered nothing

        var muted = true;
        var volume = DEFAULT_VOLUME;
        var lastNonZeroVolume = DEFAULT_VOLUME;

        function apply() {
            videoEl.volume = volume;
            videoEl.muted = muted;
        }

        function syncUi() {
            var effective = muted ? 0 : volume;
            if (slider) {
                slider.value = String(Math.round(effective * 100));
                slider.title = 'Volume — ' + Math.round(effective * 100) + '%';
            }
            if (muteBtn) {
                // Three levels rather than two: at a low volume an unmuted tile can be inaudible in
                // a noisy room, and 🔊 next to silence reads as a bug. 🔉 says "on, but quiet".
                muteBtn.textContent = effective === 0 ? '🔇' : (effective < 0.5 ? '🔉' : '🔊');
                muteBtn.title = muted ? 'Unmute' : 'Mute';
                muteBtn.setAttribute('aria-label', muteBtn.title);
            }
        }

        apply();
        syncUi();
        videoEl.addEventListener('loadstart', apply);

        if (slider) {
            slider.addEventListener('input', function () {
                volume = Math.min(1, Math.max(0, Number(slider.value) / 100));
                muted = volume === 0;
                if (volume > 0) lastNonZeroVolume = volume;
                apply();
                syncUi();
            });
            // Both tiles sit inside elements that treat plain clicks and double-clicks as gestures of
            // their own — Playback selects the clicked tile as primary, and fullscreen-tile.js opens
            // fullscreen on a double-click anywhere in the frame. Dragging a slider is neither.
            ['click', 'dblclick', 'pointerdown'].forEach(function (type) {
                slider.addEventListener(type, function (e) { e.stopPropagation(); });
            });
        }

        if (muteBtn) {
            muteBtn.addEventListener('click', function (e) {
                e.stopPropagation();
                muted = !muted;
                // Unmuting a tile the user had dragged all the way down would otherwise be silent,
                // which reads exactly like a broken button.
                if (!muted && volume === 0) volume = lastNonZeroVolume || DEFAULT_VOLUME;
                apply();
                syncUi();
            });
            muteBtn.addEventListener('dblclick', function (e) { e.stopPropagation(); });
        }
    }

    return { html: html, wire: wire };
})();
