﻿
using Archipelago.Core;
using Archipelago.Core.MauiGUI;
using Archipelago.Core.MauiGUI.Models;
using Archipelago.Core.MauiGUI.ViewModels;
using Archipelago.Core.Models;
using Archipelago.Core.Util;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using DSAP.Models;
using Newtonsoft.Json;
using Serilog;
using Windows.UI.Core;
using static DSAP.Enums;
using Location = Archipelago.Core.Models.Location;
namespace DSAP
{
    public partial class App : Application
    {
        static MainPageViewModel Context;
        public static ArchipelagoClient Client { get; set; }
        public static List<DarkSoulsItem> AllItems { get; set; }
        private static readonly object _lockObject = new object();
        public App()
        {
            InitializeComponent();
            var options = new GuiDesignOptions
            {
                BackgroundColor = Color.FromArgb("FF000000"),
                ButtonColor = Color.FromArgb("FFFF0000"),
                ButtonTextColor = Color.FromArgb("FF000000"),
                Title = "DSAP - Dark Souls Remastered Archipelago",

            };

            Context = new MainPageViewModel(options);
            Context.ConnectClicked += Context_ConnectClicked;
            Context.CommandReceived += (e, a) =>
            {
                Client?.SendMessage(a.Command);
            };
            MainPage = new MainPage(Context);
            Context.ConnectButtonEnabled = true;
        }
        public static void AddItem(int category, int id, int quantity)
        {
            var command = Helpers.GetItemCommand();
            //Set item category
            Array.Copy(BitConverter.GetBytes(category), 0, command, 0x1, 4);
            //Set item quantity
            Array.Copy(BitConverter.GetBytes(quantity), 0, command, 0x7, 4);
            //set item id
            Array.Copy(BitConverter.GetBytes(id), 0, command, 0xD, 4);

            var result = Memory.ExecuteCommand(command);
        }

        public static bool IsValidPointer(ulong address)
        {
            try
            {
                Memory.ReadByte(address);
                return true;
            }
            catch
            {
                return false;
            }
        }
        public static async Task MonitorLocations(List<Location> locations)
        {
            var locationBatches = locations
                .Select((location, index) => new { Location = location, Index = index })
                .GroupBy(x => x.Index / 25)
                .Select(g => g.Select(x => x.Location).ToList())
                .ToList();
            var tasks = locationBatches.Select(x => MonitorBatch(x));
            await Task.WhenAll(tasks);

        }
        private static async Task MonitorBatch(List<Location> batch)
        {
            List<Location> completed = new List<Location>();

            while (!batch.All(x => completed.Any(y => y.Id == x.Id)))
            {
                foreach (var location in batch)
                {
                    var isCompleted = await global::Archipelago.Core.Util.Helpers.CheckLocation(location);
                    if (isCompleted)
                    {
                        completed.Add(location);
                        //  Log.Logger.Information(JsonConvert.SerializeObject(location));
                    }
                }
                if (completed.Any())
                {
                    foreach (var location in completed)
                    {
                        Client.SendLocation(location);
                        //     Log.Logger.Information($"{location.Name} ({location.Id}) Completed");
                        batch.Remove(location);
                    }
                }
                completed.Clear();
                await Task.Delay(500);
            }
        }
        private async void Context_ConnectClicked(object? sender, ConnectClickedEventArgs e)
        {
            Context.ConnectButtonEnabled = false;
            Log.Logger.Information("Connecting...");
            if (Client != null)
            {
                Client.Connected -= OnConnected;
                Client.Disconnected -= OnDisconnected;
                Client.ItemReceived -= Client_ItemReceived;
                Client.MessageReceived -= Client_MessageReceived;
                Client.CancelMonitors();
            }
            DarkSoulsClient client = new DarkSoulsClient();
            var connected = client.Connect();
            if (!connected)
            {
                Log.Logger.Error("Dark Souls not running, open Dark Souls before connecting!");
                Context.ConnectButtonEnabled = true;
                return;
            }

            Client = new ArchipelagoClient(client);

            AllItems = Helpers.GetAllItems();
            Client.Connected += OnConnected;
            Client.Disconnected += OnDisconnected;
            var isOnline = Helpers.GetIsPlayerOnline();
            if (isOnline)
            {
                Log.Logger.Warning("YOU ARE PLAYING ONLINE. THIS APPLICATION WILL NOT PROCEED.");
                Context.ConnectButtonEnabled = true;
                return;
            }
            await Client.Connect(e.Host, "Dark Souls Remastered");

            Client.ItemReceived += Client_ItemReceived;
            Client.MessageReceived += Client_MessageReceived;

            await Client.Login(e.Slot, !string.IsNullOrWhiteSpace(e.Password) ? e.Password : null);



            var bossLocations = Helpers.GetBossFlagLocations();
            var itemLocations = Helpers.GetItemLotLocations();
            var bonfireLocations = Helpers.GetBonfireFlagLocations();
            var doorLocations = Helpers.GetDoorFlagLocations();
            var fogWallLocations = Helpers.GetFogWallFlagLocations();
            var miscLocations = Helpers.GetMiscFlagLocations();

            var goalLocation = bossLocations.First(x => x.Name.Contains("Lord of Cinder"));
            Archipelago.Core.Util.Memory.MonitorAddressBitForAction(goalLocation.Address, goalLocation.AddressBit, () => Client.SendGoalCompletion());

            Client.MonitorLocations(bossLocations);
            Client.MonitorLocations(itemLocations);
            Client.MonitorLocations(bonfireLocations);
            Client.MonitorLocations(doorLocations);
            // Client.MonitorLocations(fogWallLocations);
            Client.MonitorLocations(miscLocations);


            //Helpers.MonitorLastBonfire((lastBonfire) =>
            //{
            //    Log.Logger.Debug($"Rested at bonfire: {lastBonfire.id}:{lastBonfire.name}");
            //});
            RemoveItems();
            Context.ConnectButtonEnabled = true;
        }

