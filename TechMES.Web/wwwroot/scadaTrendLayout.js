// Только геометрия боковой панели. График и данные полностью принадлежат Radzen.
export function observe(root) {
    const layout = root.closest('.param-graph-layout, .param-tune-graph-layout');

    if (!layout) {
        return { dispose() { } };
    }

    let frame = 0;
    let disposed = false;

    // Измеряем реальную область построения, исключая toolbar, легенду и Navigator.
    function measure() {
        frame = 0;

        if (disposed || !root.isConnected) {
            return;
        }

        const path = root.querySelector('.rz-chart > svg clipPath path');

        if (!path) {
            return;
        }

        const matrix = path.getScreenCTM();

        if (!matrix) {
            return;
        }

        const box = path.getBBox();

        if (box.height <= 0) {
            return;
        }

        const first = new DOMPoint(box.x, box.y).matrixTransform(matrix);
        const last = new DOMPoint(box.x + box.width, box.y + box.height).matrixTransform(matrix);
        const bounds = layout.getBoundingClientRect();

        const values = {
            '--param-chart-plot-top': Math.max(0, Math.min(first.y, last.y) - bounds.top),
            '--param-chart-plot-bottom': Math.max(0, bounds.bottom - Math.max(first.y, last.y))
        };

        for (const [name, value] of Object.entries(values)) {
            const pixels = `${value.toFixed(2)}px`;

            if (layout.style.getPropertyValue(name) !== pixels) {
                layout.style.setProperty(name, pixels);
            }
        }
    }

    // Несколько DOM-событий объединяем в одно измерение на кадр.
    function schedule() {
        if (!disposed && !frame) {
            frame = requestAnimationFrame(measure);
        }
    }

    const resize = new ResizeObserver(schedule);
    const mutation = new MutationObserver(schedule);

    resize.observe(root);
    resize.observe(layout);
    mutation.observe(root, { childList: true, subtree: true, attributes: true });

    schedule();

    return {
        dispose() {
            disposed = true;
            cancelAnimationFrame(frame);
            resize.disconnect();
            mutation.disconnect();

            layout.style.removeProperty('--param-chart-plot-top');
            layout.style.removeProperty('--param-chart-plot-bottom');
        }
    };
}