// Minimal dark-mode toggle; a real switch UI can call window.nidusvmsSetTheme('dark'|'light').
window.nidusvmsSetTheme = function (theme) {
    document.documentElement.setAttribute('data-bs-theme', theme);
    localStorage.setItem('nidusvms.theme', theme);
};
