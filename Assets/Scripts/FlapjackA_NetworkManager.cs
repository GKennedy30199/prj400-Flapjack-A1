using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.IO;




public class FlapjackA_NetworkManager : MonoBehaviour
{
    private readonly Queue<Action> _mainThread = new Queue<Action>();

    private void RunOnMainThread(Action a)
    {
        lock (_mainThread) _mainThread.Enqueue(a);
    }
    public enum AppState
    {
        Login,        // not connected
        Connecting,   // trying to connect / waiting
        Connected,    // TCP connected but not authed yet
        Ready         // authenticated
    }

    

    [Header("Auth UI")]
    public TMP_InputField passwordInput;
   

    private TcpClient tcp;
    private NetworkStream stream;
    private Thread tcpReadThread;
    

    private volatile bool connected;       // TCP socket is connected
    private volatile bool authenticated;   // server accepted auth
    private volatile bool shouldRun;
    private volatile bool hasPendingState;
    private AppState pendingState;
    private string pendingStatus;
 

    private readonly object sendLock = new object();

    public bool IsConnected => connected;
    public bool IsAuthenticated => authenticated;

    
    public bool IsReady => connected && authenticated;

    [SerializeField] private bool editorForceLocalhost = true;

    [SerializeField] private GameObject loginPanel;
    [SerializeField] private GameObject menuPanel;

    [SerializeField] private TMPro.TMP_Text statusText;

    // track state
    private volatile AppState state = AppState.Login;
    public AppState State => state;

    private int counterIndex = 0;

    public Texture2D testPlaymat;

    //public InputField passwordInput;
   // private volatile bool authenticated;

    [Header("Ports")]
    public int udpPort = 47777;
    public int tcpPortFallback = 48888;

    [Header("Discovery")]
    public float discoverySeconds = 4f;

    [Header("UI")]
    //public Text statusText;
    public Button retryButton;
    public Button connectButton;

    private volatile bool discovering;
    //private volatile bool connected;

    private string connectedIp;
   // private TcpClient tcp;
   // private NetworkStream stream;

    private Thread discoverThread;
    // private Thread tcpReadThread;


    //Counters data
    [Header("Counter Data")]
    public List<CounterData> counters = new List<CounterData>();
    public CounterData selectedCounter;

    [Header("Counter UI")]
    public GameObject counterListPanel;
    public GameObject counterActionPanel;
    public GameObject counterMovePanel;
    public GameObject counterNamePanel;
    public Transform counterListContainer;
    public GameObject counterListButtonPrefab;
    public TMP_InputField counterNameInput;

    private byte[] pendingCounterImageBytes;

    void Start()
    {
        SetState(AppState.Login, "Enter pairing code and connect.");

        retryButton.onClick.AddListener(RetryDiscovery);

        RetryDiscovery();
    }

    void OnDestroy()
    {
        ShutdownNetworking();
    }

    private void Update()
    {
        int ran = 0;
        while (true)
        {
            Action a = null;
            lock (_mainThread)
            {
                if (_mainThread.Count == 0) break;
                a = _mainThread.Dequeue();
            }
            a?.Invoke();
            ran++;
        }
        if (ran > 0) Debug.Log("UI actions ran: " + ran);
    }


    public void RetryDiscovery()
    {
        SetState(AppState.Connecting, "Searching for Flapjack B...");

        if (connected) ShutdownNetworking();

        if (discoverThread != null && discoverThread.IsAlive)
            return;

        discovering = true;
        discoverThread = new Thread(DiscoveryWorker) { IsBackground = true };
        discoverThread.Start();
    }

