using System.Net.WebSockets;
namespace Jibo.Cloud.Api.Hosting;
// The coordinator owns the inner socket. All send paths, including registry pushes,
// go through the same connection/entitlement guard.
internal sealed class BorrowedAuthorizedWebSocket(WebSocket inner,Func<CancellationToken,Task>? guard):WebSocket
{
 public override WebSocketCloseStatus? CloseStatus=>inner.CloseStatus;public override string? CloseStatusDescription=>inner.CloseStatusDescription;
 public override WebSocketState State=>inner.State;public override string? SubProtocol=>inner.SubProtocol;
 public override void Abort()=>inner.Abort();public override void Dispose(){}
 public override Task CloseAsync(WebSocketCloseStatus s,string? d,CancellationToken c)=>inner.CloseAsync(s,d,c);
 public override Task CloseOutputAsync(WebSocketCloseStatus s,string? d,CancellationToken c)=>inner.CloseOutputAsync(s,d,c);
 public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b,CancellationToken c)=>inner.ReceiveAsync(b,c);
 public override async Task SendAsync(ArraySegment<byte> b,WebSocketMessageType t,bool e,CancellationToken c){if(guard is not null)await guard(c);await inner.SendAsync(b,t,e,c);}
 public override async ValueTask SendAsync(ReadOnlyMemory<byte> b,WebSocketMessageType t,bool e,CancellationToken c=default){if(guard is not null)await guard(c);await inner.SendAsync(b,t,e,c);}
}
