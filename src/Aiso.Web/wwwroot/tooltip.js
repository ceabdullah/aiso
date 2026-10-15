// Tooltips for any element with data-tip (Info.razor, tabs, tags). One floating element, positioned in the
// viewport so it is never clipped by scrolling tables or panels. Hover or keyboard focus shows it; on touch
// screens a tap on an "i" button shows it, and a tap elsewhere, Escape or scrolling hides it.
(function () {
    let tip = null;
    let current = null;

    function element() {
        if (!tip || !tip.isConnected) {
            tip = document.createElement("div");
            tip.id = "aiso-tooltip";
            tip.className = "tooltip";
            tip.setAttribute("role", "tooltip");
            document.body.appendChild(tip);
        }
        return tip;
    }

    function show(target) {
        const text = target.getAttribute("data-tip");
        if (!text) { return; }
        if (current && current !== target) { current.removeAttribute("aria-describedby"); }
        current = target;
        const t = element();
        t.textContent = text;
        t.classList.add("on");
        target.setAttribute("aria-describedby", t.id);

        const r = target.getBoundingClientRect();
        const margin = 8;
        const left = Math.min(Math.max(margin, r.left + r.width / 2 - t.offsetWidth / 2), window.innerWidth - t.offsetWidth - margin);
        let top = r.top - t.offsetHeight - margin;
        if (top < margin) { top = r.bottom + margin; }
        t.style.left = `${left}px`;
        t.style.top = `${top}px`;
    }

    function hide() {
        if (current) { current.removeAttribute("aria-describedby"); }
        current = null;
        if (tip) { tip.classList.remove("on"); }
    }

    const tipOf = (e) => (e.target instanceof Element ? e.target.closest("[data-tip]") : null);

    document.addEventListener("mouseover", (e) => { const el = tipOf(e); if (el) { show(el); } });
    document.addEventListener("mouseout", (e) => {
        const el = tipOf(e);
        if (el && el === current && !(e.relatedTarget instanceof Node && el.contains(e.relatedTarget))) { hide(); }
    });
    document.addEventListener("focusin", (e) => { const el = tipOf(e); if (el) { show(el); } });
    document.addEventListener("focusout", (e) => { if (tipOf(e) === current) { hide(); } });
    document.addEventListener("click", (e) => {
        const el = tipOf(e);
        if (el && el.classList.contains("info")) {
            e.preventDefault();
            show(el); // hover may already show it; a tap on a touch screen has no hover
        } else if (!el) {
            hide();
        }
    });
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") { hide(); } });
    window.addEventListener("scroll", hide, { passive: true, capture: true });
    window.addEventListener("resize", hide);
})();
