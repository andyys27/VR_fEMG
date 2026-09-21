using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Registra eventos del experimento en un CSV con timestamps en nanosegundos y,
/// opcionalmente, los envía por UDP al proceso Python que adquiere el EMG
/// (marker_server.py) para tener estímulo y EMG en el mismo reloj.
///
/// USO
///   1. Pon este componente en UN GameObject de la escena (p. ej. "Logger").
///   2. Desde cualquier script:  ExperimentLogger.Log("evento", "detalle");
///
/// CSV: t_unix_ns, t_iso_utc, t_mono_ns, unity_time_s, frame, event, detail, offset_ns
///   t_unix_ns  ns desde 1970 UTC según el reloj de ESTA computadora.
///   offset_ns  (reloj Python) - (reloj Unity), mejor estimación hasta ese momento.
///              Tiempo del evento en el reloj de Python = t_unix_ns + offset_ns.
///              Vacío si no hay receptor Python o aún no respondió.
///
/// PROTOCOLO UDP (texto UTF-8, campos separados por TAB)
///   Unity -> Python : PING  seq  t1_unity_ns
///   Python -> Unity : PONG  seq  t1  t2_rx_ns  t3_tx_ns
///   Unity -> Python : EVT   seq  t_unity_ns  offset_ns|NA  label  detail
/// </summary>
[DefaultExecutionOrder(-1000)]
public class ExperimentLogger : MonoBehaviour
{
    public static ExperimentLogger Instance { get; private set; }

    [Header("CSV")]
    [Tooltip("Vacío = Application.persistentDataPath (Linux: ~/.config/unity3d/<Company>/<Product>/)")]
    public string outputFolder = "";
    public string filePrefix = "session";
    [Tooltip("Cada cuántos segundos se fuerza el volcado a disco.")]
    public float flushInterval = 1f;
    [Tooltip("Una fila por frame (útil para medir jitter de render).")]
    public bool logEveryFrame = false;

    [Header("UDP hacia el PC del EMG (Python)")]
    public bool sendUdp = true;
    [Tooltip("IP del equipo que corre marker_server.py. Mismo equipo: 127.0.0.1")]
    public string udpHost = "127.0.0.1";
    public int udpPort = 5005;
    [Tooltip("Pings por ráfaga de sincronización de relojes.")]
    public int syncBurst = 20;
    [Tooltip("Segundos entre ráfagas (0 = solo una al inicio).")]
    public float syncIntervalSeconds = 30f;

    public string FilePath { get; private set; }

    static readonly double NsPerTick = 1e9 / Stopwatch.Frequency;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static bool _warnedNoInstance;

    StreamWriter _writer;
    Stopwatch _sw;
    long _anchorUnixNs;                  // Unix ns correspondiente a Stopwatch = 0
    readonly object _lock = new object();
    float _lastFlush;

    // Copias cacheadas: las APIs de Time solo se pueden leer en el hilo principal.
    volatile int _frame;
    double _unityTime;

    // ---- UDP / sincronización ----
    UdpClient _udp;
    Thread _rxThread, _syncThread;
    volatile bool _running;
    long _evtSeq, _pingSeq;
    readonly object _syncLock = new object();
    readonly Queue<long[]> _samples = new Queue<long[]>();   // {rtt_ns, offset_ns}
    bool _hasOffset;
    long _offsetNs;

    // ------------------------------------------------------------------ API pública

    /// <summary>Registra un evento con la hora actual (llamar desde el hilo principal).</summary>
    public static void Log(string evt, string detail = "")
    {
        if (Instance == null)
        {
            if (!_warnedNoInstance)
            {
                _warnedNoInstance = true;
                Debug.LogWarning("[ExperimentLogger] No hay instancia en la escena; los eventos se ignoran.");
            }
            return;
        }
        Instance.Write(evt, detail, true);
    }

    /// <summary>Hora actual en ns Unix (UTC) del reloj de esta computadora.</summary>
    public static long NowUnixNs()
    {
        return Instance != null ? Instance.UnixNsNow() : 0;
    }

