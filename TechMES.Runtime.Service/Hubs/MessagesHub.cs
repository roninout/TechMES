using Microsoft.AspNetCore.SignalR;

namespace TechMES.Runtime.Service.Hubs;

/// <summary>
/// SignalR Hub для live-обновлений Messages.
/// 
/// Важно:
/// Hub сам не хранит данные и не работает с БД.
/// Он только доставляет события подключённым WEB-клиентам.
/// </summary>
public sealed class MessagesHub : Hub
{
}