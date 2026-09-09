(() => {
    "use strict";

    if (window.techmesFormulaEditorInstalled) return;
    window.techmesFormulaEditorInstalled = true;

    const rootSelector = "[data-formula-editor]";
    const tokenSelector = "[data-formula-template]";
    const expressionSelector = "textarea.formula-expression";
    const pendingDrops = new WeakSet();

    let dragged = null;

    function getToken(target) {
        return target instanceof Element ? target.closest(tokenSelector) : null;
    }

    function getExpression(root) {
        const expression = root?.querySelector(expressionSelector);
        return expression && !expression.disabled && !expression.readOnly ? expression : null;
    }

    function notifyChange(expression) {
        // RadzenTextArea вызывает существующий ChangeExpression через событие change.
        expression.dispatchEvent(new Event("change", { bubbles: true }));
    }

    document.addEventListener("dragstart", event => {
        const token = getToken(event.target);

        if (!token || token.getAttribute("draggable") !== "true") return;

        const root = token.closest(rootSelector);

        if (!getExpression(root) || !event.dataTransfer) return;

        dragged = { root };

        event.dataTransfer.effectAllowed = "copy";
        event.dataTransfer.setData("text/plain", token.dataset.formulaTemplate);
    });

    document.addEventListener("dragover", event => {
        if (!dragged || event.target !== getExpression(dragged.root)) return;

        event.preventDefault();

        if (event.dataTransfer)
            event.dataTransfer.dropEffect = "copy";
    });

    document.addEventListener("drop", event => {
        if (!dragged) return;

        const expression = getExpression(dragged.root);

        if (event.target !== expression) return;

        // Вставку в точку отпускания выполняет сам браузер как обычный text/plain drop.
        // Не отменяем default: сохраняем штатное позиционирование курсора и undo.
        pendingDrops.add(expression);
        setTimeout(() => pendingDrops.delete(expression), 0);
    });

    document.addEventListener("input", event => {
        const expression = event.target;

        if (!pendingDrops.has(expression)) return;

        pendingDrops.delete(expression);
        notifyChange(expression);
    });

    document.addEventListener("dragend", () => {
        dragged = null;
    });

    document.addEventListener("click", event => {
        const token = getToken(event.target);

        if (!token) return;

        const expression = getExpression(token.closest(rootSelector));

        if (!expression) return;

        // Берём текущий текст DOM, чтобы не потерять ещё не завершённый ручной ввод.
        const template = token.dataset.formulaTemplate;

        expression.setRangeText(template, expression.selectionStart, expression.selectionEnd, "end");
        expression.focus();
        expression.dispatchEvent(new Event("input", { bubbles: true }));

        notifyChange(expression);
    });
})();