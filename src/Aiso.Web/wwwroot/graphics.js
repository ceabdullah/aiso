// Turns a content graphic's SVG into a PNG of its full size and downloads it (ContentTab.razor).
// The SVG uses system fonts only, so it renders the same in the canvas as in the preview.
window.aisoGraphics = {
    downloadPng: (svg, width, height, fileName) => new Promise((resolve, reject) => {
        const url = URL.createObjectURL(new Blob([svg], { type: "image/svg+xml;charset=utf-8" }));
        const img = new Image();
        img.onload = () => {
            const canvas = document.createElement("canvas");
            canvas.width = width;
            canvas.height = height;
            canvas.getContext("2d").drawImage(img, 0, 0, width, height);
            URL.revokeObjectURL(url);
            canvas.toBlob((blob) => {
                if (!blob) { reject(new Error("PNG export failed")); return; }
                const a = document.createElement("a");
                a.href = URL.createObjectURL(blob);
                a.download = fileName;
                document.body.appendChild(a);
                a.click();
                a.remove();
                setTimeout(() => URL.revokeObjectURL(a.href), 1000);
                resolve();
            }, "image/png");
        };
        img.onerror = () => { URL.revokeObjectURL(url); reject(new Error("SVG could not be drawn")); };
        img.src = url;
    }),
};
