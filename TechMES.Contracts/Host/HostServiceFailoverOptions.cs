namespace TechMES.Contracts.Host;

public sealed class HostServiceFailoverOptions
{
    public bool Enabled { get; set; }
    public string PreferredComputerName { get; set; } = "";
    public int FailureThreshold { get; set; } = 4;
    public int RecoveryThreshold { get; set; } = 12;

    public void Validate(bool controlEnabled, bool preferred)
    {
        if (!Enabled)
            return;

        if (!controlEnabled)
            throw new InvalidOperationException("Для переключения при отказе Host включите защищённый endpoint Host.");

        if (FailureThreshold is < 2 or > 60 || RecoveryThreshold is < 2 or > 60)
            throw new InvalidOperationException("Пороги отказа и восстановления Host должны быть от 2 до 60.");

        if (!preferred && !string.IsNullOrEmpty(PreferredComputerName)
            && (Uri.CheckHostName(PreferredComputerName) != UriHostNameType.Dns || !PreferredComputerName.Contains('.')
                || PreferredComputerName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-')))
            throw new InvalidOperationException("На резерве укажите полное DNS-имя координатора, например srv1.plant.local.");
    }
}
