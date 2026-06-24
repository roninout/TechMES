namespace TechMES.Maintenance.Models;

/// <summary>
/// Профиль будущей операторской безопасности.
/// Пока WEB/Runtime продолжают работать по текущей схеме, но Maintenance уже показывает,
/// какие Windows-группы и write-флаги считаются правильной серверной конфигурацией.
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// Включать ли в Maintenance проверки профиля Windows-пользователей.
    /// </summary>
    public bool WindowsUsersEnabled { get; set; } = true;

    /// <summary>
    /// Windows-группы, которым в будущем будет разрешен write-режим.
    /// </summary>
    public string WriteGroups { get; set; } = "TechMES-Operators;TechMES-Engineers";

    /// <summary>
    /// Требовать ли подтверждение write-команд на WEB-стороне.
    /// </summary>
    public bool RequireWriteConfirmation { get; set; }

    /// <summary>
    /// Считать SCADA audit через SaveActionOperators обязательной частью write-flow.
    /// </summary>
    public bool RequireScadaAudit { get; set; } = true;
}
