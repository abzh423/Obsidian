﻿using Obsidian.BlockData;
using Obsidian.Commands;
using Obsidian.Concurrency;
using Obsidian.Entities;
using Obsidian.Events;
using Obsidian.Logging;
using Obsidian.Plugins;
using Obsidian.World;
using Obsidian.World.Generators;
using Qmmands;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Obsidian
{
    public struct QueueChat
    {
        public string Message;
        public byte Position;
    }

    public class Server
    {
        private ConcurrentQueue<QueueChat> _chatmessages;
        private CancellationTokenSource _cts;
        private TcpListener _tcpListener;

        public MinecraftEventHandler Events;
        public PluginManager PluginManager;
        public DateTimeOffset StartTime;

        public List<WorldGenerator> WorldGenerators { get; } = new List<WorldGenerator>();

        public WorldGenerator WorldGenerator { get; private set; }

        /// <summary>
        /// Creates a new Server instance. Spawning multiple of these could make a multi-server setup  :thinking:
        /// </summary>
        /// <param name="version">Version the server is running.</param>
        public Server(Config config, string version, string serverid)
        {
            this.Config = config;

            this.Logger = new Logger($"Obsidian ID: {serverid}", Config.LogLevel);

            this.Port = config.Port;
            this.Version = version;
            this.Id = serverid;

            this._tcpListener = new TcpListener(IPAddress.Any, this.Port);

            this.Clients = new ConcurrentHashSet<Client>();

            this._cts = new CancellationTokenSource();
            this._chatmessages = new ConcurrentQueue<QueueChat>();
            this.Commands = new CommandService(new CommandServiceConfiguration()
            {
                CaseSensitive = false,
                DefaultRunMode = RunMode.Parallel,
                IgnoreExtraArguments = true
            });
            this.Commands.AddModule<MainCommandModule>();
            this.Events = new MinecraftEventHandler();

            this.PluginManager = new PluginManager(this);
        }

        public ConcurrentHashSet<Client> Clients { get; }
        public CommandService Commands { get; }
        public Config Config { get; }
        public Logger Logger { get; }
        public string Id { get; private set; }
        public string Version { get; }
        public int Port { get; }
        public int TotalTicks { get; private set; } = 0;

        private async Task ServerLoop()
        {
            var keepaliveticks = 0;
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(50);

                TotalTicks++;
                await Events.InvokeServerTick();

                keepaliveticks++;
                if (keepaliveticks > 200)
                {
                    var keepaliveid = DateTime.Now.Millisecond;

                    foreach (var clnt in this.Clients.Where(x => x.State == ClientState.Play).ToList())
                        await Task.Factory.StartNew(async () => { await clnt.SendKeepAliveAsync(keepaliveid); }).ContinueWith(t => { if (t.IsCompleted) Logger.LogDebugAsync($"Broadcasting keepalive {keepaliveid}"); });

                    keepaliveticks = 0;
                }

                if (_chatmessages.Count > 0)
                {
                    foreach (var clnt in this.Clients.Where(x => x.State == ClientState.Play).ToList())
                        if (_chatmessages.TryDequeue(out QueueChat msg))
                            await Task.Factory.StartNew(async () => { await clnt.SendChatAsync(msg.Message, msg.Position); });
                }

                foreach (var client in Clients)
                {
                    if (client.Timedout)
                        client.Disconnect();
                    if (!client.Tcp.Connected)
                        this.Clients.TryRemove(client);
                }
            }
        }

        public bool CheckPlayerOnline(string username) => this.Clients.Any(x => x.Player != null && x.Player.Username == username);

        public async Task SendChatAsync(string message, Client source, byte position = 0, bool system = false)
        {
            if (system)
            {
                _chatmessages.Enqueue(new QueueChat() { Message = message, Position = position });
                await Logger.LogMessageAsync(message);
            }
            else
            {
                if (!CommandUtilities.HasPrefix(message, '/', out string output))
                {
                    _chatmessages.Enqueue(new QueueChat() { Message = $"<{source.Player.Username}> {message}", Position = position });
                    await Logger.LogMessageAsync($"<{source.Player.Username}> {message}");
                    return;
                }

                var context = new CommandContext(source, this);
                IResult result = await Commands.ExecuteAsync(output, context);
                if (!result.IsSuccessful)
                    _chatmessages.Enqueue(new QueueChat()
                    {
                        Message = $"{ChatColor.Red}Command error: {(result as FailedResult).Reason}",
                        Position = position
                    });
            }
        }

        /// <summary>
        /// Starts this server
        /// </summary>
        /// <returns></returns>
        public async Task StartServer()
        {
            await Logger.LogMessageAsync($"Launching Obsidian Server v {Version} with ID {Id}");

            await Logger.LogMessageAsync("Registering default entities");
            await RegisterDefaultAsync();

            await Logger.LogMessageAsync($"Loading and Initializing plugins...");
            await this.PluginManager.LoadPluginsAsync(this.Logger);

            if (WorldGenerators.FirstOrDefault(g => g.Id == Config.Generator) is WorldGenerator worldGenerator)
            {
                this.WorldGenerator = worldGenerator;
            }
            else
            {
                throw new Exception($"Generator ({Config.Generator}) is unknown.");
            }
            await Logger.LogMessageAsync($"World generator set to {this.WorldGenerator.Id} ({this.WorldGenerator.ToString()})");

            await Logger.LogDebugAsync($"Set start DateTimeOffset for measuring uptime.");
            this.StartTime = DateTimeOffset.Now;

            await Logger.LogMessageAsync("Starting server backend...");
            await Task.Factory.StartNew(async () => { await this.ServerLoop().ConfigureAwait(false); });

            if (!this.Config.OnlineMode)
                await this.Logger.LogMessageAsync($"Server is in offline mode..");

            await Logger.LogDebugAsync($"Start listening for new clients");
            _tcpListener.Start();

            await Blocks.RegisterAsync();

            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _tcpListener.AcceptTcpClientAsync();

                await Logger.LogDebugAsync($"New connection from client with IP {tcp.Client.RemoteEndPoint.ToString()}");

                int newplayerid = 0;
                if (Clients.Count > 0)
                    newplayerid = this.Clients.Max(x => x.PlayerId);

                var clnt = new Client(tcp, this.Config, newplayerid, this);
                Clients.Add(clnt);

                await Task.Factory.StartNew(async () => { await clnt.StartConnectionAsync().ConfigureAwait(false); });
            }
            // Cancellation has been requested
            await Logger.LogWarningAsync($"Cancellation has been requested. Stopping server...");
            // TODO: TRY TO GRACEFULLY SHUT DOWN THE SERVER WE DONT WANT ERRORS REEEEEEEEEEE
        }

        public void StopServer()
        {
            this.WorldGenerators.Clear(); //Clean up for memory and next boot
            this._cts.Cancel();
        }

        /// <summary>
        /// Registers the "obsidian-vanilla" entities and objects
        /// </summary>
        private async Task RegisterDefaultAsync()
        {
            await RegisterAsync(new SuperflatGenerator());
            await RegisterAsync(new TestBlocksGenerator());
        }

        public async Task RegisterAsync(params object[] input)
        {
            foreach (object item in input)
            {
                switch (item)
                {
                    default:
                        throw new Exception($"Input ({item.GetType().ToString()}) can't be handled by RegisterAsync.");

                    case WorldGenerator generator:
                        await Logger.LogDebugAsync($"Registering {generator.Id}...");
                        WorldGenerators.Add(generator);
                        break;
                }
            }
        }
    }
}