using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace PipeTests.Proxy
{
    public class ProxyClient
    {
        #region Members
        public int _disposed = 0;
        #endregion

        #region Properties
        public ProxyServer Server { get; }
        public object UserToken { get; set; }

        internal PipedTcpClient LocalClient { get; }
        internal PipedTcpClient RemoteClient { get; }

        public bool Disposed { get => Volatile.Read(ref _disposed) == 1; }
        #endregion

        #region Constructor
        internal ProxyClient(ProxyServer server)
        {
            Server = server;
            LocalClient = new PipedTcpClient(this, PipedTcpClient.ClientType.Local);
            RemoteClient = new PipedTcpClient(this, PipedTcpClient.ClientType.Remote);
        }
        #endregion

        #region Run
        internal Task Run()
        {
            return Task.WhenAll(LocalClient.Run(), RemoteClient.Run());
        }
        #endregion

        #region Send
        public void SendLocal(Span<byte> buffer)
        {
            LocalClient.Send(buffer);
        }

        public void SendRemote(Span<byte> buffer)
        {
            RemoteClient.Send(buffer);
        }

        public void SendLocal(string message)
        {
            LocalClient.Send(message);
        }

        public void SendRemote(string message)
        {
            RemoteClient.Send(message);
        }
        #endregion

        #region IDisposable
        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                void PerformDispose()
                {
                    Server.PublishClientState(this, false);
                    LocalClient.Dispose();
                    RemoteClient.Dispose();
                }

                if (Server.IsWorkThread(Thread.CurrentThread))
                {
                    PerformDispose();
                }
                else
                {
                    try
                    {
                        Server.Schedule((_) => PerformDispose(), null);
                    }
                    catch (Exception)
                    {
                        Task.Factory.StartNew(PerformDispose);
                    }
                }
            }

            GC.SuppressFinalize(this);
        }
        #endregion

        #region PipedTcpClient
        internal class PipedTcpClient : IDisposable
        {
            #region Properties
            internal ProxyClient Client { get; }
            internal ClientType Type { get; }

            internal Socket Socket { get; }

            internal PipeReader RecvReader { get; }
            internal PipeWriter RecvWriter { get; }
            internal PipeReader SendReader { get; }
            internal PipeWriter SendWriter { get; }

            internal bool Disposed { get; private set; }
            #endregion

            #region Constructor
            internal PipedTcpClient(ProxyClient client, ClientType type)
            {
                Client = client;
                Type = type;

                Socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

                Pipe recvPipe = client.Server.NewPipe();
                Pipe sendPipe = client.Server.NewPipe();

                RecvReader = recvPipe.Reader;
                SendReader = sendPipe.Reader;
                RecvWriter = recvPipe.Writer;
                SendWriter = sendPipe.Writer;
            }
            #endregion

            #region Run
            internal Task Run()
            {
                return Task.WhenAll(HandleRecvWrite(), HandleRecvRead(), HandleSend());
            }

            private async Task HandleRecvRead()
            {
                const byte delimiter = (byte)'\x00';

                while (!Disposed)
                {
                    var result = await RecvReader.ReadAsync().ConfigureAwait(false);

                    if (!result.IsCompleted)
                    {
                        ReadOnlySequence<byte> buffer = result.Buffer;
                        SequencePosition? position = null;

                        do
                        {
                            position = buffer.PositionOf(delimiter);

                            if (position != null)
                            {
                                var buffers = buffer.Slice(0, position.Value);
                                var message = Utils.GetAsciiString(buffers);

                                if (Type == ClientType.Local)
                                    Client.Server.PublishClientLocalMessage(Client, message);
                                else
                                    Client.Server.PublishClientRemoteMessage(Client, message);

                                buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                            }
                        }
                        while (position != null);

                        RecvReader.AdvanceTo(buffer.Start, buffer.End);
                    }
                    else
                    {
                        break; // Writer is completed.
                    }
                }

                Client.Dispose();
            }

            private async Task HandleRecvWrite()
            {
                var ct = Client.Server.CancelToken;

                while (!Disposed)
                {
                    var buffer = RecvWriter.GetMemory();

                    try
                    {
                        int bytesRecv = await Socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);

                        RecvWriter.Advance(bytesRecv);

                        FlushResult result = await RecvWriter.FlushAsync().ConfigureAwait(false);
                        if (result.IsCompleted)
                        {
                            break; // The Reader has completed.
                        }
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }

                Client.Dispose();
            }

            private async Task HandleSend()
            {
                var ct = Client.Server.CancelToken;
                var st = new NetworkStream(Socket, false);

                while (!Disposed)
                {
                    var result = await SendReader.ReadAsync().ConfigureAwait(false);

                    if (!result.IsCompleted)
                    {
                        try
                        {
                            foreach (var buffer in result.Buffer)
                            {
                                await st.WriteAsync(buffer, ct).ConfigureAwait(false);
                            }

                            SendReader.AdvanceTo(result.Buffer.End, result.Buffer.End);
                        }
                        catch (Exception)
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                st.Dispose();
                Client.Dispose();
            }
            #endregion

            #region Send
            internal ValueTask<FlushResult> Send(Span<byte> buf)
            {
                if (Client.Server.IsWorkThread(Thread.CurrentThread))
                {
                    int length = buf.Length + 1;
                    var buffer = SendWriter.GetMemory(length);
                    buf.CopyTo(buffer.Span);
                    buffer.Span[buf.Length] = (byte)'\x00'; 

                    SendWriter.Advance(length);
                    return SendWriter.FlushAsync();
                }
                else
                {
                    throw new NotSupportedException();
                }
            }

            internal ValueTask<FlushResult> Send(string str)
            {
                if (Client.Server.IsWorkThread(Thread.CurrentThread))
                {
                    var length = Encoding.ASCII.GetByteCount(str) + 1;
                    var buffer = SendWriter.GetMemory(length);
                    Encoding.ASCII.GetBytes(str, buffer.Span);
                    buffer.Span[length-1] = (byte)'\x00';

                    SendWriter.Advance(length);
                    return SendWriter.FlushAsync();
                }
                else
                {
                    throw new NotSupportedException();
                }
            }
            #endregion

            #region IDisposable
            public void Dispose()
            {
                Dispose(true);
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!Disposed)
                {
                    Socket.Shutdown(SocketShutdown.Both);

                    RecvReader.Complete();
                    RecvWriter.Complete();
                    SendReader.Complete();
                    SendWriter.Complete();

                    Socket.Dispose();

                    Disposed = true;
                }

                GC.SuppressFinalize(this);
            }
            #endregion

            #region Types
            internal enum ClientType
            {
                Local,
                Remote,
            }
            #endregion
        }
        #endregion
    }
}
