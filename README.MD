# Silverbullet

Silverbullet is a small Windows CLI tool that allows redirecting network traffic of a program.
This is done by setting up a loopback interface that (in combination with a local TCP relay) will forward TCP packets to a remote server.
Additionally, the tool hosts a small HTTP server which can be helpful for redirecting HTTP traffic. (It also adds an entry to your hosts file to make the hostname of the application point to your local machine)

The tool is designed to run on Windows XP, Vista, 7, 8, 8.1, 10 and 11. It does not modify, patch or inject into the program it starts.

> [!IMPORTANT]
> This project is provided for educational, research and software preservation purposes only!
> It is a generic networking utility. It is not affiliated with, endorsed by or connected to any game, publisher or other software vendor, and it does not contain, download or distribute any third-party software, files or code.
>
> You are responsible for making sure that you have the right to run and connect the software you use it with, and that doing so complies with the laws of your country and any license or terms of service that apply to that software. Do not use this tool to access services you are not authorized to access.

## Requirements

- Windows XP SP3 up to Windows 11
- .NET Framework 2.0 or newer (3.5 and 4.x both work)
- Admin rights (Silverbullet asks for them automatically as long as it's supported by the OS)

## Usage

```
silverbullet.exe --working_directory=<folder> --executable=<file> "--args=<arguments>"
                 --target_ip=<ip> --forward_src_port=<port> --forward_to_ip=<ip> --forward_to_port=<port>
                 --hostname_redirect=<hostname> --http_root=<folder> --http_port=<port>

silverbullet.exe uninstall
```

(Once the tool has started, use Ctrl+C to terminate it)


| Parameter | Description |
|---|---|
| `--working_directory` | Folder the program is started in (Use quotes if your path contains spaces) |
| `--executable` | Program file, relative to the working directory |
| `--args` | Arguments passed to the program (Important: Quote the whole parameter if it contains spaces) |
| `--target_ip` | The fixed IPv4 address that you want to redirect |
| `--forward_src_port` | TCP port on `--target_ip` to relay |
| `--forward_to_ip` | IPv4 address of the server the relay connects to |
| `--forward_to_port` | TCP port on that server |
| `--hostname_redirect` | Hostname to point at `127.0.0.1` via the `hosts` file |
| `--http_root` | Absolute path of a directory that files should be served from |
| `--http_port` | Port of the local HTTP server (usually `80` or `8080`) |

Example:

```
silverbullet.exe --working_directory=C:\Games\Example --executable=example.exe "--args=-user test"
                 --target_ip=1.2.3.4 --forward_src_port=5000 --forward_to_ip=20.30.67.100 --forward_to_port=5000
                 --hostname_redirect=update.example.invalid --http_root=C:\Games\Example\update --http_port=80
```

## System Modifications

Silverbullet installs a loopback network adapter on your system. To remove it, either run `silverbullet.exe uninstall` or remove it in Device Manager.
It also modifies your hosts file (e.g. `C:\Windows\System32\drivers\etc\hosts`) - Just remove the line ending in `# Silverbullet IP redirect` from your hosts file.

The TCP relay and the HTTP server are only active when the tool is running.

## Details and known limitations

- Windows XP does not accept a single address subnet mask (`255.255.255.255`). 
  - This is why `GetMaskFor` exists: It tries to find the narrowest mask possible (for example `/28`), so a few neighbouring addresses are also routed to the loopback adapter.
- The relay server handles only one TCP port. (UDP or any other protocols are not supported)
- The http server only listens to your loopback interface (`127.0.0.1`) - Only `GET` & `HEAD` requests are answered. The server only serves files that are directly inside the directory specified in `--http_root`.

Currently, the tool forces you to use all of it's features at once. This might change in the future, but it wasn't a huge priority for me at the moment.

Also, important to note: Although I haven't seen it happen yet, Antivirus programs or anticheat software will very likely flag the executable.

You always have the option to manually build the software yourself if you wish to do so.

## Building

Open the project in Visual Studio (Rider also works!) and build it.
The project targets .NET Framework 3.5 and runs on the 2.0 and 4.x runtimes.

Required settings:
- Build it as `AnyCPU` (without `Prefer 32 bit`) -> As far as I know, Windows refuses driver installations from 32 bit programs on 64 bit systems.

The reason why it's based on such an old version of .NET is that I wanted to make sure it works on Windows XP. (Requirement for a personal project of mine)

## Disclaimer

This software is provided "as is", without warranty of any kind. The authors are not liable for any damage or legal consequences arising from its use. All trademarks belong to their respective owners.

## License

The repository's license can be found [here](LICENSE.MD).
