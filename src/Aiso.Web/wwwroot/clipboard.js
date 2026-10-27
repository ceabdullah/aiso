// Copy text for the Suggestions "Apply" kits. The Clipboard API needs a secure, focused page and can be
// blocked by policy, so fall back to a hidden textarea. Returns whether the copy worked.
window.aisoClipboard = {
    copy: async (text) => {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            const area = document.createElement("textarea");
            area.value = text;
            area.setAttribute("readonly", "");
            area.style.position = "fixed";
            area.style.opacity = "0";
            document.body.appendChild(area);
            area.select();
            try { return document.execCommand("copy"); } catch { return false; } finally { area.remove(); }
        }
    }
};
