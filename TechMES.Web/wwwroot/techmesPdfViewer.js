window.techMesPdfViewer = {
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
            // Browser-native PDF viewers can be isolated from the host page.
        }

        return parsePdfViewState(url);
    }
};

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

function parsePositiveInteger(value) {
    const parsed = Number.parseInt(value || "", 10);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function parsePositiveNumber(value) {
    const parsed = Number.parseFloat(value || "");
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}