    // ------------------------------------------------------------------ Ciclo de vida

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Ancla: relaciona el reloj de pared (UtcNow) con el Stopwatch monotónico.
        _sw = Stopwatch.StartNew();
        long t0 = _sw.ElapsedTicks;
        DateTime utc = DateTime.UtcNow;
        long t1 = _sw.ElapsedTicks;
        long midTicks = (t0 + t1) / 2;
        long utcNs = (utc.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
        _anchorUnixNs = utcNs - (long)(midTicks * NsPerTick);

        string folder = string.IsNullOrWhiteSpace(outputFolder) ? Application.persistentDataPath : outputFolder;
        Directory.CreateDirectory(folder);
        string name = filePrefix + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) + ".csv";
        FilePath = Path.Combine(folder, name);

        _writer = new StreamWriter(FilePath, false, new UTF8Encoding(false));
        _writer.WriteLine("t_unix_ns,t_iso_utc,t_mono_ns,unity_time_s,frame,event,detail,offset_ns");

        _frame = Time.frameCount;
        _unityTime = Time.realtimeSinceStartupAsDouble;

        if (sendUdp) StartUdp();

        Write("session_start",
              "anchor_unix_ns=" + _anchorUnixNs.ToString(Inv) +
              ";stopwatch_hz=" + Stopwatch.Frequency.ToString(Inv) +
              ";unity=" + Application.unityVersion +
              ";udp=" + (_udp != null ? udpHost + ":" + udpPort : "off"),
              true);

