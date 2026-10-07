using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Silverbullet
{
    /**
     * Class that manages a static HTTP server.
     */
    public sealed class StaticFileServer : IDisposable
    {
        // Dictionary that maps files incoming paths to absolute file paths
        private readonly Dictionary<string, string> files = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        );

        private volatile bool isRunning = false;
        private readonly TcpListener _listener;
        private Thread _connectionThread;

        public StaticFileServer(string rootDirectory, int port)
        {
            foreach (var path in Directory.GetFiles(rootDirectory))
                files[Path.GetFileName(path)] = path;
            _listener = new TcpListener(IPAddress.Loopback, port);
        }

        public void Start()
        {
            _listener.Start();
            isRunning = true;
            _connectionThread = new Thread(ConnectionAcceptLoop)
            {
                IsBackground = true,
                Name = "Static File Server"
            };
            _connectionThread.Start();
            Log("Listening on 127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port);
        }
        
        public void Dispose()
        {
            isRunning = false;
            _listener.Stop();
            _connectionThread?.Join(2000);
        }

        private void ConnectionAcceptLoop()
        {
            while (isRunning)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException ex)
                {
                    Log("Caught Socket Exception: " + ex.Message);
                    break;
                }
                catch (ObjectDisposedException)
                {
                    Log("Received ObjectDisposedException: Assuming file server is shutting down");
                    break;
                }
                ThreadPool.QueueUserWorkItem(HandleRequest, client);
            }
        }

        private void HandleRequest(object state)
        {
            using (var client = (TcpClient)state) {
                try {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var stream = client.GetStream();

                    var requestLine = ReadHeaderBlock(stream); // Request line (we don't care about headers)
                    if (requestLine == null) return;
                    
                    var parts = requestLine.Split(' '); // "GET {Path} HTTP/1.1"
                    if (parts.Length < 2) return;
                    var method = parts[0].ToUpperInvariant();

                    // Only the last path segment counts, and only files that exist in our dictionary are served.
                    // That also rules out path traversals
                    var path = parts[1];
                    var queryCharIndex = path.IndexOf('?');
                    
                    if (queryCharIndex >= 0) path = path.Substring(0, queryCharIndex);
                    var name = path.Substring(path.LastIndexOf('/') + 1);

                    if ((method != "GET" && method != "HEAD") || !files.TryGetValue(name, out var file))
                    {
                        Log(method + " " + parts[1] + " -> 404");
                        RespondToSocket(stream, "404 Not Found", null, false);
                        return;
                    }
                    var body = File.ReadAllBytes(file);
                    Log(method + " " + parts[1] + " -> 200 (" + body.Length + " bytes)");
                    RespondToSocket(stream, "200 OK", body, method == "GET");
                }
                catch (IOException) { } // Timeout or other I/O related issue
                catch (SocketException) { }
            }
        }
        
        
        /**
         * Helper function that reads the first line of an HTTP request + the header block.
         * 
         */
        private static string ReadHeaderBlock(Stream s)
        {
            const int cap = 16384; // Maximum amount of bytes to read
            var line = new StringBuilder();
            string first = null;
            var total = 0;
            while (true)
            {
                var data = s.ReadByte();
                if (data < 0 || total++ > cap) return null; // Closed (<0), or not HTTP/malformed request
                if (data == '\n')
                {
                    var trimmed = line.ToString().TrimEnd('\r');
                    line.Length = 0;
                    if (first == null) first = trimmed;
                    else if (trimmed.Length == 0) return first; // end of headers
                }
                else line.Append((char)data);
            }
        }

        static void RespondToSocket(Stream socket, string status, byte[] body, bool sendBody)
        {
            var length = body?.Length ?? 0;
            var head = "HTTP/1.1 " + status + "\r\n" +
                       "Content-Type: application/octet-stream\r\n" +
                       "Content-Length: " + length + "\r\n" +
                       "Connection: close\r\n\r\n";
            
            var headerBytes = Encoding.ASCII.GetBytes(head);
            socket.Write(headerBytes, 0, headerBytes.Length);
            
            if (sendBody && length > 0)
                if (body != null) 
                    socket.Write(body, 0, length);
            socket.Flush();
        }

        void Log(string msg)
        {
            Console.WriteLine("[http] " + msg);
        }
    }
}