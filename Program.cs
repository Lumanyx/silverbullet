using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using silverbullet;
using Silverbullet.NetworkDevice;

namespace Silverbullet
{
    internal class Program
    {
        private static bool isRunning = true;
        private const string DesiredInterfaceName = "Silverbullet Loopback";

        /**
         * Parses the provided string arguments as a dictionary.
         * (Parameters need to be formatted like this: "--key=value")
         */
        private static Dictionary<string, string> ParseParameterMap(string[] args)
        {
            var dictionary = new Dictionary<string, string>();
            foreach(var parameter in args)
            {
                var split = parameter.Split(new []{'='}, StringSplitOptions.RemoveEmptyEntries);
                if(split.Length != 2) continue;
                if(!split[0].StartsWith("--")) continue;
                dictionary.Add(split[0].Substring(2), split[1]);
            }
            
            return dictionary;
        }
        
        
        private static void RunNetshCommand(string command)
        {
            var cmd = new Process();
            cmd.StartInfo.FileName = "netsh";
            cmd.StartInfo.Arguments = command;
            cmd.StartInfo.RedirectStandardInput = true;
            cmd.StartInfo.RedirectStandardOutput = true;
            cmd.StartInfo.CreateNoWindow = true;
            cmd.StartInfo.UseShellExecute = false;
            cmd.Start();

            Console.WriteLine(cmd.StandardOutput.ReadToEnd());
            cmd.WaitForExit();
            if(cmd.ExitCode != 0)
                Console.Error.WriteLine("netsh (" + command + ") exited with exit code " + cmd.ExitCode);
        }
        
        /**
         * Helper function for determining the netmask to use for the netsh command.
         */
        private static string GetMaskFor(IPAddress ip)
        {
            // Only relevant on WinXP
            if (Environment.OSVersion.Version.Major >= 6) return "255.255.255.255";

            var b = ip.GetAddressBytes();
            
            // Build a packed integer from the IP data
            var addr = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            
            for (var prefix = 30; prefix >= 8; prefix--)
            {
                var mask = 0xFFFFFFFFu << (32 - prefix);
                var hostPart = addr & ~mask;
                var networkAddress = 0; // Network address = (All host bits == 0)
                var broadcastAddress = ~mask; // Broadcast address = (All host bits == 1)
                
                if (hostPart != networkAddress && hostPart != broadcastAddress) // not the network or broadcast address
                    return new IPAddress(new[]
                    {
                        (byte)(mask >> 24), (byte)(mask >> 16), 
                        (byte)(mask >> 8), (byte)mask
                    }).ToString();
            }
            throw new ArgumentException("Unable to determine subnet mask for " + ip);
        }

        private static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "uninstall")
            {
                Console.WriteLine("Removing loopback device...");
                var old = LoopbackDeviceHelper.FindByConnectionName(DesiredInterfaceName);
                if (old != null) LoopbackDeviceHelper.Remove(old.NetConfigInstanceId);
                return;
            }
            
            Console.WriteLine("Starting Silverbullet...");
            var map = ParseParameterMap(args);
            
            map.TryGetValue("working_directory", out var workingDirectory);
            if (workingDirectory == null) throw new ArgumentException("Missing parameter: --working_directory");
            
            map.TryGetValue("executable", out var executable);
            if (executable == null) throw new ArgumentException("Missing parameter: --executable");

            var executableArgs = new string[0];
            if (map.TryGetValue("args", out var value)) executableArgs = value.Split(' ');
            
            map.TryGetValue("target_ip", out var targetIp);
            if (targetIp == null) throw new ArgumentException("Missing parameter: --target_ip");
            
            map.TryGetValue("forward_src_port", out var forwardSrcPort);
            if (forwardSrcPort == null) throw new ArgumentException("Missing parameter: --forward_src_port");
            
            map.TryGetValue("forward_to_ip", out var forwardToIp);
            if (forwardToIp == null) throw new ArgumentException("Missing parameter: --forward_to_ip");
            
