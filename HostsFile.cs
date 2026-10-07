using System;
using System.IO;

namespace silverbullet
{
    public static class HostsFile
    {
        public static void UpdateHostsFile(string hostname)
        {
            var hostsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"drivers\etc\hosts"
            );
            
            var lines = File.ReadAllLines(hostsPath);
            foreach (var line in lines)
            {
                if (line.StartsWith("#")) continue;

                var trimmedLine= line;
                if (trimmedLine.Contains("#"))
                {
                    trimmedLine = trimmedLine.Substring(
                        0,
                        line.IndexOf("#", StringComparison.Ordinal) - 1
                    );
                }
                var parts = trimmedLine.Split(new []{' ', '\t'}, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2) continue;

                var ip = parts[0].Trim();
                var host = parts[1].Trim();

                if (ip != "127.0.0.1") continue;
                // If an identical record already exists, we can just exit without modifying the hosts file.
                if (hostname.Equals(host, StringComparison.InvariantCultureIgnoreCase)) return;
            }
            
            Console.WriteLine("Redirecting " + hostname + " to 127.0.0.1 via hosts file...");
            using (var stream = File.AppendText(hostsPath))
            {
                stream.WriteLine("127.0.0.1 " + hostname + " # Silverbullet IP redirect");
                stream.Flush();
            }
        }
    }
}