    private void DiscoveryWorker()
    {
        UdpClient udp = null;
        try
        {
            udp = new UdpClient();
            udp.EnableBroadcast = true;
            udp.Client.ReceiveTimeout = 500;

            var bcastIp = NetUtil.GetSubnetBroadcastAddress();
            var broadcast = new IPEndPoint(bcastIp, udpPort);

            byte[] discover = Encoding.UTF8.GetBytes("FLAPJACK_DISCOVER");

            DateTime end = DateTime.UtcNow.AddSeconds(discoverySeconds);

            while (!connected && DateTime.UtcNow < end)
            {
                udp.Send(discover, discover.Length, broadcast);

                var remote = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    byte[] resp = udp.Receive(ref remote);
                    string msg = Encoding.UTF8.GetString(resp).Trim();

                    if (msg.StartsWith("FLAPJACK_HERE:"))
                    {
                        // FLAPJACK_HERE:<name>:<port>
                        var parts = msg.Split(':');
                        int tcpPort = (parts.Length >= 3 && int.TryParse(parts[2], out var p))
                            ? p
                            : tcpPortFallback;

                        connectedIp = remote.Address.ToString();
                        discovering = false;

                        ConnectToResolvedTarget(connectedIp, tcpPort);

                        return;
                    }
                }
                catch (SocketException)
                {
                    // timeout, loop again
                }

                Thread.Sleep(250);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Discovery error: " + e);
        }
        finally
        {
            try { udp?.Close(); } catch { }
            discovering = false;
        }
        //Debug.Log($"TRY TCP -> {connectedIp}:{tcpPort}");
    }

    private void ConnectTcp(string ip, int port)
    {

        // Clean reset before connecting
        DisconnectInternal();

        try
        {
            tcp = new TcpClient();
            tcp.NoDelay = true; // good for small messages
            tcp.Connect(ip, port);

            stream = tcp.GetStream();
            connected = true;
            SetState(AppState.Connected, "Connected. Authenticating...");

            authenticated = false;
            shouldRun = true;

            Debug.Log($"✅ TCP connected to {ip}:{port}");

            // Start the reader thread FIRST so we can receive auth_ok/auth_fail
            tcpReadThread = new Thread(TcpReadWorker) { IsBackground = true };
            tcpReadThread.Start();

            // Send auth right after connection
            string pw = SafePassword();
            Debug.Log($"AUTH sending password='{pw}' len={pw.Length}");
            SendLineSafe($"{{\"type\":\"auth\",\"password\":\"{EscapeJson(pw)}\"}}");

            Debug.Log("➡️ Sent auth message");
        }
        catch (Exception e)
        {
            Debug.LogError($"❌ TCP connect failed: {e}");
            DisconnectInternal();
        }
    }

    private void ConnectToResolvedTarget(string ip, int port)
    {
#if UNITY_EDITOR
    Debug.Log("EDITOR: forcing localhost");
    ip = "127.0.0.1";
#endif
        ConnectTcp(ip, port);
    }


    private void TcpReadWorker()
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();

        try
        {
            while (shouldRun && tcp != null && tcp.Connected)
            {
                int read = stream.Read(buf, 0, buf.Length);
                if (read <= 0) break;

                sb.Append(Encoding.UTF8.GetString(buf, 0, read));

                while (true)
                {
                    int idx = sb.ToString().IndexOf('\n');
                    if (idx < 0) break;

                    string line = sb.ToString(0, idx).Trim('\r');
                    sb.Remove(0, idx + 1);

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    Debug.Log("⬅️ TCP line: " + line);
                    HandleServerLine(line);
                }
            }
        }
        catch (Exception e)
        {
            Debug.Log("TCP read ended: " + e.Message);
        }

