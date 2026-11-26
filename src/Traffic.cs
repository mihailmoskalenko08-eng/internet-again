using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace InternetAgain
{
    internal sealed class TrafficMeter : IDisposable
    {
        private const uint TraceSystemLoggerMode = 0x02000000;
        private const uint TraceRealTimeMode = 0x100;
        private const uint TraceNetworkTcpIp = 0x10000;
        private const uint ProcessTraceEventRecord = 0x10000000;
        private const uint ProcessTraceRealTime = 0x100;
        private const uint StopTrace = 1;
        public const int RateSeconds = 5;
        private static readonly Guid TcpProvider = new Guid("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
        private static readonly Guid UdpProvider = new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");

        private readonly object gate = new object();
        private readonly Dictionary<int, ProcessIdentity> processes = new Dictionary<int, ProcessIdentity>();
        private readonly Dictionary<string, Dictionary<string, TrafficTotal>> pending =
            new Dictionary<string, Dictionary<string, TrafficTotal>>();
        private readonly Dictionary<long, Dictionary<string, TrafficTotal>> recent =
            new Dictionary<long, Dictionary<string, TrafficTotal>>();
        private readonly EventCallback eventCallback;
        private readonly BufferCallback bufferCallback;
        private readonly string name;
        private readonly Guid sessionId = Guid.NewGuid();
        private long lastPruned;
        private long dateAtSecond;
        private string dateAt;
        private ulong controller;
        private Thread worker;
        private volatile bool stopped;
        private volatile string problem;
        private volatile bool missingEvents;

        public bool HasFailed { get { return problem != null; } }

        public string Status
        {
            get
            {
                if (problem != null) return problem;
                if (missingEvents) return "Windows пропустила часть событий. Итоги могут быть ниже фактических.";
                return "Учёт включён · TCP и UDP · средняя скорость за 5 секунд";
            }
        }

        public TrafficMeter(string userSid)
        {
            name = "InternetAgain-Network-" + userSid;
            eventCallback = OnEvent;
            bufferCallback = OnBuffer;
            // The caller holds the per-user mutex, so a session with this name is orphaned.
            uint result = WithProperties(p => StartTraceW(out controller, name, p));
            if (result == 183)
            {
                WithProperties(p => ControlTraceW(0, name, p, StopTrace));
                result = WithProperties(p => StartTraceW(out controller, name, p));
            }
            if (result != 0) throw new Win32Exception((int)result);
            try
            {
                worker = new Thread(Consume) { IsBackground = true, Name = "Network ETW" };
                worker.Start();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private uint WithProperties(Func<IntPtr, uint> action)
        {
            const int propertiesSize = 120;
            byte[] data = new byte[propertiesSize + (name.Length + 1) * 2];
            var p = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, p, data.Length);
                Marshal.WriteInt32(p, 0, data.Length); // Wnode.BufferSize
                Marshal.Copy(sessionId.ToByteArray(), 0, IntPtr.Add(p, 24), 16);
                Marshal.WriteInt32(p, 40, 1); // Wnode.ClientContext: QPC
                Marshal.WriteInt32(p, 44, 0x20000); // WNODE_FLAG_TRACED_GUID
                Marshal.WriteInt32(p, 48, 64); // Buffer size in KiB
                Marshal.WriteInt32(p, 56, 256); // Max. buffers
                Marshal.WriteInt32(p, 64, (int)(TraceSystemLoggerMode | TraceRealTimeMode));
                Marshal.WriteInt32(p, 68, 1); // Flush each second
                Marshal.WriteInt32(p, 72, (int)TraceNetworkTcpIp);
                Marshal.WriteInt32(p, 116, propertiesSize);
                return action(p);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // The app and these interop layouts target 64-bit Windows.
        [StructLayout(LayoutKind.Explicit, Size = 448)]
        private struct TraceLogfile
        {
            [FieldOffset(8)] public IntPtr LoggerName;
            [FieldOffset(28)] public uint Mode;
            [FieldOffset(400)] public IntPtr BufferCallback;
            [FieldOffset(424)] public IntPtr EventRecordCallback;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void EventCallback(IntPtr record);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint BufferCallback(IntPtr logfile);

        private void Consume()
        {
            IntPtr sessionName = Marshal.StringToHGlobalUni(name);
            try
            {
                var log = new TraceLogfile
                {
                    LoggerName = sessionName,
                    Mode = ProcessTraceEventRecord | ProcessTraceRealTime,
                    BufferCallback = Marshal.GetFunctionPointerForDelegate(bufferCallback),
                    EventRecordCallback = Marshal.GetFunctionPointerForDelegate(eventCallback)
                };
                ulong consumer = OpenTraceW(ref log);
                if (consumer == ulong.MaxValue)
                {
                    var error = new Win32Exception(Marshal.GetLastWin32Error());
                    if (!stopped) problem = "Не удалось открыть поток ETW. " + error.Message;
                    return;
                }
                try
                {
                    uint status = ProcessTrace(new[] { consumer }, 1, IntPtr.Zero, IntPtr.Zero);
                    if (!stopped) problem = status == 0 ? "Windows завершила сбор трафика."
                        : "Сбор трафика завершился с ошибкой. " + new Win32Exception((int)status).Message;
                }
                finally { CloseTrace(consumer); }
            }
            finally { Marshal.FreeHGlobal(sessionName); }
        }

        private uint OnBuffer(IntPtr logfile)
        {
            if (Marshal.ReadInt32(logfile, 416) != 0) missingEvents = true;
            return 1;
        }

        [StructLayout(LayoutKind.Explicit, Size = 112)]
        private struct NetworkEvent
        {
            [FieldOffset(16)] public long Stamp;
            [FieldOffset(24)] public Guid Provider;
            [FieldOffset(42)] public byte Version;
            [FieldOffset(45)] public byte Opcode;
            [FieldOffset(86)] public ushort Length;
            [FieldOffset(96)] public IntPtr Data;
        }

        private unsafe void OnEvent(IntPtr record)
        {
            var data = (NetworkEvent*)record;
            if (data->Version < 1 || data->Version > 2 || data->Length < 8 || data->Data == IntPtr.Zero) return;
            if (data->Provider != TcpProvider && data->Provider != UdpProvider) return;
            bool sent = data->Opcode == 10 || data->Opcode == 26;
            if (!sent && data->Opcode != 11 && data->Opcode != 27) return;
            try
            {
                int pid = Marshal.ReadInt32(data->Data);
                uint length = unchecked((uint)Marshal.ReadInt32(data->Data, 4));
                if (length == 0 || pid < 0) return;
                // ProcessTrace converts timestamps to system FILETIME unless RAW_TIMESTAMP is requested.
                DateTime time = DateTime.FromFileTimeUtc(data->Stamp);
                long second = time.Ticks / TimeSpan.TicksPerSecond;
                long clock = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                string program = Identify(pid, clock, data->Stamp);
                if (dateAtSecond != second || dateAt == null)
                {
                    dateAtSecond = second;
                    dateAt = StateFile.LocalDay(time);
                }
                lock (gate)
                {
                    Add(FindBucket(pending, dateAt), program, length, sent);
                    if (second >= clock - RateSeconds && second <= clock + 1)
                        Add(FindBucket(recent, second), program, length, sent);
                    if (clock != lastPruned)
                    {
                        lastPruned = clock;
                        foreach (long key in new List<long>(recent.Keys))
                            if (key < clock - RateSeconds || key > clock + 1) recent.Remove(key);
                    }
                }
            }
            catch (Exception)
            {
                missingEvents = true;
            }
        }

        private sealed class ProcessIdentity
        {
            public string Name;
            public long ObservedAt, StartedAt;
        }

        private string Identify(int pid, long now, long eventStamp)
        {
            if (pid == 0 || pid == 4) return "Система";
            string unknown = "Неопределённый процесс (PID " + pid.ToString(CultureInfo.InvariantCulture) + ")";
            ProcessIdentity identity;
            if (!processes.TryGetValue(pid, out identity) || now - identity.ObservedAt >= 2 || now < identity.ObservedAt)
            {
                if (processes.Count >= 4096) processes.Clear();
                identity = new ProcessIdentity { Name = unknown, ObservedAt = now };
                var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                if (process != IntPtr.Zero)
                {
                    try
                    {
                        var path = new StringBuilder(1024);
                        int characters = path.Capacity;
                        long start, exit, kernel, user;
                        if (GetProcessTimes(process, out start, out exit, out kernel, out user) &&
                            QueryFullProcessImageNameW(process, 0, path, ref characters))
                        {
                            identity.StartedAt = start;
                            identity.Name = path.ToString();
                        }
                    }
                    finally { CloseHandle(process); }
                }
                processes[pid] = identity;
            }
            return identity.StartedAt > eventStamp ? unknown : identity.Name;
        }

        private static Dictionary<string, TrafficTotal> FindBucket<TKey>(
            Dictionary<TKey, Dictionary<string, TrafficTotal>> buckets, TKey key)
        {
            Dictionary<string, TrafficTotal> bucket;
            if (!buckets.TryGetValue(key, out bucket))
            {
                bucket = new Dictionary<string, TrafficTotal>(StringComparer.OrdinalIgnoreCase);
                buckets.Add(key, bucket);
            }
            return bucket;
        }

        private static void Add(Dictionary<string, TrafficTotal> bucket, string program, long length, bool sent)
        {
            TrafficTotal total;
            if (!bucket.TryGetValue(program, out total))
            {
                total = new TrafficTotal();
                bucket.Add(program, total);
            }
            if (sent) total.Sent += length;
            else total.Received += length;
        }

        public Dictionary<string, TrafficTotal> Drain(string today, DateTime now, AppState state, out bool changed)
        {
            var rates = new Dictionary<string, TrafficTotal>(StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                changed = false;
                Dictionary<string, TrafficTotal> newTotals;
                if (pending.TryGetValue(today, out newTotals))
                {
                    Merge(state.Traffic, newTotals);
                    changed = true;
                }
                pending.Clear();
                long second = now.Ticks / TimeSpan.TicksPerSecond;
                for (long time = second - RateSeconds + 1; time <= second; time++)
                {
                    Dictionary<string, TrafficTotal> bucket;
                    if (recent.TryGetValue(time, out bucket)) Merge(rates, bucket);
                }
            }
            return rates;
        }

        private static void Merge(Dictionary<string, TrafficTotal> target, Dictionary<string, TrafficTotal> input)
        {
            foreach (var item in input)
            {
                Add(target, item.Key, item.Value.Received, false);
                Add(target, item.Key, item.Value.Sent, true);
            }
        }

        public void Dispose()
        {
            if (stopped) return;
            stopped = true;
            WithProperties(p => ControlTraceW(controller, name, p, StopTrace));
            if (worker != null && worker.IsAlive) worker.Join(2000);
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint StartTraceW(out ulong handle, string name, IntPtr properties);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint ControlTraceW(ulong handle, string name, IntPtr properties, uint controlCode);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern ulong OpenTraceW(ref TraceLogfile logfile);
        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(ulong handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long create, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
