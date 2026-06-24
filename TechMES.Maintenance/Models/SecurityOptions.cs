namespace TechMES.Maintenance.Models;

/// <summary>
/// Профиль операторской безопасности.
/// Maintenance использует его как эталон для Runtime/Web appsettings, где включается Windows write-enforcement.
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// Включать ли в Maintenance проверки профиля Windows-пользователей.
    /// </summary>
    public bool WindowsUsersEnabled { get; set; } = true;

    /// <summary>
    /// Windows-группы, которым разрешен write-режим.
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
