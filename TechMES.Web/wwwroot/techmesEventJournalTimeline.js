// Наблюдатели привязаны к DOM-элементам таблиц и отключаются при уходе со страницы.
const observers = new WeakMap();

export function observe(element, dotNetReference, itemWidth, maxItems) {
    if (!element) {
        return;
    }

    disconnect(element);

    const state = {
        observer: null,
        frameId: null,
        lastCount: 0
    };

    function measure() {
        state.frameId = null;

        if (!observers.has(element)) {
            return;
        }

        const width = element.getBoundingClientRect().width;

        if (width <= 0) {
            return;
        }

        const count = Math.max(1, Math.min(maxItems, Math.floor(width / itemWidth)));

        if (count === state.lastCount) {
            return;
        }

        state.lastCount = count;

        // Blazor получает только новое число элементов; данные из БД здесь не запрашиваются.
        dotNetReference.invokeMethodAsync("OnTimelineCapacityChanged", count)
            .catch(() => disconnect(element));
    }

    function scheduleMeasure() {
        if (state.frameId !== null) {
            return;
        }

        state.frameId = requestAnimationFrame(measure);
    }

    state.observer = new ResizeObserver(scheduleMeasure);
    observers.set(element, state);
    state.observer.observe(element);
    scheduleMeasure();
}

export function disconnect(element) {
    const state = observers.get(element);

    if (!state) {
        return;
    }

    state.observer.disconnect();

    if (state.frameId !== null) {
        cancelAnimationFrame(state.frameId);
    }

    observers.delete(element);
}