            if(!IPAddress.TryParse(forwardToIp, out _))
                throw new ArgumentException("Invalid IP address specified: " + targetIp);
            
            map.TryGetValue("forward_to_port", out var forwardToPort);
            if (forwardToPort == null) throw new ArgumentException("Missing parameter: --forward_to_port");
            
            if(!IPAddress.TryParse(targetIp, out var parsedIp))
                throw new ArgumentException("Invalid IP address specified: " + targetIp);
            
            map.TryGetValue("http_port", out var httpPort);
            if (httpPort == null) throw new ArgumentException("Missing parameter: --http_port");
            
            map.TryGetValue("http_root", out var httpRoot);
            if (httpRoot == null) throw new ArgumentException("Missing parameter: --http_root");
            
            map.TryGetValue("hostname_redirect", out var hostnameRedirect);
            if (hostnameRedirect == null) throw new ArgumentException("Missing parameter: --hostname_redirect");
            
            Console.WriteLine("");
            Console.WriteLine("     working_directory = " + workingDirectory);
            Console.WriteLine("     args = " + string.Join(" ", executableArgs));
            Console.WriteLine("     executable = " + executable);
            Console.WriteLine("     target_ip = " + targetIp);
            Console.WriteLine("     forward_to_ip = " + forwardToIp);
            Console.WriteLine("     http_port = " + httpPort);
            Console.WriteLine("     http_root = " + httpRoot);
            Console.WriteLine("     hostname_redirect = " + hostnameRedirect);
            Console.WriteLine("");
            
            Console.WriteLine("Updating Hosts file...");
            HostsFile.UpdateHostsFile(hostnameRedirect);
            
            Console.WriteLine("Creating Network Interface...");
            var adapter = LoopbackDeviceHelper.FindByConnectionName(DesiredInterfaceName);
            if (adapter == null)
            {
                Console.WriteLine("Network Interface does not exist. Creating...");
                adapter = LoopbackDeviceHelper.Create(15);
                if (adapter.ConnectionName == null)
                    throw new Exception("Adapter created but no connecton appeared.");
                RunNetshCommand("interface set interface " +
                                  "name=\"" + adapter.ConnectionName + "\" " +
                                  "newname=\"" + DesiredInterfaceName + "\"");
            }
            Console.WriteLine("Setting up IP redirect...");
            RunNetshCommand("interface ip set address " +
                              "name=\"" + DesiredInterfaceName + "\" " +
                              "static " + targetIp + " " + GetMaskFor(parsedIp));

                
            Console.WriteLine("Starting HTTP server...");
            using (var server = new StaticFileServer(httpRoot, int.Parse(httpPort)))
            {
                try
                {
                    server.Start();
                }
                catch (SocketException ex)
                {
                    if (ex.ErrorCode == 10048) // WSAEADDRINUSE
                        throw new Exception("Unable to start HTTP server: Port " + httpPort + " is in use");
                    throw new Exception("Unable to start HTTP server", ex);
                }
            
                // Start the Relay server
                var relay = new TcpRelay(
                    parsedIp,
                    int.Parse(forwardSrcPort),
                    forwardToIp,
                    int.Parse(forwardToPort)
                );
                relay.Start();
                
                // Start the actual executable
                Console.WriteLine("Starting process...");
                var psi = new ProcessStartInfo(
                    Path.Combine(workingDirectory, executable), 
                    string.Join(" ", executableArgs)
                )
                {
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false
                };

                using (var app = Process.Start(psi))
                {
                    if (app == null) throw new Exception("Couldn't start main process");
                    app.WaitForExit();
                    Console.WriteLine("Main process exited with exit code " + app.ExitCode);
                }

                Console.CancelKeyPress += (sender, eventArgs) =>
                {
                    isRunning = false;
                };
                
                Console.WriteLine("Entering main wait loop... (Use Ctrl-C to exit)");
                while (isRunning)
                {
                    Thread.Sleep(100);
                }
                Console.WriteLine("Shutting down...");
            }
            
        }
        
    }
}