        Debug.Log("[ExperimentLogger] Guardando en: " + FilePath);
    }

    void Update()
    {
        _frame = Time.frameCount;
        _unityTime = Time.realtimeSinceStartupAsDouble;

        if (logEveryFrame) Write("frame", "", false);

        if (Time.unscaledTime - _lastFlush >= flushInterval)
        {
            _lastFlush = Time.unscaledTime;
            lock (_lock) { if (_writer != null) _writer.Flush(); }
        }
    }

    void OnApplicationPause(bool paused)
    {
        if (paused) lock (_lock) { if (_writer != null) _writer.Flush(); }
    }

    void OnApplicationQuit() { Close(); }

    void OnDestroy()
    {
        if (Instance == this) { Close(); Instance = null; }
    }

    // ------------------------------------------------------------------ Escritura

    long MonoNs() { return (long)(_sw.ElapsedTicks * NsPerTick); }

    long UnixNsNow() { return _anchorUnixNs + MonoNs(); }

    static string L(long v) { return v.ToString(Inv); }

    void Write(string evt, string detail, bool alsoUdp)
    {
        long mono = MonoNs();
        long unix = _anchorUnixNs + mono;
        long off;
        bool has = TryGetOffset(out off);

        string line = string.Concat(
            L(unix), ",",
            IsoFromUnixNs(unix), ",",
            L(mono), ",",
            _unityTime.ToString("F6", Inv), ",",
            _frame.ToString(Inv), ",",
            Csv(evt), ",",
            Csv(detail), ",",
            has ? L(off) : "");

        lock (_lock)
        {
            if (_writer != null) _writer.WriteLine(line);
        }

        if (alsoUdp && _udp != null)
        {
            SendRaw("EVT\t" + L(Interlocked.Increment(ref _evtSeq)) + "\t" + L(unix) + "\t" +
                    (has ? L(off) : "NA") + "\t" + Clean(evt) + "\t" + Clean(detail));
        }
    }

    void Close()
    {
        if (_writer == null) return;

        Write("session_end", "", true);

        _running = false;
        UdpClient u = _udp;
        _udp = null;
        if (u != null) { try { u.Close(); } catch (Exception) { } }

        lock (_lock)
        {
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    // ------------------------------------------------------------------ UDP

    void StartUdp()
    {
        try
        {
            IPAddress ip;
            if (!IPAddress.TryParse(udpHost, out ip))
            {
                ip = null;
                foreach (IPAddress a in Dns.GetHostAddresses(udpHost))
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                }
                if (ip == null) throw new Exception("sin dirección IPv4 para " + udpHost);
            }

            _udp = new UdpClient(ip.AddressFamily);
            _udp.Connect(ip, udpPort);
            _running = true;

            _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "ExpLogger-UDP-RX" };
            _rxThread.Start();
            _syncThread = new Thread(SyncLoop) { IsBackground = true, Name = "ExpLogger-UDP-SYNC" };
            _syncThread.Start();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ExperimentLogger] UDP desactivado: " + e.Message);
            _running = false;
            if (_udp != null) { try { _udp.Close(); } catch (Exception) { } }
            _udp = null;
        }
    }

    void SendRaw(string s)
    {
        UdpClient u = _udp;
        if (u == null) return;
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            u.Send(b, b.Length);
        }
        catch (Exception) { /* nunca romper el experimento por un fallo de red */ }
    }

    void SyncLoop()
    {
        Thread.Sleep(200);
        while (_running)
        {
            for (int i = 0; i < syncBurst && _running; i++)
            {
                SendRaw("PING\t" + L(Interlocked.Increment(ref _pingSeq)) + "\t" + L(UnixNsNow()));
                Thread.Sleep(50);
            }
            if (syncIntervalSeconds <= 0f) break;
            int waitMs = (int)(syncIntervalSeconds * 1000f);
            for (int t = 0; t < waitMs && _running; t += 100) Thread.Sleep(100);
        }
    }

    void RxLoop()
    {
        UdpClient u = _udp;
        if (u == null) return;
        IPEndPoint any = new IPEndPoint(
            u.Client.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = u.Receive(ref any);
                long t4 = UnixNsNow();                          // llegada del PONG
                HandleDatagram(Encoding.UTF8.GetString(data), t4);
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception) { if (_running) Thread.Sleep(20); }
        }
    }

    void HandleDatagram(string msg, long t4)
    {
        string[] p = msg.TrimEnd('\r', '\n').Split('\t');
        if (p.Length < 5 || p[0] != "PONG") return;

        long seq, t1, t2, t3;
        if (!long.TryParse(p[1], NumberStyles.Integer, Inv, out seq)) return;
        if (!long.TryParse(p[2], NumberStyles.Integer, Inv, out t1)) return;
        if (!long.TryParse(p[3], NumberStyles.Integer, Inv, out t2)) return;
        if (!long.TryParse(p[4], NumberStyles.Integer, Inv, out t3)) return;

        // Estilo NTP:  reloj_python = reloj_unity + offset
        long rtt = (t4 - t1) - (t3 - t2);
        long offset = ((t2 - t1) + (t3 - t4)) / 2;
        if (rtt < 0) return;

        long bestRtt = long.MaxValue, bestOff = 0;
        lock (_syncLock)
        {
            _samples.Enqueue(new long[] { rtt, offset });
            while (_samples.Count > 40) _samples.Dequeue();
            foreach (long[] s in _samples)
            {
                if (s[0] < bestRtt) { bestRtt = s[0]; bestOff = s[1]; }   // el de menor RTT es el más fiable
            }
            _offsetNs = bestOff;
            _hasOffset = true;
        }

        Write("sync",
              "seq=" + L(seq) + ";rtt_ns=" + L(rtt) + ";offset_ns=" + L(offset) + ";best_rtt_ns=" + L(bestRtt),
              false);
    }

    bool TryGetOffset(out long off)
    {
        lock (_syncLock)
        {
            off = _offsetNs;
            return _hasOffset;
        }
    }

    // ------------------------------------------------------------------ Utilidades

    static string IsoFromUnixNs(long unixNs)
    {
        long sec = unixNs / 1000000000L;
        long frac = unixNs % 1000000000L;
        DateTime dt = DateTime.UnixEpoch.AddSeconds(sec);
        return dt.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + "." + frac.ToString("D9", Inv) + "Z";
    }

    static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }
}