window.techMesPdfViewer = {
    // Пытается прочитать page/zoom из src или текущего location встроенного PDF iframe.
    getViewState(frameId) {
        const frame = document.getElementById(frameId);

        if (!frame) {
            return null;
        }

        let url = frame.getAttribute("src") || "";

        try {
            const href = frame.contentWindow?.location?.href;

            if (href) {
                url = href;
            }
        } catch {
            // Встроенные PDF-viewer-ы браузера могут быть изолированы от host page.
        }

        return parsePdfViewState(url);
    }
};

// Разбирает PDF fragment вида #page=7&zoom=175.
function parsePdfViewState(url) {
    if (!url) {
        return null;
    }

    let hash = "";

    try {
        hash = new URL(url, document.baseURI).hash || "";
    } catch {
        const hashIndex = url.indexOf("#");
        hash = hashIndex >= 0 ? url.substring(hashIndex) : "";
    }

    if (!hash) {
        return null;
    }

    const parameters = new URLSearchParams(hash.replace(/^#/, ""));
    const page = parsePositiveInteger(parameters.get("page"));
    const zoom = parsePositiveNumber((parameters.get("zoom") || "").split(",")[0]);

    return {
        PageNumber: page,
        ZoomFactor: zoom
    };
}

// Читает положительное целое число или возвращает null.
function parsePositiveInteger(value) {
    const parsed = Number.parseInt(value || "", 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

// Читает положительное дробное число или возвращает null.
function parsePositiveNumber(value) {
    const parsed = Number.parseFloat(value || "");
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}
