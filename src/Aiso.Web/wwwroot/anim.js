// Count-up numbers for elements rendered by CountUp.razor: animates from the last shown value to data-value.
// Blazor only owns the element's attributes; the text node is ours. Honours prefers-reduced-motion.
(function () {
    const reduced = window.matchMedia("(prefers-reduced-motion: reduce)");
    const shown = new WeakMap();

    const format = (v, decimals) => v.toFixed(decimals);

    function run(el) {
        const target = parseFloat(el.dataset.value);
        const decimals = parseInt(el.dataset.decimals || "1", 10);
        if (Number.isNaN(target)) { return; }
        const from = shown.has(el) ? shown.get(el) : 0;
        shown.set(el, target);
        if (reduced.matches || from === target) { el.textContent = format(target, decimals); return; }

        const start = performance.now();
        const duration = 700;
        const step = (now) => {
            const t = Math.min(1, (now - start) / duration);
            const eased = 1 - Math.pow(1 - t, 3);
            el.textContent = format(from + (target - from) * eased, decimals);
            if (t < 1 && shown.get(el) === target) { requestAnimationFrame(step); }
        };
        requestAnimationFrame(step);
    }

    function scan(root) {
        if (root.nodeType !== 1) { return; }
        if (root.classList.contains("count-up")) { run(root); }
        root.querySelectorAll(".count-up").forEach(run);
    }

    new MutationObserver((mutations) => {
        for (const m of mutations) {
            if (m.type === "attributes" && m.target.classList.contains("count-up")) { run(m.target); }
            m.addedNodes.forEach(scan);
        }
    }).observe(document.documentElement, { subtree: true, childList: true, attributes: true, attributeFilter: ["data-value"] });

    document.addEventListener("DOMContentLoaded", () => scan(document.body));
})();
