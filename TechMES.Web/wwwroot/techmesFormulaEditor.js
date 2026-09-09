(() => {
    "use strict";

    if (window.techmesFormulaEditorInstalled) return;
    window.techmesFormulaEditorInstalled = true;

    const rootSelector = "[data-formula-editor]";
    const tokenSelector = "[data-formula-template]";
    const expressionSelector = "textarea.formula-expression";

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