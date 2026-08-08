// Minimal dark-mode toggle; a real switch UI can call window.rcordrSetTheme('dark'|'light').
window.rcordrSetTheme = function (theme) {
    document.documentElement.setAttribute('data-bs-theme', theme);
    localStorage.setItem('rcordr.theme', theme);
};
