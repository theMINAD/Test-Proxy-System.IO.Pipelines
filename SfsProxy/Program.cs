using PipeTests.Proxy;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace SfsProxy
{
    class Program
    {
        static List<ProxyClient> _clients = new List<ProxyClient>();

        static void Main(string[] args)
        {
            using (ProxyServer server = new ProxyServer())
            {
                server.OnClientLocalMessage += OnLocalClientMessage;
                server.OnClientRemoteMessage += OnRemoteClientMessage;
                server.OnClientStateChanged += OnClientStatusChange;
                server.OnTick += OnTick;

                server.Bind
                (
                    new IPEndPoint(IPAddress.Any, 9933),
                    new IPEndPoint(IPAddress.Parse("my.sfs.game.ip"), 9933)
                );

                Console.WriteLine("PRESS Q TO EXIT...");
                while (Console.ReadKey(true).Key != ConsoleKey.Q) continue;
            }
        }

        static void OnLocalClientMessage(ProxyClient client, string message)
        {
            Console.WriteLine("LOCAL: {0}, {1}", message, Thread.CurrentThread.ManagedThreadId);
            client.SendRemote(message);
        }

        static void OnRemoteClientMessage(ProxyClient client, string message)
        {
            Console.WriteLine("REMOTE: {0}, {1}", message, Thread.CurrentThread.ManagedThreadId);
            client.SendLocal(message);
        }

        static void OnClientStatusChange(ProxyClient client, bool connected)
        {
            Console.WriteLine("CLIENT STATUS CHANGED!!");

            if (connected) _clients.Add(client);
            else _clients.Remove(client);
        }

        static bool _firstTick = true;
        static long _lastTick = 0;
        static void OnTick(long msElapsed)
        {
            if (_firstTick)
            {
                Console.WriteLine("FIRST TICK FROM EVENTLOOP {0}", Thread.CurrentThread.ManagedThreadId);
                _firstTick = false;
            }

            if (msElapsed > _lastTick + 1000)
            {
                foreach (ProxyClient client in _clients)
                    client.SendLocal("%xt%msg%1%:v)%");

                _lastTick = msElapsed;
            }
        }
    }
}
