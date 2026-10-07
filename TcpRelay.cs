using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Silverbullet
{
    /**
     * Minimalistic TCP relay socket. Accepts incoming connections on listenerIp:listenerPort and forwards each one to
     * targetHost:targetPort.
     */
    public sealed class TcpRelay : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _targetHost;
        private readonly int _targetPort;
        private Thread _acceptThread;
        private volatile bool isRunning;

        public TcpRelay(IPAddress listenIp, int listenPort, string targetHost, int targetPort)
        {
            _listener = new TcpListener(listenIp, listenPort);
            _targetHost = targetHost;
            _targetPort = targetPort;
        }

        public void Start()
        {
            _listener.Start();
            isRunning = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "TcpRelay";
            _acceptThread.Start();
            Write("relaying " + _listener.LocalEndpoint + " -> " + _targetHost + ":" + _targetPort);
        }

        public void Dispose()
        {
            isRunning = false;
            _listener.Stop();
            _acceptThread?.Join(2000);
        }

        void AcceptLoop()
        {
            while (isRunning)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                ThreadPool.QueueUserWorkItem(Connect, client);
            }
        }

        private void Connect(object state)
        {
            var client = (TcpClient)state;
            var from = GetEndpoint(client);
            var upstream = new TcpClient();
            try
            {
                upstream.Connect(_targetHost, _targetPort);   // resolves hostnames too
            }
            catch (SocketException e)
            {
                Write(from + ": cannot reach " + _targetHost + ":" + _targetPort + " (" + e.Message + ")");
                client.Close();
                upstream.Close();
                return;
            }
            client.NoDelay = true;
            upstream.NoDelay = true;
            Write(from + " has connected.");

            var clientPair = new ClientPair(client, upstream);
            var up = new Thread(delegate() { Pump(client, upstream, clientPair); })
            {
                IsBackground = true
            };
            up.Start();
            Pump(upstream, client, clientPair);
            up.Join();
            Write(from + " has been closed.");
        }

        /**
         * Copies bytes from src to dst until either side closes, then closes both.
         */
        static void Pump(TcpClient source, TcpClient destination, ClientPair clientPair)
        {
            var buf = new byte[16384];
            try
            {
                NetworkStream input = source.GetStream();
                NetworkStream output = destination.GetStream();
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    output.Write(buf, 0, n);
            }
            catch (System.IO.IOException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }   // GetStream on an already closed client
            clientPair.Close();                            // one direction ended: end the connection
        }

        private sealed class ClientPair
        {
            private readonly TcpClient _source, _destination;
            private int _closed;

            public ClientPair(TcpClient source, TcpClient destination)
            {
                this._source = source;
                this._destination = destination;
            }
            
            public void Close()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                _source.Close();
                _destination.Close();
            }
        }

        static string GetEndpoint(TcpClient c)
        {
            try { return c.Client.RemoteEndPoint.ToString(); }
            catch (Exception) { return "?"; }
        }

        void Write(string msg)
        {
            Console.WriteLine("[relay] " + msg);
        }
    }
}
