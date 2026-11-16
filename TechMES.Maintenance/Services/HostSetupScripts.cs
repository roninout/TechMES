using TechMES.Contracts.Host;

namespace TechMES.Maintenance.Services;

/// <summary>Проверенные параметры PowerShell: адреса проходят строгий IPv4 разбор до подстановки.</summary>
public static class HostSetupScripts
{
    /// <summary>Создаёт машинный сертификат с SAN IP и двумя EKU; приватный ключ не экспортируется.</summary>
    public static string Certificate(string nodeAddress)
    {
        if (!HostNetworkPolicy.TryAddress(nodeAddress, out var node)) throw new ArgumentException("Нужен IPv4 узла.");
        return "$ErrorActionPreference='Stop'; $cert=New-SelfSignedCertificate -Type Custom -Subject 'CN=TechMES Host' -FriendlyName 'TechMES Host node' -CertStoreLocation 'Cert:\\LocalMachine\\My' -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -Provider 'Microsoft Software Key Storage Provider' -KeyUsage DigitalSignature,KeyEncipherment -NotAfter (Get-Date).AddYears(3) -TextExtension @('2.5.29.17={text}IPAddress=" + node + "','2.5.29.37={text}1.3.6.1.5.5.7.3.1,1.3.6.1.5.5.7.3.2'); $sha=[Security.Cryptography.SHA256]::Create(); try { [BitConverter]::ToString($sha.ComputeHash($cert.RawData)).Replace('-','') } finally { $sha.Dispose() }";
    }

    /// <summary>Возвращает правило PersistentStore только для двух заданных IP и порта Host.</summary>
    public static string Firewall(HostSharedAddressSettings shared, HostControlSettings control, int healthPort)
    {
        control.Validate(healthPort);
        if (!control.Enabled || !HostNetworkPolicy.TryAddress(shared.NodeAddress, out var node) || !HostNetworkPolicy.TryAddress(shared.PeerAddress, out var peer) || node.Equals(peer)) throw new ArgumentException("Нужны два разных IPv4 узлов и включённый HostControl.");
        return $"$ErrorActionPreference='Stop'; $hostRule=@{{Name='TechMES-Host-Control';Direction='Inbound';Action='Allow';Protocol='TCP';LocalAddress='{node}';LocalPort={control.Port};RemoteAddress=@('{node}','{peer}');Profile='Any';Enabled='True';PolicyStore='PersistentStore'}}; if(Get-NetFirewallRule -Name $hostRule.Name -PolicyStore PersistentStore -ErrorAction SilentlyContinue) {{Set-NetFirewallRule @hostRule}} else {{New-NetFirewallRule @hostRule -DisplayName 'TechMES Host control HTTPS' | Out-Null}}";
    }

    /// <summary>Ожидает состояния SCM и возвращает PID нового процесса Host.</summary>
    public static string Restart() => "$ErrorActionPreference='Stop'; $svc=Get-Service -Name 'TechMES.Host'; if($svc.Status -ne 'Stopped') { if($svc.Status -ne 'StopPending') { Stop-Service -Name 'TechMES.Host' -ErrorAction Stop }; $svc.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(20)) }; Start-Service -Name 'TechMES.Host' -ErrorAction Stop; (Get-Service -Name 'TechMES.Host').WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(20)); (Get-CimInstance Win32_Service -Filter \"Name='TechMES.Host'\").ProcessId";
}