        private void Client_MessageReceived(object? sender, Archipelago.Core.Models.MessageReceivedEventArgs e)
        {
            if (e.Message.Parts.Any(x => x.Text == "[Hint]: "))
            {
                LogHint(e.Message);
            }
            Log.Logger.Information(JsonConvert.SerializeObject(e.Message));
        }

        private static void RemoveItems()
        {
            var lots = Helpers.GetItemLots();
            var lotFlags = Helpers.GetItemLotFlags();

            //Helpers.WriteToFile("itemLots.json", lots);

            var replacementLot = new ItemLot()
            {
                Rarity = 1,
                GetItemFlagId = -1,
                CumulateNumFlagId = -1,
                CumulateNumMax = 0,
                Items = new List<ItemLotItem>()
                {
                    new ItemLotItem
                    {
                        CumulateLotPoint = 0,
                        CumulateReset = false,
                        EnableLuck = false,
                        GetItemFlagId = -1,
                        LotItemBasePoint = 100,
                        LotItemCategory = (int)DSItemCategory.Consumables,
                        LotItemNum = 1,
                        LotItemId = 370
                    }
                }
            };
            foreach(var lotFlag in lotFlags.Where(x => x.IsEnabled))
            {
                _ = Task.Run(() =>
                {
                    Helpers.OverwriteItemLot(lotFlag.Flag, replacementLot);
                });
            }
            Log.Logger.Information("Finished overwriting items");
        }
        private static void Client_ItemReceived(object? sender, ItemReceivedEventArgs e)
        {
            LogItem(e.Item);
            var itemId = e.Item.Id;
            var itemToReceive = AllItems.FirstOrDefault(x => x.ApId == itemId);
            if (itemToReceive != null)
            {
                Log.Logger.Verbose($"Received {itemToReceive.Name} ({itemToReceive.ApId})");
                AddItem((int)itemToReceive.Category, itemToReceive.Id, 1);
            }
            else
            {
                Log.Logger.Information("Couldnt find correct item");
                var filler = AllItems.First(x => x.Id == 380);
                AddItem((int)filler.Category, filler.Id, 1);
            }
        }
        private static void LogItem(Item item)
        {
            var messageToLog = new LogListItem(new List<TextSpan>()
            {
                new TextSpan(){Text = $"[{item.Id.ToString()}] -", TextColor = Color.FromRgb(255, 255, 255)},
                new TextSpan(){Text = $"{item.Name}", TextColor = Color.FromRgb(200, 255, 200)},
                new TextSpan(){Text = $"x{item.Quantity.ToString()}", TextColor = Color.FromRgb(200, 255, 200)}
            });
            lock (_lockObject)
            {
                Application.Current.Dispatcher.DispatchAsync(() =>
                {
                    Context.ItemList.Add(messageToLog);
                });
            }
        }
        private static void LogHint(LogMessage message)
        {
            var newMessage = message.Parts.Select(x => x.Text);

            if (Context.HintList.Any(x => x.TextSpans.Select(y => y.Text) == newMessage))
            {
                return; //Hint already in list
            }
            List<TextSpan> spans = new List<TextSpan>();
            foreach (var part in message.Parts)
            {
                spans.Add(new TextSpan() { Text = part.Text, TextColor = Color.FromRgb(part.Color.R, part.Color.G, part.Color.B) });               
            }
            lock (_lockObject)
            {
                Application.Current.Dispatcher.DispatchAsync(() =>
                {
                    Context.HintList.Add(new LogListItem(spans));
                });
            }
        }
        private static void OnConnected(object sender, EventArgs args)
        {
            Log.Logger.Information("Connected to Archipelago");
            Log.Logger.Information($"Playing {Client.CurrentSession.ConnectionInfo.Game} as {Client.CurrentSession.Players.GetPlayerName(Client.CurrentSession.ConnectionInfo.Slot)}");
        }

        private static void OnDisconnected(object sender, EventArgs args)
        {
            Log.Logger.Information("Disconnected from Archipelago");
        }
        protected override Window CreateWindow(IActivationState activationState)
        {
            var window = base.CreateWindow(activationState);
            if (DeviceInfo.Current.Platform == DevicePlatform.WinUI)
            {
                window.Title = "DSAP - Dark Souls Archipelago Randomizer";

            }
            window.Width = 600;

            return window;
        }
    }
}
