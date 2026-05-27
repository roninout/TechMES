window.techMesQrScanner = (() => {
    const scanners = new Map();

    // Starts camera preview and QR scanning for a specific <video> element.
    // deviceId is optional: an empty value means "browser/default camera".
    async function start(videoId, dotNetRef, deviceId) {
        const video = document.getElementById(videoId);

        if (!video) {
            return { success: false, message: "Camera preview is not ready." };
        }

        if (!window.isSecureContext) {
            return {
                success: false,
                message: `Camera requires HTTPS or localhost. Current origin is not secure: ${window.location.origin}`
            };
        }

        if (!navigator.mediaDevices?.getUserMedia) {
            return {
                success: false,
                message: `Camera API is not available. SecureContext=${window.isSecureContext}; Origin=${window.location.origin}`
            };
        }

        await stop(videoId);

        try {
            const stream = await navigator.mediaDevices.getUserMedia({
                audio: false,
                video: buildVideoConstraints(deviceId)
            });

            video.srcObject = stream;
            video.setAttribute("playsinline", "true");
            video.muted = true;
            await video.play();

            const state = {
                canvas: document.createElement("canvas"),
                detector: await createBarcodeDetector(),
                dotNetRef,
                stream,
                timerId: 0,
                isBusy: false,
                isStopped: false
            };

            state.timerId = window.setInterval(() => scan(videoId), 450);
            scanners.set(videoId, state);

            return { success: true, message: "Camera started." };
        } catch (error) {
            const errorName = error?.name ? `${error.name}: ` : "";

            return {
                success: false,
                message: `${errorName}${error?.message || "Cannot start camera."}`
            };
        }
    }

    // Collects browser capability details shown in the QR modal. This helps to
    // diagnose tablet issues without opening DevTools on the device.
    function getDiagnostics() {
        const mediaDevices = navigator.mediaDevices;

        return {
            origin: window.location.origin,
            protocol: window.location.protocol,
            host: window.location.host,
            isSecureContext: window.isSecureContext === true,
            hasMediaDevices: !!mediaDevices,
            hasGetUserMedia: !!mediaDevices?.getUserMedia,
            hasEnumerateDevices: !!mediaDevices?.enumerateDevices,
            hasBarcodeDetector: "BarcodeDetector" in window,
            userAgent: navigator.userAgent || ""
        };
    }

    // Lists available video inputs for the camera dropdown. Some browsers hide
    // friendly labels until the user grants camera permission.
    async function listCameras() {
        if (!navigator.mediaDevices?.enumerateDevices) {
            return [];
        }

        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            let cameraIndex = 1;

            return devices
                .filter((device) => device.kind === "videoinput")
                .map((device) => ({
                    deviceId: device.deviceId || "",
                    label: device.label || `Camera ${cameraIndex++}`
                }));
        } catch {
            return [];
        }
    }

    function buildVideoConstraints(deviceId) {
        const video = {
            width: { ideal: 1280 },
            height: { ideal: 720 }
        };

        if (deviceId) {
            video.deviceId = { exact: deviceId };
        } else {
            video.facingMode = { ideal: "environment" };
        }

        return video;
    }

    async function createBarcodeDetector() {
        if (!("BarcodeDetector" in window)) {
            return null;
        }

        try {
            if (BarcodeDetector.getSupportedFormats) {
                const formats = await BarcodeDetector.getSupportedFormats();

                if (!formats.includes("qr_code")) {
                    return null;
                }
            }

            return new BarcodeDetector({ formats: ["qr_code"] });
        } catch {
            return null;
        }
    }

    async function scan(videoId) {
        const state = scanners.get(videoId);
        const video = document.getElementById(videoId);

        if (!state || !video || state.isBusy || state.isStopped || video.readyState < 2) {
            return;
        }

        state.isBusy = true;

        try {
            const nativeText = await detectNative(state, video);

            if (nativeText) {
                await finish(videoId, nativeText);
                return;
            }

            const frame = captureLuminanceFrame(state.canvas, video);

            if (!frame) {
                return;
            }

            const decodedText = await state.dotNetRef.invokeMethodAsync(
                "DecodeQrFrame",
                frame.data,
                frame.width,
                frame.height);

            if (decodedText) {
                await finish(videoId, decodedText);
            }
        } catch {
            // Keep scanning; transient frame decode errors are expected.
        } finally {
            state.isBusy = false;
        }
    }

    async function detectNative(state, video) {
        if (!state.detector) {
            return "";
        }

        try {
            const codes = await state.detector.detect(video);
            return codes?.[0]?.rawValue?.trim() || "";
        } catch {
            return "";
        }
    }

    function captureLuminanceFrame(canvas, video) {
        const sourceWidth = video.videoWidth || 0;
        const sourceHeight = video.videoHeight || 0;

        if (!sourceWidth || !sourceHeight) {
            return null;
        }

        const side = Math.min(sourceWidth, sourceHeight);
        const sourceX = Math.floor((sourceWidth - side) / 2);
        const sourceY = Math.floor((sourceHeight - side) / 2);
        const targetSize = 220;

        canvas.width = targetSize;
        canvas.height = targetSize;

        const context = canvas.getContext("2d", { willReadFrequently: true });

        if (!context) {
            return null;
        }

        context.drawImage(video, sourceX, sourceY, side, side, 0, 0, targetSize, targetSize);

        const rgba = context.getImageData(0, 0, targetSize, targetSize).data;
        const gray = new Uint8Array(targetSize * targetSize);

        for (let i = 0, j = 0; i < rgba.length; i += 4, j++) {
            gray[j] = ((rgba[i] * 299 + rgba[i + 1] * 587 + rgba[i + 2] * 114 + 500) / 1000) & 255;
        }

        return {
            data: bytesToBase64(gray),
            width: targetSize,
            height: targetSize
        };
    }

    async function finish(videoId, text) {
        const state = scanners.get(videoId);

        if (!state || !text) {
            return;
        }

        await state.dotNetRef.invokeMethodAsync("OnQrCodeDetected", text.trim());
        await stop(videoId);
    }

    function bytesToBase64(bytes) {
        let binary = "";
        const chunkSize = 0x8000;

        for (let i = 0; i < bytes.length; i += chunkSize) {
            const chunk = bytes.subarray(i, i + chunkSize);
            binary += String.fromCharCode.apply(null, chunk);
        }

        return btoa(binary);
    }

    async function stop(videoId) {
        const state = scanners.get(videoId);
        const video = document.getElementById(videoId);

        if (state) {
            state.isStopped = true;
            window.clearInterval(state.timerId);

            for (const track of state.stream?.getTracks?.() || []) {
                track.stop();
            }

            scanners.delete(videoId);
        }

        if (video?.srcObject) {
            video.srcObject = null;
        }

        return true;
    }

    return {
        getDiagnostics,
        listCameras,
        start,
        stop
    };
})();
