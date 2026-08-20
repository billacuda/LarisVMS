// Single source of truth for the plan's aspect-ratio list (section 5, "Views & layout"), shared by
// the view editor and the view player so a ratio string always resolves to the same number in one
// place. Stored on a cell as the string (e.g. "16:9"), not the float, so LayoutJson stays readable.
window.LarisVMSAspectRatio = (function () {
    'use strict';

    var RATIOS = {
        '1:1': 1, '4:3': 4 / 3, '3:2': 3 / 2, '16:10': 16 / 10, '16:9': 16 / 9,
        '1.85:1': 1.85, '21:9': 21 / 9, '2.39:1': 2.39,
        '3:4': 3 / 4, '2:3': 2 / 3, '10:16': 10 / 16, '9:16': 9 / 16, '9:21': 9 / 21
    };

    // Display order: common landscape ratios first, then the vertical inverses.
    var OPTIONS = ['16:9', '4:3', '1:1', '3:2', '16:10', '1.85:1', '21:9', '2.39:1',
        '9:16', '3:4', '2:3', '10:16', '9:21'];

    var DEFAULT = '16:9';

    // Closest listed ratio to a camera's actual reported resolution, or null when the resolution
    // isn't known (a camera that has never been probed, or an older page that doesn't send it).
    // Callers treat null as "fall back to whatever was stored/defaulted" rather than guessing.
    //
    // Exists because the editor used to default every newly-added cell to DEFAULT regardless of the
    // camera — confirmed live as "landscape on phone squishes some cameras but not others": the
    // derived phone stack sizes each cell from its stored aspect, so any camera that isn't actually
    // 16:9 got a cell shaped wrong for it and object-fit:contain letterboxed the video down into a
    // band inside it. Matching by ratio *value* rather than requiring an exact w:h match means an
    // unusual resolution (2560x1440, 3840x2160, 640x480) still resolves to the right listed entry.
    function nearest(width, height) {
        if (!width || !height || width <= 0 || height <= 0) return null;
        var target = width / height;
        var bestKey = null, bestDelta = Infinity;
        Object.keys(RATIOS).forEach(function (key) {
            var delta = Math.abs(RATIOS[key] - target);
            if (delta < bestDelta) { bestDelta = delta; bestKey = key; }
        });
        return bestKey;
    }

    return {
        options: OPTIONS,
        default: DEFAULT,
        ratio: function (key) { return RATIOS[key] || RATIOS[DEFAULT]; },
        isValid: function (key) { return Object.prototype.hasOwnProperty.call(RATIOS, key); },
        nearest: nearest
    };
})();
