// Single source of truth for the plan's aspect-ratio list (section 5, "Views & layout"), shared by
// the view editor and the view player so a ratio string always resolves to the same number in one
// place. Stored on a cell as the string (e.g. "16:9"), not the float, so LayoutJson stays readable.
window.NidusVMSAspectRatio = (function () {
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

    return {
        options: OPTIONS,
        default: DEFAULT,
        ratio: function (key) { return RATIOS[key] || RATIOS[DEFAULT]; },
        isValid: function (key) { return Object.prototype.hasOwnProperty.call(RATIOS, key); }
    };
})();
