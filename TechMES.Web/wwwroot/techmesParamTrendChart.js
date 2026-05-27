(function () {
    const charts = new WeakMap();
    const resizeObservers = new WeakMap();
    const zoomStates = new WeakMap();
    const dataRanges = new WeakMap();
    const viewKeys = new WeakMap();
    const zoomHandlers = new WeakSet();
    const touchHandlers = new WeakMap();
    const historyCallbacks = new WeakMap();
    const historyLimits = new WeakMap();
    const historyRequestTimes = new WeakMap();

    function getChart(element) {
        if (!element) {
            return null;
        }

        if (!window.echarts) {
            throw new Error("Apache ECharts is not loaded.");
        }

        let chart = charts.get(element);

        if (!chart || chart.isDisposed?.()) {
            chart = window.echarts.init(element, null, {
                renderer: "canvas",
                useCoarsePointer: true,
                pointerSize: 44
            });
            charts.set(element, chart);
            prepareTouchSurface(element, chart);
            bindResize(element, chart);
            bindZoomState(element, chart);
            bindTouchPanZoom(element, chart);
        }

        return chart;
    }

    function prepareTouchSurface(element, chart) {
        const candidates = [
            element,
            chart.getDom?.(),
            chart.getZr?.()?.painter?.getViewportRoot?.()
        ];

        for (const candidate of candidates) {
            if (!candidate?.style) {
                continue;
            }

            candidate.style.touchAction = "none";
            candidate.style.msTouchAction = "none";
            candidate.style.userSelect = "none";
            candidate.style.webkitUserSelect = "none";
            candidate.style.webkitTapHighlightColor = "rgba(0,0,0,0)";
        }
    }

    function bindResize(element, chart) {
        if (resizeObservers.has(element)) {
            return;
        }

        if (window.ResizeObserver) {
            const observer = new ResizeObserver(() => chart.resize());
            observer.observe(element);
            resizeObservers.set(element, observer);
            return;
        }

        const handler = () => chart.resize();
        window.addEventListener("resize", handler);
        resizeObservers.set(element, { disconnect: () => window.removeEventListener("resize", handler) });
    }

    function bindZoomState(element, chart) {
        if (zoomHandlers.has(element)) {
            return;
        }

        chart.on("datazoom", () => {
            const zoomState = readZoomStateFromOption(element, chart);

            if (zoomState) {
                zoomStates.set(element, zoomState);
                requestHistoryNearEdge(element, zoomState);
            }
        });

        zoomHandlers.add(element);
    }

    function bindTouchPanZoom(element, chart) {
        if (touchHandlers.has(element) || !window.PointerEvent) {
            return;
        }

        const activePointers = new Map();
        let gesture = null;

        const onPointerDown = event => {
            if (!isPanPointer(event)) {
                return;
            }

            activePointers.set(event.pointerId, event);
            element.setPointerCapture?.(event.pointerId);

            gesture = createGesture(element, chart, activePointers);
            showTouchPointer(element, chart, event);
            event.preventDefault();
            event.stopPropagation();
        };

        const onPointerMove = event => {
            if (!activePointers.has(event.pointerId)) {
                return;
            }

            activePointers.set(event.pointerId, event);

            if (!gesture) {
                gesture = createGesture(element, chart, activePointers);
            }

            updateTouchZoom(element, chart, activePointers, gesture);
            if (activePointers.size === 1) {
                showTouchPointer(element, chart, event);
            }
            event.preventDefault();
            event.stopPropagation();
        };

        const onPointerUp = event => {
            if (!activePointers.has(event.pointerId)) {
                return;
            }

            activePointers.delete(event.pointerId);
            element.releasePointerCapture?.(event.pointerId);
            gesture = activePointers.size > 0
                ? createGesture(element, chart, activePointers)
                : null;
            event.preventDefault();
            event.stopPropagation();
        };

        element.addEventListener("pointerdown", onPointerDown, { capture: true, passive: false });
        element.addEventListener("pointermove", onPointerMove, { capture: true, passive: false });
        element.addEventListener("pointerup", onPointerUp, { capture: true, passive: false });
        element.addEventListener("pointercancel", onPointerUp, { capture: true, passive: false });
        element.addEventListener("lostpointercapture", onPointerUp, { capture: true, passive: false });

        touchHandlers.set(element, {
            onPointerDown,
            onPointerMove,
            onPointerUp
        });
    }

    function isPanPointer(event) {
        if (event.pointerType === "mouse") {
            return event.button === 0;
        }

        return event.pointerType === "touch" || event.pointerType === "pen";
    }

    function showTouchPointer(element, chart, event) {
        const point = getElementPoint(element, event);

        chart.dispatchAction({
            type: "updateAxisPointer",
            currTrigger: "mousemove",
            x: point.x,
            y: point.y
        });

        const nearest = findNearestPoint(chart, point);

        if (nearest) {
            chart.dispatchAction({
                type: "showTip",
                seriesIndex: nearest.seriesIndex,
                dataIndex: nearest.dataIndex
            });
            return;
        }

        chart.dispatchAction({
            type: "showTip",
            x: point.x,
            y: point.y
        });
    }

    function getElementPoint(element, event) {
        const rect = element.getBoundingClientRect();

        return {
            x: clamp(event.clientX - rect.left, 0, rect.width),
            y: clamp(event.clientY - rect.top, 0, rect.height)
        };
    }

    function findNearestPoint(chart, point) {
        let axisValue;

        try {
            const converted = chart.convertFromPixel({ xAxisIndex: 0 }, [point.x, point.y]);
            axisValue = Array.isArray(converted) ? converted[0] : converted;
        } catch {
            return null;
        }

        if (!Number.isFinite(axisValue)) {
            return null;
        }

        const seriesItems = chart.getOption?.()?.series || [];
        let nearest = null;

        for (let seriesIndex = 0; seriesIndex < seriesItems.length; seriesIndex++) {
            const data = seriesItems[seriesIndex]?.data || [];

            for (let dataIndex = 0; dataIndex < data.length; dataIndex++) {
                const item = data[dataIndex];
                const itemTime = Array.isArray(item) ? item[0] : item?.value?.[0];

                if (!Number.isFinite(itemTime)) {
                    continue;
                }

                const distance = Math.abs(itemTime - axisValue);

                if (!nearest || distance < nearest.distance) {
                    nearest = {
                        seriesIndex,
                        dataIndex,
                        distance
                    };
                }
            }
        }

        return nearest;
    }

    function createGesture(element, chart, activePointers) {
        const pointers = [...activePointers.values()];
        const zoom = getCurrentZoom(element, chart);

        if (pointers.length >= 2) {
            const first = pointers[0];
            const second = pointers[1];

            return {
                mode: "pinch",
                startDistance: getDistance(first, second),
                startCenterX: (first.clientX + second.clientX) / 2,
                startZoom: zoom
            };
        }

        const pointer = pointers[0];

        return {
            mode: "pan",
            startX: pointer?.clientX ?? 0,
            startZoom: zoom
        };
    }

    function updateTouchZoom(element, chart, activePointers, gesture) {
        const pointers = [...activePointers.values()];

        if (pointers.length >= 2 && gesture.mode === "pinch") {
            updatePinchZoom(element, chart, pointers[0], pointers[1], gesture);
            return;
        }

        if (pointers.length === 1 && gesture.mode === "pan") {
            updatePanZoom(element, chart, pointers[0], gesture);
        }
    }

    function updatePanZoom(element, chart, pointer, gesture) {
        const plot = getPlotRect(element);
        const start = gesture.startZoom.start;
        const end = gesture.startZoom.end;
        const span = end - start;

        if (span >= 99.9 || plot.width <= 1) {
            return;
        }

        const deltaX = pointer.clientX - gesture.startX;
        const deltaPercent = -(deltaX / plot.width) * span;
        const nextStart = start + deltaPercent;
        const nextEnd = end + deltaPercent;

        if (nextStart < -4 && requestHistory(element, "older")) {
            return;
        }

        if (nextEnd > 104 && requestHistory(element, "newer")) {
            return;
        }

        applyZoom(chart, element, clampZoom(nextStart, nextEnd));
    }

    function updatePinchZoom(element, chart, first, second, gesture) {
        const distance = getDistance(first, second);

        if (gesture.startDistance <= 10 || distance <= 10) {
            return;
        }

        const plot = getPlotRect(element);
        const startZoom = gesture.startZoom;
        const startSpan = startZoom.end - startZoom.start;
        const centerRatio = clamp((gesture.startCenterX - plot.left) / plot.width, 0, 1);
        const centerPercent = startZoom.start + centerRatio * startSpan;
        const scale = distance / gesture.startDistance;
        const newSpan = clamp(startSpan / scale, 1, 100);
        const nextStart = centerPercent - centerRatio * newSpan;

        applyZoom(chart, element, clampZoom(nextStart, nextStart + newSpan));
    }

    function getCurrentZoom(element, chart) {
        const range = dataRanges.get(element);
        const state = readZoomStateFromOption(element, chart) || zoomStates.get(element);

        if (range && state) {
            return valuesToPercent(state, range);
        }

        return { start: 0, end: 100 };
    }

    function applyZoom(chart, element, zoom) {
        const range = dataRanges.get(element);
        const valueZoom = percentToValues(zoom, range);

        if (valueZoom) {
            zoomStates.set(element, valueZoom);
        }

        chart.dispatchAction({
            type: "dataZoom",
            batch: [
                { dataZoomIndex: 0, start: zoom.start, end: zoom.end },
                { dataZoomIndex: 1, start: zoom.start, end: zoom.end }
            ]
        });
    }

    function readZoomStateFromOption(element, chart) {
        const range = dataRanges.get(element);
        const zoom = chart.getOption?.()?.dataZoom?.[0];

        if (!zoom || !range) {
            return null;
        }

        if (Number.isFinite(zoom.startValue) && Number.isFinite(zoom.endValue)) {
            return normalizeValueZoom(zoom.startValue, zoom.endValue, range);
        }

        if (Number.isFinite(zoom.start) && Number.isFinite(zoom.end)) {
            return percentToValues({ start: zoom.start, end: zoom.end }, range);
        }

        return null;
    }

    function getRenderZoomState(element, payload, range) {
        const stored = normalizeValueZoom(
            zoomStates.get(element)?.startValue,
            zoomStates.get(element)?.endValue,
            range);

        if (stored) {
            return stored;
        }

        const requested = normalizeValueZoom(
            payload.visibleFromUtcMs,
            payload.visibleToUtcMs,
            range);

        if (requested) {
            return requested;
        }

        return {
            startValue: range.from,
            endValue: range.to
        };
    }

    function percentToValues(zoom, range) {
        if (!range || !Number.isFinite(zoom?.start) || !Number.isFinite(zoom?.end)) {
            return null;
        }

        const span = range.to - range.from;

        if (span <= 0) {
            return null;
        }

        return normalizeValueZoom(
            range.from + span * zoom.start / 100,
            range.from + span * zoom.end / 100,
            range);
    }

    function valuesToPercent(state, range) {
        if (!range || !state) {
            return { start: 0, end: 100 };
        }

        const span = range.to - range.from;

        if (span <= 0) {
            return { start: 0, end: 100 };
        }

        return {
            start: clamp(((state.startValue - range.from) / span) * 100, 0, 100),
            end: clamp(((state.endValue - range.from) / span) * 100, 0, 100)
        };
    }

    function normalizeValueZoom(startValue, endValue, range) {
        if (!range || !Number.isFinite(startValue) || !Number.isFinite(endValue)) {
            return null;
        }

        const rangeSpan = range.to - range.from;

        if (rangeSpan <= 0) {
            return null;
        }

        let start = Math.min(startValue, endValue);
        let end = Math.max(startValue, endValue);
        const span = Math.min(Math.max(end - start, 1000), rangeSpan);

        if (start < range.from) {
            start = range.from;
            end = start + span;
        }

        if (end > range.to) {
            end = range.to;
            start = end - span;
        }

        return {
            startValue: clamp(start, range.from, range.to),
            endValue: clamp(end, range.from, range.to)
        };
    }

    function getPlotRect(element) {
        const rect = element.getBoundingClientRect();
        const left = rect.left + 54;
        const right = rect.right - 22;

        return {
            left,
            width: Math.max(1, right - left)
        };
    }

    function getDistance(first, second) {
        return Math.hypot(
            first.clientX - second.clientX,
            first.clientY - second.clientY);
    }

    function clampZoom(start, end) {
        const span = end - start;

        if (start < 0) {
            end -= start;
            start = 0;
        }

        if (end > 100) {
            start -= end - 100;
            end = 100;
        }

        if (span >= 100) {
            return { start: 0, end: 100 };
        }

        return {
            start: clamp(start, 0, 100),
            end: clamp(end, 0, 100)
        };
    }

    function requestHistory(element, direction) {
        const limits = historyLimits.get(element) || {};

        if (direction === "older" && !limits.canLoadOlder) {
            return false;
        }

        if (direction === "newer" && !limits.canLoadNewer) {
            return false;
        }

        const callback = historyCallbacks.get(element);

        if (!callback?.invokeMethodAsync) {
            return false;
        }

        const now = Date.now();
        const requestTimes = historyRequestTimes.get(element) || {};

        if (now - (requestTimes[direction] || 0) < 900) {
            return false;
        }

        requestTimes[direction] = now;
        historyRequestTimes.set(element, requestTimes);
        callback.invokeMethodAsync("RequestHistoryAsync", direction).catch(() => {});
        return true;
    }

    function requestHistoryNearEdge(element, zoomState) {
        const range = dataRanges.get(element);

        if (!range || !zoomState) {
            return;
        }

        const rangeSpan = range.to - range.from;
        const visibleSpan = zoomState.endValue - zoomState.startValue;

        if (rangeSpan <= 0 || visibleSpan <= 0) {
            return;
        }

        const threshold = Math.max(visibleSpan * 0.35, rangeSpan * 0.06);

        if (zoomState.startValue <= range.from + threshold) {
            requestHistory(element, "older");
        }

        if (zoomState.endValue >= range.to - threshold) {
            requestHistory(element, "newer");
        }
    }

    function render(element, payload, dotNetCallback) {
        const chart = getChart(element);

        if (!chart || !payload) {
            return;
        }

        if (dotNetCallback) {
            historyCallbacks.set(element, dotNetCallback);
        }

        historyLimits.set(element, {
            canLoadOlder: !!payload.canLoadOlder,
            canLoadNewer: !!payload.canLoadNewer
        });

        const from = payload.fromUtcMs;
        const to = payload.toUtcMs;
        const rangeMs = Math.max(0, to - from);
        const range = { from, to };
        const showSeconds = rangeMs <= 5 * 60 * 1000;
        const splitNumber = getTimeSplitNumber(rangeMs);
        const series = (payload.series || []).map(toSeries);
        const viewKey = payload.viewKey || "";

        if (viewKeys.get(element) !== viewKey) {
            viewKeys.set(element, viewKey);
            zoomStates.delete(element);
        }

        dataRanges.set(element, range);
        const zoomState = getRenderZoomState(element, payload, range);

        const option = {
            animation: false,
            color: series.map(x => x.lineStyle.color),
            grid: {
                left: 54,
                right: 22,
                top: 18,
                bottom: 86,
                containLabel: true
            },
            tooltip: {
                trigger: "axis",
                confine: true,
                appendToBody: true,
                axisPointer: {
                    type: "cross",
                    snap: true,
                    label: {
                        show: true,
                        backgroundColor: "#7f7f7f"
                    }
                },
                formatter: params => formatTooltip(params, payload.unit)
            },
            axisPointer: {
                link: [{ xAxisIndex: "all" }],
                snap: true
            },
            xAxis: {
                type: "time",
                min: Number.isFinite(from) ? from : undefined,
                max: Number.isFinite(to) ? to : undefined,
                boundaryGap: false,
                splitNumber,
                axisLabel: {
                    hideOverlap: true,
                    margin: 10,
                    formatter: value => formatTime(value, showSeconds)
                },
                axisLine: { lineStyle: { color: "#9b9b9b" } },
                splitLine: {
                    show: true,
                    lineStyle: { color: "#d9d9d9", width: 1 }
                }
            },
            yAxis: {
                type: "value",
                min: payload.yMin ?? "dataMin",
                max: payload.yMax ?? "dataMax",
                scale: false,
                splitNumber: 5,
                axisLabel: {
                    formatter: value => formatNumber(value)
                },
                axisLine: { show: true, lineStyle: { color: "#9b9b9b" } },
                splitLine: {
                    show: true,
                    lineStyle: { color: "#d4d4d4", width: 1 }
                }
            },
            dataZoom: [
                {
                    type: "inside",
                    xAxisIndex: 0,
                    filterMode: "none",
                    startValue: zoomState.startValue,
                    endValue: zoomState.endValue,
                    throttle: 40,
                    zoomOnMouseWheel: true,
                    moveOnMouseMove: true,
                    moveOnMouseWheel: true,
                    preventDefaultMouseMove: true
                },
                {
                    type: "slider",
                    xAxisIndex: 0,
                    filterMode: "none",
                    startValue: zoomState.startValue,
                    endValue: zoomState.endValue,
                    height: 20,
                    bottom: 34,
                    brushSelect: true
                }
            ],
            legend: {
                type: "scroll",
                bottom: 0,
                icon: "roundRect"
            },
            series
        };

        chart.setOption(option, {
            notMerge: false,
            lazyUpdate: true,
            replaceMerge: ["series"]
        });
    }

    function toSeries(source) {
        const color = source.color || "#2f80ed";

        return {
            name: source.name || "",
            type: "line",
            data: source.data || [],
            showSymbol: false,
            connectNulls: false,
            sampling: "lttb",
            lineStyle: {
                color,
                width: 1.6
            },
            itemStyle: {
                color
            },
            areaStyle: {
                color: source.fill || toRgba(color, 0.18),
                opacity: 1
            },
            emphasis: {
                focus: "series"
            }
        };
    }

    function formatTooltip(params, unit) {
        const items = Array.isArray(params) ? params : [params];
        const first = items[0];
        const time = first?.data?.[0] ?? first?.value?.[0];
        const lines = [`<div class="param-echart-tooltip-time">${formatDateTimeCompact(time)}</div>`];

        for (const item of items) {
            const data = item.data || item.value || [];
            const value = data[1];
            const suffix = unit ? ` ${escapeHtml(unit)}` : "";

            lines.push(
                `<div class="param-echart-tooltip-row">${item.marker || ""}` +
                `<span>${escapeHtml(item.seriesName || "")}</span>` +
                `<strong>${formatNumber(value)}${suffix}</strong></div>`);
        }

        return lines.join("");
    }

    function formatTime(value, showSeconds) {
        const date = new Date(value);

        if (Number.isNaN(date.getTime())) {
            return "";
        }

        const result = `${pad2(date.getHours())}:${pad2(date.getMinutes())}`;
        return showSeconds
            ? `${result}:${pad2(date.getSeconds())}`
            : result;
    }

    function formatDateTime(value, showSeconds) {
        const date = new Date(value);

        if (Number.isNaN(date.getTime())) {
            return "";
        }

        const datePart = date.toLocaleDateString();
        return `${datePart} ${formatTime(value, showSeconds)}`;
    }

    function formatDateTimeCompact(value) {
        const date = new Date(value);

        if (Number.isNaN(date.getTime())) {
            return "";
        }

        return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())} ${formatTime(value, true)}`;
    }

    function formatNumber(value) {
        const number = Number(value);

        if (!Number.isFinite(number)) {
            return "-";
        }

        return number.toLocaleString(undefined, { maximumFractionDigits: 3 });
    }

    function getTimeSplitNumber(rangeMs) {
        const minutes = rangeMs / 60000;

        if (minutes <= 5) {
            return 5;
        }

        if (minutes <= 30) {
            return 6;
        }

        if (minutes <= 120) {
            return 8;
        }

        return 10;
    }

    function pad2(value) {
        return String(value).padStart(2, "0");
    }

    function clamp(value, min, max) {
        return Math.min(Math.max(value, min), max);
    }

    function toRgba(color, alpha) {
        if (!/^#[0-9a-f]{6}$/i.test(color || "")) {
            return color || `rgba(47, 128, 237, ${alpha})`;
        }

        const r = Number.parseInt(color.slice(1, 3), 16);
        const g = Number.parseInt(color.slice(3, 5), 16);
        const b = Number.parseInt(color.slice(5, 7), 16);

        return `rgba(${r}, ${g}, ${b}, ${alpha})`;
    }

    function escapeHtml(value) {
        return String(value)
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll("\"", "&quot;")
            .replaceAll("'", "&#039;");
    }

    function dispose(element) {
        const touchHandler = touchHandlers.get(element);

        if (touchHandler) {
            element.removeEventListener("pointerdown", touchHandler.onPointerDown, true);
            element.removeEventListener("pointermove", touchHandler.onPointerMove, true);
            element.removeEventListener("pointerup", touchHandler.onPointerUp, true);
            element.removeEventListener("pointercancel", touchHandler.onPointerUp, true);
            element.removeEventListener("lostpointercapture", touchHandler.onPointerUp, true);
            touchHandlers.delete(element);
        }

        const observer = resizeObservers.get(element);

        if (observer) {
            observer.disconnect();
            resizeObservers.delete(element);
        }

        const chart = charts.get(element);

        if (chart && !chart.isDisposed?.()) {
            chart.dispose();
        }

        charts.delete(element);
        zoomStates.delete(element);
        dataRanges.delete(element);
        viewKeys.delete(element);
        zoomHandlers.delete(element);
        historyCallbacks.delete(element);
        historyLimits.delete(element);
        historyRequestTimes.delete(element);
    }

    window.techMesParamTrendChart = {
        render,
        dispose
    };
})();
