namespace Jibo.Cloud.Application.Services;
public sealed class RuntimeAccessSession(IRuntimeAccessClient client,RuntimePairingCredentialEvidence credential,byte[] session,string operation,
 Func<CancellationToken,Task<bool>> authority,TimeProvider? timeProvider=null)
{
 private readonly TimeProvider clock=timeProvider??TimeProvider.System;
 private readonly byte[] binding=session.ToArray();private readonly SemaphoreSlim gate=new(1,1);
 private DateTimeOffset until;private long started;private TimeSpan duration;private int failed;
 private bool Current()=>Volatile.Read(ref failed)==0&&until>clock.GetUtcNow()&&clock.GetElapsedTime(started)<duration;
 public async Task<bool> CheckAsync(CancellationToken cancellation)
 {
  cancellation.ThrowIfCancellationRequested();if(Volatile.Read(ref failed)!=0)return false;
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(6));
  var acquired=false;
  try
  {
   await gate.WaitAsync(deadline.Token);acquired=true;if(Current())return true;
   if(!client.IsReady||binding.Length!=32||!await authority(deadline.Token).WaitAsync(TimeSpan.FromSeconds(2),deadline.Token))return Fail();
   var requestStarted=clock.GetTimestamp();var requestedAt=clock.GetUtcNow();
   var expires=await client.AuthorizeAsync(credential,binding,operation,deadline.Token).WaitAsync(TimeSpan.FromSeconds(2),deadline.Token);
   if(expires is null||expires<=clock.GetUtcNow()||expires>clock.GetUtcNow().AddSeconds(5)||!await authority(deadline.Token).WaitAsync(TimeSpan.FromSeconds(2),deadline.Token))return Fail();
   deadline.Token.ThrowIfCancellationRequested();until=expires.Value<requestedAt.AddSeconds(5)?expires.Value:requestedAt.AddSeconds(5);started=requestStarted;duration=until-requestedAt;
   return Current()||Fail();
  }
  catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
  catch(Exception){deadline.Cancel();return Fail();}
  finally{if(acquired)gate.Release();}
 }
 private bool Fail(){Interlocked.Exchange(ref failed,1);return false;}
}