        // If we exit loop, connection ended
        Debug.Log("🔌 TCP disconnected.");
        DisconnectInternal();
    }

    private void HandleServerLine(string line)
    {
        if (line.Contains("\"type\":\"auth_ok\""))
        {
            authenticated = true;
            SetState(AppState.Ready, "Connected ✅");
            return;
        }

        if (line.Contains("\"type\":\"auth_fail\""))
        {
            authenticated = false;
            SetState(AppState.Login, "Wrong code. Try again.");
            DisconnectInternal();
            return;
        }

        if (line.Contains("not_authenticated"))
        {
            Debug.LogWarning("Server says not_authenticated. Resending auth...");
            string pw = SafePassword();
            SendLineSafe($"{{\"type\":\"auth\",\"password\":\"{EscapeJson(pw)}\"}}");
            return;
        }
    }


    public void SendLineSafe(string line)
    {
        if (!connected || stream == null) return;

        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (sendLock)
        {
            try
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e)
            {
                Debug.LogError("Send failed: " + e.Message);
                DisconnectInternal();
            }
        }
    }


    private void ShutdownNetworking()
    {
        discovering = false;
        connected = false;

        try { stream?.Close(); } catch { }
        try { tcp?.Close(); } catch { }

        stream = null;
        tcp = null;
    }


    public void SendTestPlaymat()
    {
        if (!IsReady)
        {
            Debug.LogWarning("Not ready (not connected/authenticated).");
            return;
        }
        if (testPlaymat == null)
        {
            Debug.LogError("testPlaymat not assigned.");
            return;
        }

        try
        {
            byte[] png = testPlaymat.EncodeToPNG();
            string header = $"{{\"type\":\"playmat_begin\",\"size\":{png.Length}}}";

            // Header line
            SendLineSafe(header);

            // Raw bytes (binary) — must write directly, not as line
            lock (sendLock)
            {
                stream.Write(png, 0, png.Length);
            }

            Debug.Log($"✅ Sent playmat bytes: {png.Length}");
        }
        catch (Exception e)
        {
            Debug.LogError("SendTestPlaymat failed: " + e);
            DisconnectInternal();
        }
    }

    public void Disconnect()
    {
        DisconnectInternal();
    }

    private void DisconnectInternal()
    {
        shouldRun = false;
        authenticated = false;
        connected = false;

        try { stream?.Close(); } catch { }
        try { tcp?.Close(); } catch { }

        stream = null;
        tcp = null;
        SetState(AppState.Login, "Disconnected. Enter code and retry.");

    }

    private void SetState(AppState newState, string status = null)
    {
        // Safe to call from ANY thread
        RunOnMainThread(() => SetState_Internal(newState, status));
    }

    private void SetState_Internal(AppState newState, string status = null)
    {
        state = newState;
        bool ready = (newState == AppState.Ready);

        if (loginPanel != null) loginPanel.SetActive(!ready);
        if (menuPanel != null) menuPanel.SetActive(ready);

        if (statusText != null && status != null)
            statusText.text = status;
    }

    //Counter Methods
    public void OpenCounterListMenu()
    {
        if (counterListPanel != null) counterListPanel.SetActive(true);
        if (counterActionPanel != null) counterActionPanel.SetActive(false);
        if (counterMovePanel != null) counterMovePanel.SetActive(false);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);

        RefreshCounterListUI();
    }

    public void OpenCounterActionMenu(CounterData counter)
    {
        selectedCounter = counter;

        if (counterListPanel != null) counterListPanel.SetActive(false);
        if (counterActionPanel != null) counterActionPanel.SetActive(true);
        if (counterMovePanel != null) counterMovePanel.SetActive(false);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);
    }

    public void OpenCounterMoveMenu()
    {
        if (selectedCounter == null) return;

        if (counterListPanel != null) counterListPanel.SetActive(false);
        if (counterActionPanel != null) counterActionPanel.SetActive(false);
        if (counterMovePanel != null) counterMovePanel.SetActive(true);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);
    }

    public void BackToCounterList()
    {
        OpenCounterListMenu();
    }

    public void BackToMainMenu()
    {
        if (counterListPanel != null) counterListPanel.SetActive(false);
        if (counterActionPanel != null) counterActionPanel.SetActive(false);
        if (counterMovePanel != null) counterMovePanel.SetActive(false);
        if (counterNamePanel != null) counterNamePanel.SetActive(false);
    }

    public void BeginAddCounter()
    {
        if (!IsReady)
        {
            Debug.LogWarning("Not ready (connect + auth first).");
            return;
        }

        if (!NativeFilePicker.CheckPermission())
        {
            Debug.LogError("Storage permission not granted.");
            return;
        }

        NativeFilePicker.PickFile((path) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.Log("User cancelled.");
                return;
            }

            try
            {
                pendingCounterImageBytes = File.ReadAllBytes(path);

                RunOnMainThread(() =>
                {
                    if (counterNameInput != null)
                        counterNameInput.text = "";

                    if (counterListPanel != null) counterListPanel.SetActive(false);
                    if (counterActionPanel != null) counterActionPanel.SetActive(false);
                    if (counterMovePanel != null) counterMovePanel.SetActive(false);
                    if (counterNamePanel != null) counterNamePanel.SetActive(true);
                });

                Debug.Log("Counter image selected. Waiting for name.");
            }
            catch (System.Exception e)
            {
                Debug.LogError("Failed to read file: " + e);
            }
        }, new string[] { "image/png" });
    }

    public void ConfirmCreateCounter()
    {
        if (!IsReady) return;

        if (pendingCounterImageBytes == null || pendingCounterImageBytes.Length == 0)
        {
            Debug.LogWarning("No counter image selected.");
            return;
        }

        string counterName = (counterNameInput != null) ? counterNameInput.text.Trim() : "";

        if (string.IsNullOrEmpty(counterName))
        {
            Debug.LogWarning("Counter name is empty.");
            return;
        }

        counterIndex++;
        string id = $"c{counterIndex}";

        CounterData newCounter = new CounterData
        {
            id = id,
            displayName = counterName,
            x = 0.5f,
            y = 0.5f
        };

        counters.Add(newCounter);
        selectedCounter = newCounter;

        SendCounterPngBytesWithId(id, pendingCounterImageBytes, newCounter.x, newCounter.y, 0.10f);

        pendingCounterImageBytes = null;

        if (counterNameInput != null)
            counterNameInput.text = "";

        RefreshCounterListUI();
        OpenCounterListMenu();
    }
    public void CancelCreateCounter()
    {
        pendingCounterImageBytes = null;

        if (counterNameInput != null)
            counterNameInput.text = "";

        OpenCounterListMenu();
    }
    public void SendCounterPngBytesWithId(string id, byte[] pngBytes, float x = 0.5f, float y = 0.5f, float size = 0.10f)
    {
        if (!IsReady || pngBytes == null || pngBytes.Length == 0) return;

        string header =
            $"{{\"type\":\"counter_begin\",\"id\":\"{id}\",\"x\":{x},\"y\":{y},\"size\":{size},\"bytes\":{pngBytes.Length}}}";

        SendLineSafe(header);

        lock (sendLock)
        {
            stream.Write(pngBytes, 0, pngBytes.Length);
        }

        Debug.Log($"✅ Sent counter {id} bytes={pngBytes.Length}");
    }
    public void SendCounterPngBytes(byte[] pngBytes, float x = 0.5f, float y = 0.5f, float size = 0.10f)
    {
        if (!IsReady || pngBytes == null || pngBytes.Length == 0) return;

        counterIndex++;
        string id = $"c{counterIndex}";

        SendCounterPngBytesWithId(id, pngBytes, x, y, size);
    }

    public void RefreshCounterListUI()
    {
        if (counterListContainer == null || counterListButtonPrefab == null) return;

        // Remove old buttons
        for (int i = counterListContainer.childCount - 1; i >= 0; i--)
        {
            Destroy(counterListContainer.GetChild(i).gameObject);
        }

        // Rebuild buttons for each counter
        foreach (var counter in counters)
        {
            GameObject btnObj = Instantiate(counterListButtonPrefab, counterListContainer);

            TMP_Text label = btnObj.GetComponentInChildren<TMP_Text>();
            if (label != null)
                label.text = counter.displayName;

            Button btn = btnObj.GetComponent<Button>();
            if (btn != null)
            {
                CounterData captured = counter;
                btn.onClick.AddListener(() => OpenCounterActionMenu(captured));
            }
        }
    }

    public string SafePassword()
    {
        // Never crash if passwordInput isn't wired
        if (passwordInput == null) return "";
        return passwordInput ? (passwordInput.text ?? "").Trim() : "";
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
    public void OnDisconnectClicked()
    {
        DisconnectInternal();
        // DisconnectInternal will push you back to Login state
    }


}
public static class NetUtil
{
    public static IPAddress GetSubnetBroadcastAddress()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var ipProps = ni.GetIPProperties();
            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (mask == null) continue;

                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    broadcast[i] = (byte)(ip[i] | (mask[i] ^ 255));

                return new IPAddress(broadcast);
            }
        }

        return IPAddress.Broadcast; // fallback
    }
}

public class AndroidMulticastLock : MonoBehaviour
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject multicastLock;
#endif

    void Awake()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var context = activity.Call<AndroidJavaObject>("getApplicationContext"))
            using (var wifiManager = context.Call<AndroidJavaObject>("getSystemService", "wifi"))
            {
                multicastLock = wifiManager.Call<AndroidJavaObject>("createMulticastLock", "flapjack_lock");
                multicastLock.Call("setReferenceCounted", true);
                multicastLock.Call("acquire");
                Debug.Log("MulticastLock acquired");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Failed to acquire MulticastLock: " + e.Message);
        }
#endif
    }

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (multicastLock != null)
                multicastLock.Call("release");
        }
        catch { }
#endif
    }
}



