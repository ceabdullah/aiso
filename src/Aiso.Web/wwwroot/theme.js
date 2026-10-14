// Theme persistence (per browser). Runs before first paint; no external dependencies (on-prem friendly).
(function () {
    const key = "aiso-theme";
    const read = () => { try { return localStorage.getItem(key); } catch { return null; } };
    const write = (v) => { try { localStorage.setItem(key, v); } catch { /* storage blocked */ } };
    const apply = (v) => document.documentElement.setAttribute("data-theme", v === "light" ? "light" : "dark");

    apply(read() || "dark");

    window.aisoTheme = {
        get: () => document.documentElement.getAttribute("data-theme") || "dark",
        set: (v) => { apply(v); write(v); return window.aisoTheme.get(); },
        toggle: () => window.aisoTheme.set(window.aisoTheme.get() === "dark" ? "light" : "dark"),
        // Applies the server default only when the user has never chosen a theme.
        applyDefault: (v) => { if (!read()) { apply(v); } return window.aisoTheme.get(); }
    };
})();
