using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InternetAgain
{
    internal sealed class CheckResult
    {
        public DateTime At = DateTime.UtcNow;
        public bool? Online;
        public string Headline, Detail, Wifi, Link, Router, Internet, Stage;
        public IPAddress Gateway;
        public bool GatewayTested, GatewayResponded;
        public long GatewayMs;
    }

    internal static class NetworkCheck
    {
        private const string CloudflareUrl = "https://www.cloudflare.com/cdn-cgi/trace";
        private const string GoogleUrl = "https://connectivitycheck.gstatic.com/generate_204";

        public static async Task<CheckResult> Run()
        {
            var result = new CheckResult();
            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces();
                var connected = adapters.Where(a => a.OperationalStatus == OperationalStatus.Up &&
                    a.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray();
                var wireless = connected.FirstOrDefault(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                result.Wifi = wireless != null ? "Подключён" :
                    adapters.Any(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                        ? "Не подключён" : "Адаптер не найден";
                if (connected.Length == 0)
                {
                    result.Link = "Нет активного адаптера";
                    result.Router = result.Internet = "Проверка недоступна";
                    return Finish(result, false, "Сетевой адаптер", "Компьютер не подключён к сети",
                        "Проверьте подключение к Wi-Fi или сетевой кабель.");
                }

                var localAddresses = new[] { SourceAddress("1.1.1.1"), SourceAddress("2606:4700:4700::1111") };
                var routed = connected.Where(a => a.GetIPProperties().UnicastAddresses.Any(
                    address => localAddresses.Any(local => local != null && address.Address.Equals(local)))).ToArray();
                var primary = routed.FirstOrDefault() ?? wireless ?? connected[0];
                var candidates = routed.Concat(connected).Distinct();
                var gateway = candidates.SelectMany(a => a.GetIPProperties().GatewayAddresses
                    .Select(g => ScopeGateway(g.Address, a))).FirstOrDefault(IsUsable);
                result.Link = primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                    ? "Wi-Fi" : primary.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                    ? "Ethernet" : primary.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                    ? "Туннель / VPN" : primary.Name;

                var routerTask = ProbeGateway(gateway, result);
                var external = new[] { ProbeWeb(CloudflareUrl, false), ProbeWeb(GoogleUrl, true) };
                bool online = false;
                while (external.Length > 0)
                {
                    var finished = await Task.WhenAny(external).ConfigureAwait(false);
                    if (await finished.ConfigureAwait(false))
                    {
                        online = true;
                        break;
                    }
                    external = external.Where(task => task != finished).ToArray();
                }
                await routerTask.ConfigureAwait(false);
                if (online)
                {
                    result.Internet = "HTTPS работает";
                    return Finish(result, true, "Доступ есть", "Интернет работает",
                        "Контрольный сайт ответил по HTTPS.");
                }

                result.Internet = "Контрольные сайты недоступны";
                if (localAddresses.All(address => address == null) && gateway == null)
                    return Finish(result, false, "Маршрут", "Не найден маршрут в интернет",
                        "Проверьте настройки IP и подключение к домашней сети.");
                if (result.GatewayTested && !result.GatewayResponded)
                    return Finish(result, false, "Шлюз / интернет", "Шлюз не ответил, сайты недоступны",
                        "Шлюз может блокировать ping. Проверьте роутер и повторите проверку.");
                return Finish(result, false, "Интернет", "Сайты недоступны",
                    "Проверьте линию провайдера, DNS или страницу входа в сеть.");
            }
            catch (Exception e)
            {
                if (!(e is SocketException || e is NetworkInformationException || e is InvalidOperationException)) throw;
                result.Wifi = result.Wifi ?? "Нет данных";
                result.Link = result.Link ?? "Нет данных";
                result.Router = result.Router ?? "Нет данных";
                result.Internet = "Проверка не завершилась";
                return Finish(result, null, "Ошибка проверки", "Не удалось проверить подключение", e.Message);
            }
        }

        private static CheckResult Finish(CheckResult result, bool? online, string stage, string title, string detail)
        {
            result.Online = online;
            result.Stage = stage;
            result.Headline = title;
            result.Detail = detail;
            return result;
        }

        private static bool IsUsable(IPAddress address)
        {
            return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) &&
                !IPAddress.IsLoopback(address);
        }

        private static IPAddress ScopeGateway(IPAddress address, NetworkInterface adapter)
        {
            if (address.AddressFamily != AddressFamily.InterNetworkV6 || !address.IsIPv6LinkLocal ||
                address.ScopeId != 0) return address;
            var properties = adapter.GetIPProperties().GetIPv6Properties();
            return properties == null ? address : new IPAddress(address.GetAddressBytes(), properties.Index);
        }

        private static IPAddress SourceAddress(string destination)
        {
            IPAddress target = IPAddress.Parse(destination);
            try
            {
                using (var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect(new IPEndPoint(target, 443));
                    var local = ((IPEndPoint)socket.LocalEndPoint).Address;
                    return IsUsable(local) ? local : null;
                }
            }
            catch (SocketException) { return null; }
        }

        private static async Task ProbeGateway(IPAddress gateway, CheckResult result)
        {
            if (gateway == null)
            {
                result.Router = "Шлюз не найден";
                return;
            }
            result.Gateway = gateway;
            result.GatewayTested = true;
            try
            {
                using (var ping = new Ping())
                {
                    var reply = await ping.SendPingAsync(gateway, 1200).ConfigureAwait(false);
                    result.GatewayResponded = reply.Status == IPStatus.Success;
                    result.GatewayMs = reply.RoundtripTime;
                }
            }
            catch (PingException) { }
            result.Router = result.GatewayResponded ? "Отвечает · " + result.GatewayMs + " мс" : "Нет ответа на ping";
        }

        private static async Task<bool> ProbeWeb(string url, bool expectNoContent)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.AllowAutoRedirect = false;
                request.KeepAlive = false;
                request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.BypassCache);
                using (var deadline = new CancellationTokenSource(3800))
                using (deadline.Token.Register(request.Abort))
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    return expectNoContent ? response.StatusCode == HttpStatusCode.NoContent
                        : response.StatusCode == HttpStatusCode.OK && !String.IsNullOrEmpty(response.Headers["cf-ray"]);
                }
            }
            catch (WebException error)
            {
                if (error.Response != null) error.Response.Close();
                return false;
            }
            catch (IOException) { return false; }
        }
    }

    public sealed class Outage
    {
        public DateTime Start { get; set; }
        public DateTime LastFailure { get; set; }
        public DateTime? End { get; set; }
        public bool Interrupted { get; set; }
        public string FirstStage { get; set; }
        public string LastStage { get; set; }
        public DateTime DurationEnd { get { return End ?? LastFailure; } }
    }

    public sealed class TrafficTotal
    {
        public long Received { get; set; }
        public long Sent { get; set; }
    }

    public sealed class AppState
    {
        public string PaymentDate { get; set; }
        public int LeadDays { get; set; }
        public string LastNotice { get; set; }
        public string TrafficDate { get; set; }
        public Dictionary<string, TrafficTotal> Traffic { get; set; }
        public List<Outage> Outages { get; set; }

        public AppState()
        {
            LeadDays = 3;
            Traffic = new Dictionary<string, TrafficTotal>(StringComparer.OrdinalIgnoreCase);
            Outages = new List<Outage>();
        }

        public void BreakObservation()
        {
            var current = Outages.LastOrDefault();
            if (current != null && !current.End.HasValue && !current.Interrupted) current.Interrupted = true;
        }

        internal bool Record(CheckResult check, DateTime? preceding)
        {
            var current = Outages.LastOrDefault();
            bool changed = false;
            bool wasOpen = current != null && !current.End.HasValue && !current.Interrupted;
            if (wasOpen && (!preceding.HasValue || check.At - preceding.Value > TimeSpan.FromSeconds(25) ||
                check.At < preceding.Value || !check.Online.HasValue))
            {
                BreakObservation();
                changed = true;
                wasOpen = false;
            }
            if (check.Online == false)
            {
                if (!wasOpen)
                {
                    current = new Outage { Start = check.At, FirstStage = check.Stage };
                    Outages.Add(current);
                }
                current.LastFailure = check.At;
                current.LastStage = check.Stage;
                return true;
            }
            if (wasOpen && check.Online == true)
            {
                current.End = check.At;
                return true;
            }
            return changed;
        }
    }

    internal static class StateFile
    {
        public static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InternetAgain");
        private static readonly string Pathname = Path.Combine(DirectoryPath, "state.json");

        public static AppState Read()
        {
            if (!File.Exists(Pathname) && !File.Exists(Pathname + ".bak")) return new AppState();
            try { return ReadFrom(Pathname); }
            catch (Exception exception)
            {
                if (!(exception is IOException || exception is System.Runtime.Serialization.SerializationException)) throw;
                return ReadFrom(Pathname + ".bak");
            }
        }

        private static AppState ReadFrom(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                var state = (AppState)new DataContractJsonSerializer(typeof(AppState)).ReadObject(stream);
                if (state == null) throw new System.Runtime.Serialization.SerializationException("Пустой файл данных.");
                state.Traffic = state.Traffic == null ? new Dictionary<string, TrafficTotal>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, TrafficTotal>(state.Traffic, StringComparer.OrdinalIgnoreCase);
                state.Outages = state.Outages ?? new List<Outage>();
                state.LeadDays = Math.Max(0, Math.Min(30, state.LeadDays));
                return state;
            }
        }

        public static void Save(AppState state)
        {
            Directory.CreateDirectory(DirectoryPath);
            string pending = Pathname + ".tmp";
            using (var stream = File.Create(pending))
            {
                new DataContractJsonSerializer(typeof(AppState)).WriteObject(stream, state);
                stream.Flush(true);
            }
            if (File.Exists(Pathname)) File.Replace(pending, Pathname, Pathname + ".bak");
            else File.Move(pending, Pathname);
        }

        public static string LocalDay(DateTime time)
        {
            return time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
