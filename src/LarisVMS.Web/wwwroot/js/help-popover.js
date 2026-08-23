// Wires every field-level help icon on the page to a Bootstrap Popover sourcing its content from a
// same-page element rather than a data-bs-content attribute — this app's help text is often several
// sentences with embedded <a>/<code>/<strong> tags, which is awkward to author as a single HTML
// attribute string but is exactly what a normal Razor-authored <div> already handles. Explicit
// id-based targeting (data-help-target), not DOM proximity, since some fields (Recording mode
// override, in particular) already had several conditional help blocks stacked after one input and
// proximity heuristics can't tell which one belongs to which icon.
//
// trigger: 'hover focus', not 'click' — focus already fires from a mouse click on the icon (it's a
// real, tabbable <button>), so this covers "hover or click" from the ask while also being reachable
// by keyboard, and blur/mouseleave dismissing it needs no extra dismiss-on-outside-click handling
// Bootstrap's own 'click' trigger would otherwise require.
(function () {
    document.addEventListener('DOMContentLoaded', function () {
        if (typeof bootstrap === 'undefined' || !bootstrap.Popover) return;

        document.querySelectorAll('.help-icon[data-help-target]').forEach(function (icon) {
            var source = document.getElementById(icon.getAttribute('data-help-target'));
            if (!source) return;

            new bootstrap.Popover(icon, {
                html: true,
                trigger: 'hover focus',
                placement: 'auto',
                container: 'body',
                content: source.innerHTML
            });
        });
    });
})();
