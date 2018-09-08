using System;
using System.IO.Pipelines;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Buffers;

namespace PipeTests.Proxy
{
    public class ProxyServer : PipeScheduler, IDisposable
    {
        #region Memers
        private readonly PipeOptions _pipeOptions;

        private readonly BlockingCollection<Work> _workQueue = new BlockingCollection<Work>();
        private readonly CancellationTokenSource _workCancel = new CancellationTokenSource();
        private readonly Thread _workThread;

        private int _tickSpeed = 1;
        private int _disposed = 0;
        #endregion

        #region Properties
        public int TickSpeed { get => Volatile.Read(ref _tickSpeed); set => Volatile.Write(ref _tickSpeed, value); }
        public bool Disposing { get => Volatile.Read(ref _disposed) == 1; }
        public bool Disposed { get => Volatile.Read(ref _disposed) == 2; }
        public CancellationToken CancelToken => _workCancel.Token;
        #endregion

        #region Delegates
        public delegate void ClientMessage(ProxyClient client, string message);
        public delegate void ClientStateChanged(ProxyClient client, bool connected);
        public delegate void Tick(long msElapsed);
        #endregion

        #region Events
        public event ClientMessage OnClientLocalMessage;
        public event ClientMessage OnClientRemoteMessage;
        public event ClientStateChanged OnClientStateChanged;
        public event Tick OnTick;
        #endregion

        #region Constructor
        public ProxyServer()
        {
            _pipeOptions = new PipeOptions(readerScheduler: this, writerScheduler: this, useSynchronizationContext: false);

            _workThread = new Thread(Scheduler);
            _workThread.Start();
        }
        #endregion

        #region PipeScheduler
        public override void Schedule(Action<object> action, object state)
        {
            _workQueue.Add(new Work(action, state));
        }

        private void Scheduler()
        {
            Stopwatch ticker = Stopwatch.StartNew();

            while (!_workCancel.IsCancellationRequested)
            {
                while (_workQueue.TryTake(out Work work, TickSpeed)) work.Execute();

                OnTick?.Invoke(ticker.ElapsedMilliseconds);
            }
        }

        internal bool IsWorkThread(Thread thread) => _workThread == thread;

        internal struct Work
        {
            private readonly Action<object> _action;
            private readonly object _state;

            internal Work(Action<object> action, object state)
            {
                _action = action;
                _state = state;
            }

            internal void Execute()
            {
                _action(_state);
            }
        }
        #endregion

        #region PipeFactory
        internal Pipe NewPipe()
        {
            return new Pipe(_pipeOptions);
        }
        #endregion

        #region ClientEventPublish
        internal void PublishClientLocalMessage(ProxyClient client, string message)
        {
            OnClientLocalMessage?.Invoke(client, message);
        }

        internal void PublishClientRemoteMessage(ProxyClient client, string message)
        {
            OnClientRemoteMessage?.Invoke(client, message);
        }

        internal void PublishClientState(ProxyClient client, bool connected)
        {
            OnClientStateChanged?.Invoke(client, connected);
        }
        #endregion

        #region Bind
        public void Bind(EndPoint local, EndPoint remote)
        {
            void BindSocket()
            {
                Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(local);
                socket.Listen(128);

                Task.Factory.StartNew(async () =>
                {
                    while (!_workCancel.IsCancellationRequested)
                    {
                        ProxyClient proxyClient = new ProxyClient(this);

                        await socket.AcceptAsync(proxyClient.LocalClient.Socket).ConfigureAwait(false);
                        await proxyClient.RemoteClient.Socket.ConnectAsync(remote).ConfigureAwait(false);

                        Schedule((c) =>
                        {
                            var client = (ProxyClient)c;

                            PublishClientState(client, true);

                            client.Run();
                        }, proxyClient);
                    }

                    socket.Dispose();
                });
            }

            if (IsWorkThread(Thread.CurrentThread))
            {
                BindSocket();
            }
            else
            {
                Schedule((_) => BindSocket(), null);
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
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                void PerformDispose()
                {
                    _workCancel.Cancel();
                    _workQueue.CompleteAdding();
                    _workThread.Join();

                    _workCancel.Dispose();
                    _workQueue.Dispose();

                    Volatile.Write(ref _disposed, 2);
                }

                //.Join() would cause deadlock in _workThread.
                if (Thread.CurrentThread != _workThread)
                {
                    PerformDispose();
                }
                else
                {
                    Task.Factory.StartNew(PerformDispose, TaskCreationOptions.LongRunning);
                }
            }

            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
